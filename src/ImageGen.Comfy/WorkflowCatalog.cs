using ImageGen.Application.Rendering;
using ImageGen.Application.Workflows;
using ImageGen.Domain;
using ImageGen.Domain.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace ImageGen.Comfy;

/// <summary>
/// The workflow catalog, loaded from <c>workflows.json</c> (the configurations) + <c>requirements.json</c> (the
/// model-file registry). A configuration binds a workflow class, supplies its parameter settings layer, soft-links
/// its requirements by id, and carries the decision-card/prompting metadata surfaced by /prompting and /workflows.
/// <para>Both files hot-reload when changed on disk. Failure is NOT silent at either point: the startup load throws
/// (a catalog that will not parse is a machine that offers no models, and booting into that state hides the reason),
/// and a failed hot-reload keeps the last-good catalog but says so at Error and does not retry the same broken
/// version.</para>
/// </summary>
public sealed class WorkflowCatalog
{
    private readonly string _workflowsDir;
    private readonly string _modelsDir;
    private readonly ILogger<WorkflowCatalog> _log;
    private readonly Lock _lock = new();
    private readonly Lock _reloadGate = new();
    /// <summary>The EFFECTIVE catalogue every reader sees: the file configs with this machine's DB variants folded in.
    /// Rebuilt from <see cref="_fileById"/>/<see cref="_fileAll"/> plus <see cref="_variants"/> on every load and edit.</summary>
    private Dictionary<string, WorkflowConfiguration> _byId = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The effective catalogue as a list (see <see cref="_byId"/>).</summary>
    private List<WorkflowConfiguration> _all = [];
    /// <summary>The pure FILE catalogue, kept apart from the effective one so a file reload can re-fold the retained
    /// variants back in rather than silently dropping them.</summary>
    private Dictionary<string, WorkflowConfiguration> _fileById = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The pure file catalogue as a list (see <see cref="_fileById"/>).</summary>
    private List<WorkflowConfiguration> _fileAll = [];
    /// <summary>This machine's DB-backed variants, retained across file reloads and folded into the effective catalogue.</summary>
    private IReadOnlyList<WorkflowConfiguration> _variants = [];
    /// <summary>The ids in <see cref="_variants"/>, for the "is this a deletable variant" test.</summary>
    private HashSet<string> _variantIds = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, Requirement> _reqById = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Slot id -> the file bound to it on this machine. Refreshed with the catalog and after a UI edit.</summary>
    private Dictionary<string, string> _bindings = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, Dictionary<string, JsonElement>> _paramOverrides = new(StringComparer.OrdinalIgnoreCase);
    private (DateTime Newest, int Count) _wfStamp, _modelStamp;
    /// <summary>The directory-stamp pair whose load threw, so the same broken version is reported once instead of
    /// on every catalog read until someone saves a file again.</summary>
    [AllowNullable("null = no broken catalog version recorded (no load has thrown); distinct from a default stamp pair")]
    private ((DateTime, int)? Wf, (DateTime, int)? Models)? _badVersion;

    /// <summary>The fixed tokens the loader spells out: the <c>*.json</c> glob it enumerates, the <c>param.</c>
    /// override-key prefix, the two catalog subdirectory names (also the section words its missing-directory errors
    /// name), and the entity words its duplicate-id errors name.</summary>
    private static class CatalogText
    {
        public const string JsonGlob = "*.json";
        public const string ParamPrefix = "param.";
        public const string ModelsSection = "models";
        public const string WorkflowsSection = "workflows";
        public const string ModelEntity = "model";
        public const string WorkflowEntity = "workflow";
    }

    /// <summary>The resolution block's member names, named when <see cref="BuildResolution"/> reports a missing one —
    /// kept in step with <see cref="ResolutionDto"/>'s <c>[JsonPropertyName]</c>s.</summary>
    private static class ResolutionMember
    {
        public const string MinW = "min_w";
        public const string MinH = "min_h";
        public const string MaxW = "max_w";
        public const string MaxH = "max_h";
        public const string Step = "step";
    }

    public WorkflowCatalog(ComfyOptions config, ILogger<WorkflowCatalog> log)
    {
        string root = config.CatalogPath;
        _workflowsDir = root.Length == 0 ? "" : Path.Combine(root, CatalogText.WorkflowsSection);
        _modelsDir = root.Length == 0 ? "" : Path.Combine(root, CatalogText.ModelsSection);
        _log = log;
        Load();   // startup: a catalog that will not parse fails the boot, naming the file and the parse error
    }

    /// <summary>
    /// Replaces the in-memory binding snapshot. Called after the catalog loads and again whenever a binding is
    /// written, so the sync <see cref="Resolve"/> path stays sync: resolving a slot to a filename happens on every
    /// submit, and going to the database for it there would put a query on the render path.
    /// </summary>
    public void SetBindings(IReadOnlyDictionary<string, string> bindings)
    {
        Dictionary<string, string> copy = new(bindings, StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            _bindings = copy;
        }
    }

    /// <summary>
    /// This machine's per-configuration parameter overrides — the render size for each aspect, the step count,
    /// anything else the configuration exposes. Same reasoning as <see cref="SetBindings"/>: held in memory so the
    /// sync merge on the submit path does not query the database.
    ///
    /// <para>Values arrive as the text the settings page stored. A value that parses as JSON is used as JSON (a
    /// number, or the aspect map's object); anything else is taken as a plain string, so a text parameter does not
    /// have to be quoted by hand in a form field.</para>
    /// </summary>
    public void SetParamOverrides(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> overrides)
    {
        Dictionary<string, Dictionary<string, JsonElement>> copy = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string? configId, IReadOnlyDictionary<string, string>? settings) in overrides)
        {
            Dictionary<string, JsonElement> parsed = new(StringComparer.OrdinalIgnoreCase);
            foreach ((string? key, string? raw) in settings)
            {
                if (!key.StartsWith(CatalogText.ParamPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                parsed[key[CatalogText.ParamPrefix.Length..]] = AsJson(raw);
            }

            if (parsed.Count > 0)
            {
                copy[configId] = parsed;
            }
        }

        lock (_lock)
        {
            _paramOverrides = copy;
        }
    }

    /// <summary>This machine's overrides for one configuration, or empty.</summary>
    public IReadOnlyDictionary<string, JsonElement> ParamOverridesFor(string configId)
    {
        lock (_lock)
        {
            return _paramOverrides.TryGetValue(configId, out Dictionary<string, JsonElement>? v)
                ? v
                : [];
        }
    }

    private static JsonElement AsJson(string raw)
    {
        try
        {
            return JsonDocument.Parse(raw).RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonDocument.Parse(JsonSerializer.Serialize(raw)).RootElement.Clone();
        }
    }

    /// <summary>Configuration by its (unique) id, or null. Resolution is by id, then a loose contains match so a
    /// caller can pass the same string it gave generate_image.</summary>
    public WorkflowConfiguration? FindConfig(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        ReloadIfChanged();
        lock (_lock)
        {
            if (_byId.TryGetValue(id, out WorkflowConfiguration? c))
            {
                return c;
            }

            foreach (KeyValuePair<string, WorkflowConfiguration> kv in _byId)
            {
                if (kv.Key.Contains(id, StringComparison.OrdinalIgnoreCase)
                    || id.Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
                {
                    return kv.Value;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// The file this machine has bound to a slot, or "" when the slot is unknown or unbound. Used for parameters
    /// declared <see cref="ParamSpec.IsModelRef"/>, whose value is a slot id rather than a filename.
    /// </summary>
    public string ResolveSlot(string? slotId)
    {
        if (string.IsNullOrWhiteSpace(slotId))
        {
            return "";
        }

        ReloadIfChanged();
        lock (_lock)
        {
            return _bindings.TryGetValue(slotId, out string? f) ? f : "";
        }
    }

    /// <summary>
    /// Replace every <see cref="ParamSpec.IsModelRef"/> value in a merged param bag — a SLOT ID — with the file this
    /// machine has bound to it, in place.
    ///
    /// <para>A slot that does not resolve is a HARD FAILURE naming the slot. Yielding "" and carrying on would let
    /// each consumer substitute its own hardcoded filename, so a configuration whose slot had been deleted outright
    /// would render on the one machine that happens to have that file and report success.</para>
    ///
    /// <para>Lives here rather than inline in <c>ComfyClient.MergeParamsDict</c> because the test suite merges params
    /// too, and a second copy of this loop would let the resolution rules drift out of step with the renderer.</para>
    /// </summary>
    public void ResolveModelRefs(IWorkflow wf, string configId, IDictionary<string, object?> v)
    {
        foreach (ParamSpec spec in wf.Schema)
        {
            if (!spec.IsModelRef || !v.TryGetValue(spec.Key, out object? raw))
            {
                continue;
            }

            string? slot = raw is JsonElement je ? (je.ValueKind == JsonValueKind.String ? je.GetString() : null)
                                             : raw as string;
            // Not set at all is legitimate — an optional LoRA is absent, not unbound. Set-but-unresolvable is not.
            if (string.IsNullOrWhiteSpace(slot))
            {
                continue;
            }

            string file = ResolveSlot(slot);
            if (string.IsNullOrWhiteSpace(file))
            {
                throw new RenderValidationException(
                    $"Configuration '{configId}' needs a file for '{slot}' ({spec.Key}), and this machine has none bound. "
                    + "Bind it on the models page — or, if the slot no longer exists in the catalogue, the configuration is stale.");
            }

            v[spec.Key] = file;
        }
    }

    /// <summary>
    /// The slot ids a configuration will actually ask for through its <see cref="ParamSpec.IsModelRef"/> parameters —
    /// the configuration's own settings under this machine's overrides, exactly the layering
    /// <see cref="ResolveModelRefs"/> sees.
    ///
    /// <para>A configuration names models in TWO places, and both must be consulted for presence-gating. A model ref
    /// set in <c>params</c> is every bit as necessary as one in <c>requirements</c>: <c>wan22-i2v-a14b</c> names its
    /// second MoE expert nowhere else. Params the configuration does NOT set are absent by choice, not unbound, so
    /// they are not here — that is what keeps an optional LoRA from hiding every configuration that has none.</para>
    /// </summary>
    public IEnumerable<string> ModelRefSlots(IWorkflow wf, WorkflowConfiguration cfg)
    {
        IReadOnlyDictionary<string, JsonElement> overrides = ParamOverridesFor(cfg.Id);
        foreach (ParamSpec spec in wf.Schema)
        {
            if (!spec.IsModelRef)
            {
                continue;
            }

            object? raw = overrides.TryGetValue(spec.Key, out JsonElement ov) ? ov
                        : cfg.Params.TryGetValue(spec.Key, out ConfigParam? cp) ? cp.Value
                        : null;
            string? slot = raw is JsonElement je ? (je.ValueKind == JsonValueKind.String ? je.GetString() : null)
                                             : raw as string;
            if (!string.IsNullOrWhiteSpace(slot))
            {
                yield return slot;
            }
        }
    }

    /// <summary>Every bindable slot in the catalogue. Needed to report unbound slots and to run auto-matching.</summary>
    public IReadOnlyList<Requirement> AllRequirements()
    {
        ReloadIfChanged();
        lock (_lock)
        {
            return [.. _reqById.Values];
        }
    }

    public IReadOnlyList<WorkflowConfiguration> AllConfigs()
    {
        ReloadIfChanged();
        lock (_lock)
        {
            return [.. _all];
        }
    }

    public Requirement? FindRequirement(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        ReloadIfChanged();
        lock (_lock)
        {
            return _reqById.GetValueOrDefault(id);
        }
    }

    /// <summary>Resolve a configuration's requirement-id links into the concrete on-disk filenames a workflow
    /// loads. A missing/unknown requirement id resolves to an empty filename (the workflow then emits an empty
    /// loader field, which presence-gating would have already hidden).</summary>
    public ResolvedRequirements Resolve(WorkflowConfiguration cfg)
    {
        ReloadIfChanged();
        lock (_lock)
        {
            // A slot resolves to whatever THIS MACHINE has bound to it. An unbound slot yields an empty filename;
            // presence-gating will already have hidden the configuration, and the diagnostics list says which slot.
            string Name(string? rid)
            {
                return rid is not null && _bindings.TryGetValue(rid, out string? f) ? f : "";
            }

            return new ResolvedRequirements
            {
                Checkpoint = Name(cfg.Requirements.Checkpoint),
                TextEncoders = [.. cfg.Requirements.TextEncoders.Select(Name).Where(n => n.Length > 0)],
                Vae = string.IsNullOrEmpty(cfg.Requirements.Vae) ? null : Name(cfg.Requirements.Vae),
                MotionModel = string.IsNullOrEmpty(cfg.Requirements.MotionModel) ? null : Name(cfg.Requirements.MotionModel),
                ControlNet = string.IsNullOrEmpty(cfg.Requirements.ControlNet) ? null : Name(cfg.Requirements.ControlNet),
                Resolution = cfg.Resolution,
            };
        }
    }

    /// <summary>Every requirement a configuration links (resolved entries only), for presence-gating the list.</summary>
    public IReadOnlyList<Requirement> RequirementsOf(WorkflowConfiguration cfg)
    {
        ReloadIfChanged();
        lock (_lock)
        {
            return [.. cfg.Requirements.All()
                      .Select(id => _reqById.GetValueOrDefault(id))
                      .OfType<Requirement>()];
        }
    }

    /// <summary>Decision card for a configuration by id (used by /prompting and the JobQueue's tagging rules).</summary>
    public ModelCard? ResolveCard(string? id) => FindConfig(id)?.Card;

    /// <summary>Every configuration's decision card, for GET /prompting and similar listings.</summary>
    public IReadOnlyList<ModelCard> AllCards()
    {
        ReloadIfChanged();
        lock (_lock)
        {
            return [.. _all.Select(c => c.Card)];
        }
    }

    /// <summary>Reload when either file's timestamp has moved. A file that is momentarily ABSENT is not a change —
    /// editors that save by rename leave that gap, and reading it as "the catalog is now empty" would wipe a good
    /// catalog mid-save — so only a present file with a moved stamp counts.</summary>
    private void ReloadIfChanged()
    {
        (DateTime, int)? wfNow = PresentStamp(_workflowsDir);
        (DateTime, int)? reqNow = PresentStamp(_modelsDir);
        lock (_reloadGate)
        {
            bool changed = (wfNow is { } w && w != _wfStamp) || (reqNow is { } r && r != _modelStamp);
            if (!changed)
            {
                return;
            }

            if (_badVersion == (wfNow, reqNow))
            {
                return;   // this exact version already failed and was reported
            }

            try
            {
                Load();
                _badVersion = null;
            }
            catch (Exception ex)
            {
                // Keeping the last-good catalog IN MEMORY is deliberate: tearing a running server's model list down
                // over a half-saved edit helps nobody, and the previous catalog is still the truth about this box.
                // The edit is not live, and that is the one thing the operator must be told.
                _badVersion = (wfNow, reqNow);
                _log.LogError(ex, "Catalog changed on disk but FAILED to load — the edit is NOT live; keeping the previously "
                    + "loaded catalog ({Configurations} configuration(s)). Directories: {Workflows} + {Models}",
                    _all.Count, _workflowsDir, _modelsDir);
            }
        }
    }

    /// <summary>
    /// A directory's change stamp: the newest write time across its <c>*.json</c> files AND how many there are.
    ///
    /// <para>The count is not redundant. Deleting a file lowers nothing and changes no other file's timestamp, so
    /// a newest-write-time alone would leave a removed workflow in the catalog until something else was edited.
    /// Null when the path is unset or the directory is absent — both are legitimate, since an installation may
    /// ship only one of the two.</para>
    /// </summary>
    private static (DateTime, int)? PresentStamp(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            return null;
        }

        DateTime newest = DateTime.MinValue;
        int count = 0;
        foreach (string file in Directory.EnumerateFiles(dir, CatalogText.JsonGlob))
        {
            count++;
            DateTime written = File.GetLastWriteTimeUtc(file);
            if (written > newest)
            {
                newest = written;
            }
        }

        return (newest, count);
    }

    /// <summary>Parse both files and swap the catalog in. THROWS on a malformed file — the caller decides what that
    /// means (fatal at startup, last-good-plus-a-loud-log on hot reload). It must not decide for them by answering
    /// with an empty catalog, which reads downstream as a perfectly valid machine that offers no models.</summary>
    private void Load()
    {
        // Models first (configurations link to them by slot id).
        Dictionary<string, Requirement> reqById = new(StringComparer.OrdinalIgnoreCase);
        (DateTime Newest, int Count) modelStamp = _modelStamp;
        if (RequireConfiguredDirectory(_modelsDir, CatalogText.ModelsSection))
        {
            modelStamp = PresentStamp(_modelsDir)
                ?? throw new InvalidOperationException($"The models catalog directory vanished while loading: {_modelsDir}");
            Dictionary<string, string> seen = new(StringComparer.OrdinalIgnoreCase);
            foreach ((string? path, ModelFileDto? dto) in ReadAll(_modelsDir, CatalogJsonContext.Default.ModelFileDto))
            {
                string? id = dto.Id;
                if (string.IsNullOrEmpty(id))
                {
                    throw new InvalidOperationException($"{path}: a model file must have an 'id'.");
                }

                RequireIdMatchesFileName(path, id);
                RequireUnique(seen, id, path, CatalogText.ModelEntity);

                reqById[id] = new Requirement
                {
                    Id = id,
                    Kind = ParseKind(dto.Kind),
                    Label = dto.Label ?? id,
                    Match = Arr(dto.Match),
                    Node = dto.Node,
                    Page = dto.Page,
                };
            }
        }

        Dictionary<string, WorkflowConfiguration> byId = new(StringComparer.OrdinalIgnoreCase);
        List<WorkflowConfiguration> all = [];
        (DateTime Newest, int Count) wfStamp = _wfStamp;
        if (RequireConfiguredDirectory(_workflowsDir, CatalogText.WorkflowsSection))
        {
            wfStamp = PresentStamp(_workflowsDir)
                ?? throw new InvalidOperationException($"The workflows catalog directory vanished while loading: {_workflowsDir}");
            Dictionary<string, string> seen = new(StringComparer.OrdinalIgnoreCase);
            foreach ((string? path, WorkflowFileDto? dto) in ReadAll(_workflowsDir, CatalogJsonContext.Default.WorkflowFileDto))
            {
                string? id = dto.Id;
                string? wf = dto.Workflow;
                // A configuration with no id or no workflow class cannot run, and dropping it silently is
                // indistinguishable from this box not being able to afford it.
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(wf))
                {
                    throw new InvalidOperationException($"{path}: a workflow file must have 'id' and 'workflow'.");
                }

                RequireIdMatchesFileName(path, id);
                RequireUnique(seen, id, path, CatalogText.WorkflowEntity);

                WorkflowConfiguration entry = BuildConfiguration(dto, id, wf);
                all.Add(entry);
                byId[id] = entry;
            }
        }

        lock (_lock)
        {
            _reqById = reqById;
            _fileAll = all;
            _fileById = byId;
            _modelStamp = modelStamp;
            _wfStamp = wfStamp;
            RebuildEffectiveLocked();
        }
    }

    /// <summary>Recompute the effective catalogue (<see cref="_byId"/>/<see cref="_all"/>) from the file snapshot plus
    /// the retained DB variants. Caller holds <see cref="_lock"/>. A variant whose id collides with a shipped file is
    /// dropped with a warning (the file wins), mirroring the loader's own duplicate-id refusal.</summary>
    private void RebuildEffectiveLocked()
    {
        Dictionary<string, WorkflowConfiguration> byId = new(_fileById, StringComparer.OrdinalIgnoreCase);
        List<WorkflowConfiguration> all = [.. _fileAll];
        foreach (WorkflowConfiguration v in _variants)
        {
            if (byId.ContainsKey(v.Id))
            {
                _log.LogWarning("Workflow variant id '{Id}' collides with a shipped configuration; ignoring the variant.", v.Id);
                continue;
            }

            byId[v.Id] = v;
            all.Add(v);
        }

        _byId = byId;
        _all = all;
    }

    /// <summary>
    /// Replace this machine's DB-backed variants. Each spec is a duplicate of a shipped configuration: it inherits the
    /// base's workflow class, requirements and card live, and carries its own snapshotted parameters (frozen at copy).
    /// A spec whose base is unknown is skipped with a warning. Held in memory and folded into the effective catalogue,
    /// so <see cref="FindConfig"/> and the sync submit path resolve a variant id without a query — the same contract
    /// <see cref="SetBindings"/>/<see cref="SetParamOverrides"/> give bindings and overrides.
    /// </summary>
    public void SetVariants(IReadOnlyList<VariantSpec> specs)
    {
        List<WorkflowConfiguration> built = [];
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            foreach (VariantSpec spec in specs)
            {
                if (!_fileById.TryGetValue(spec.BaseConfigId, out WorkflowConfiguration? baseCfg))
                {
                    _log.LogWarning("Workflow variant '{Id}' names an unknown base configuration '{Base}'; skipping it.",
                        spec.VariantId, spec.BaseConfigId);
                    continue;
                }

                built.Add(BuildVariant(baseCfg, spec));
                _ = ids.Add(spec.VariantId);
            }

            _variants = built;
            _variantIds = ids;
            RebuildEffectiveLocked();
        }
    }

    /// <summary>True when the id names a DB-backed variant on this machine (not a shipped file).</summary>
    public bool IsVariant(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        lock (_lock)
        {
            return _variantIds.Contains(id);
        }
    }

    /// <summary>True when the EXACT id is a known configuration (file or variant). Unlike <see cref="FindConfig"/> this
    /// does no loose contains-match, so it is the right test for choosing a fresh, unused variant id.</summary>
    public bool HasConfigId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        lock (_lock)
        {
            return _byId.ContainsKey(id);
        }
    }

    /// <summary>Build a variant configuration: the base's structure (workflow class, requirements, card, resolution)
    /// with the variant's own id, friendly name, and snapshotted parameter values. A snapshot value replaces the base
    /// param's value (through <see cref="CloneValue"/>, so it is byte-identical to a file-loaded one); the visibility/
    /// range structure stays the base's. A snapshot key the base no longer has is dropped (a stale param).</summary>
    private static WorkflowConfiguration BuildVariant(WorkflowConfiguration baseCfg, VariantSpec spec)
    {
        Dictionary<string, ConfigParam> pars = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, ConfigParam bp) in baseCfg.Params)
        {
            object? value = spec.Params.TryGetValue(key, out JsonElement snap) ? CloneValue(snap) : bp.Value;
            pars[key] = new ConfigParam
            {
                Value = value,
                Visibility = bp.Visibility,
                Min = bp.Min,
                Max = bp.Max,
                Step = bp.Step,
            };
        }

        return new WorkflowConfiguration
        {
            Id = spec.VariantId,
            WorkflowName = baseCfg.WorkflowName,
            FriendlyName = spec.FriendlyName,
            Params = pars,
            Requirements = baseCfg.Requirements,
            EffectType = baseCfg.EffectType,
            EditGroup = baseCfg.EditGroup,
            Default = false,
            Card = baseCfg.Card,
            Resolution = baseCfg.Resolution,
        };
    }

    /// <summary>
    /// Deserializes every <c>*.json</c> in a catalogue directory into <typeparamref name="T"/>, in a stable order.
    ///
    /// <para>A file that cannot be understood in isolation — malformed JSON, an unknown/misspelled key (the DTOs are
    /// <see cref="System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow"/>), or a value of the wrong
    /// type — is reported by NAME and skipped, rather than failing the whole directory. This is the one deliberate
    /// departure from this codebase's fail-fast rule, and it is the consequence of the one-file-per-thing layout:
    /// users may add their own files here, and letting a single bad one take out all 167 workflows is a far worse
    /// outcome than losing the one. It is affordable precisely because the file is individually identifiable.</para>
    ///
    /// <para>Relational errors that a single file cannot see — a missing id, an id that disagrees with the filename,
    /// a duplicate id, an unknown model kind, an incomplete resolution block — are NOT caught here; they are thrown
    /// by the caller and are fatal at startup / last-good on hot reload.</para>
    /// </summary>
    private IEnumerable<(string Path, T Dto)> ReadAll<T>(string dir, JsonTypeInfo<T> type)
    {
        foreach (string? path in Directory.EnumerateFiles(dir, CatalogText.JsonGlob).OrderBy(p => p, StringComparer.Ordinal))
        {
            T? dto;
            try
            {
                dto = JsonSerializer.Deserialize(File.ReadAllText(path), type);
            }
            catch (JsonException ex)
            {
                _log.LogError(ex, "Catalog file {Path} is not valid or carries an unknown key and was SKIPPED; "
                    + "everything that depends on it is unavailable until it parses.", path);
                continue;
            }

            if (dto is null)
            {
                _log.LogError("Catalog file {Path} is an empty or null document and was SKIPPED.", path);
                continue;
            }

            yield return (path, dto);
        }
    }

    /// <summary>
    /// The filename is a convention, the <c>id</c> inside is the truth — but they must agree, because every error
    /// message and every link in the app names the id, and hunting for which file holds it is nobody's idea of a
    /// good time.
    /// </summary>
    private static void RequireIdMatchesFileName(string path, string id)
    {
        string stem = Path.GetFileNameWithoutExtension(path);
        if (!string.Equals(stem, id, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{path}: the file is named '{stem}' but declares id '{id}'. Rename one to match the other.");
        }
    }

    /// <summary>
    /// Two files claiming one id is an error naming BOTH, never a last-one-wins. Silently preferring one is how a
    /// user's file shadows a shipped one — or the reverse — without anybody finding out.
    /// </summary>
    private static void RequireUnique(Dictionary<string, string> seen, string id, string path, string what)
    {
        if (seen.TryGetValue(id, out string? first))
        {
            throw new InvalidOperationException($"Two {what} files declare id '{id}': {first} and {path}.");
        }

        seen[id] = path;
    }

    /// <summary>True when <paramref name="path"/> is configured and present. An UNSET path is a legitimate
    /// installation choice (only one of the two directories may ship) and answers false. A path that was configured
    /// and is not on disk is a misconfiguration, and throws rather than loading as an empty section — that difference
    /// is the whole distinction between "this box offers no models" and "nobody can find the files that list them".</summary>
    private static bool RequireConfiguredDirectory(string path, string what)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"The configured {what} catalog directory is not on disk: {path}");
        }

        return true;
    }

    private static WorkflowConfiguration BuildConfiguration(WorkflowFileDto c, string id, string workflow)
    {
        RequirementLinks rl = c.Requirements is { } r
            ? new RequirementLinks
            {
                Checkpoint = r.Checkpoint ?? "",
                TextEncoders = Arr(r.TextEncoders),
                Vae = r.Vae,
                MotionModel = r.MotionModel,
                ControlNet = r.ControlNet,
                Extra = Arr(r.Extra),
            }
            : new RequirementLinks();

        Dictionary<string, ConfigParam> pars = new(StringComparer.OrdinalIgnoreCase);
        if (c.Params is { } pmap)
        {
            foreach ((string? name, ConfigParamDto? p) in pmap)
            {
                pars[name] = new ConfigParam
                {
                    Value = CloneValue(p.Value),
                    Visibility = p.Visibility,
                    Min = p.Min,
                    Max = p.Max,
                    Step = p.Step,
                };
            }
        }

        ValidateReferenceConsistency(id, c.Card?.Reference, pars);

        return new WorkflowConfiguration
        {
            Id = id,
            WorkflowName = workflow,
            FriendlyName = c.FriendlyName,
            Requirements = rl,
            Params = pars,
            EffectType = c.EffectType,
            EditGroup = c.EditGroup,
            Default = c.Default ?? false,
            Resolution = BuildResolution(c.Resolution, id),
            Card = BuildCard(c.Card, id, c.FriendlyName),
        };
    }

    /// <summary>Normalize a card's <c>reference</c> block to per-kind allowances. The explicit <c>types</c> array wins
    /// (each entry's kind validated, non-positive maxes dropped); otherwise the scalar <c>max</c> declares IMAGE
    /// references only; a block that declares neither (or all-zero) yields no allowances (the editor takes no
    /// references). An unknown kind token throws — a malformed card is a boot error, not something to route around.</summary>
    private static List<ReferenceAllowance> NormalizeReferenceTypes(ReferenceDto? reference)
    {
        if (reference is null)
        {
            return [];
        }

        if (reference.Types is { Length: > 0 } types)
        {
            List<ReferenceAllowance> allow = new(types.Length);
            foreach (ReferenceTypeDto t in types)
            {
                ReferenceKind kind = ReferenceKinds.Parse(t.Kind);   // validate the token; throws on an unknown kind
                if (t.Max is > 0)
                {
                    allow.Add(new ReferenceAllowance(ReferenceKinds.Wire(kind), t.Max.Value));
                }
            }

            return allow;
        }

        return reference.Max is > 0 ? [new ReferenceAllowance(ReferenceKindNames.Image, reference.Max.Value)] : [];
    }

    /// <summary>An edit card's scalar <c>reference</c> block and its <c>reference_max</c> param are two independently
    /// authored numbers with no runtime coupling: the card sizes the UI <c>＋ ref</c> affordance, <c>reference_max</c>
    /// caps the graph. When the card uses the scalar (image-only) form they MUST agree — a higher card max lets the UI
    /// attach references the graph then rejects at submit, a lower one hides references the graph would accept. Two
    /// forms are deliberately out of scope: the multi-kind <c>types[]</c> card (ref2v) whose <c>reference_max</c> is a
    /// cross-kind total, and a <c>reference_max</c> with no card block at all (a graph cap with no UI affordance, e.g.
    /// an auto-applied pixelize). A mismatch is an authoring error, caught at boot rather than at the user's first
    /// over-supplied submit.</summary>
    private static void ValidateReferenceConsistency(string id, ReferenceDto? reference, Dictionary<string, ConfigParam> pars)
    {
        if (reference is null || reference.Types is { Length: > 0 })
        {
            return;
        }

        int cardMax = reference.Max ?? 0;
        int paramMax = pars.TryGetValue(WorkflowParamKeys.ReferenceMax, out ConfigParam? p) ? ParamsCodec.AsInt(p.Value) : 0;
        if (cardMax != paramMax)
        {
            throw new InvalidOperationException(
                $"{id}: card.reference.max ({cardMax}) and params.reference_max ({paramMax}) disagree. The card sizes the "
                + "＋ ref UI and reference_max caps the graph; when they diverge the UI offers reference slots the graph "
                + "rejects, or hides ones it would accept. Set both to the number of reference images the workflow wires.");
        }
    }

    private static ModelCard BuildCard(CardDto? m, string id, string? friendlyName)
    {
        PromptDto? prompt = m?.Prompt;
        SpeedDto? speed = m?.Speed;
        NegativeDto? negative = m?.Negative;
        UiHelpDto? ui = m?.UiHelp;
        ReferenceDto? reference = m?.Reference;
        return new ModelCard
        {
            Name = id,
            Id = id,
            FriendlyName = friendlyName ?? m?.FriendlyName,
            UiGoodFor = ui?.GoodFor,
            UiNote = ui?.Note,
            UiLinkText = ui?.Link?.Text,
            UiLinkUrl = ui?.Link?.Url,
            Architecture = m?.Architecture,
            Summary = m?.Summary,
            Notes = m?.Notes,
            UseCases = Arr(m?.UseCases),
            PromptFormat = prompt?.Format,
            RequiredPrefix = prompt?.RequiredPrefix,
            PromptOptionalTags = Arr(prompt?.OptionalTags),
            PromptGuidance = prompt?.Guidance,
            PromptOverview = prompt?.Overview,
            PromptInstructions = prompt?.Instructions,
            PromptExample = prompt?.Example,
            PromptDo = Arr(prompt?.Do),
            PromptDont = Arr(prompt?.Dont),
            PromptExamples = Arr(prompt?.Examples),
            PromptSource = prompt?.Source,
            NegativeSupported = negative?.Supported,
            NegativeGuidance = prompt?.NegativeGuidance ?? negative?.Note,
            Speed = speed?.Class,
            SpeedNote = speed?.Note,
            ExpectedGenSeconds = speed?.MeasuredSeconds,
            ExpectedGenNote = speed?.MeasuredNote,
            NsfwCapable = m?.NsfwCapable,
            CommercialUse = m?.CommercialUse,
            PickWhen = m?.PickWhen,
            EditUseCases = Arr(m?.EditUseCases),
            EditReferenceTypes = NormalizeReferenceTypes(reference),
            EditReferenceHint = reference?.Hint,
            Tagging = m?.Tagging is { } t
                ? new TaggingInfo
                {
                    Tags = t.Tags ?? false,
                    Artists = t.Artists ?? false,
                    KeepArtistMarker = t.KeepArtistMarker ?? false,
                    UnderscoresToSpaces = t.UnderscoresToSpaces ?? false
                }
                : null
        };
    }

    /// <summary>
    /// The catalogue's kind string to the loader it draws from. Every value is named; an unknown one THROWS.
    ///
    /// <para>A catch-all <c>_ =&gt; Other</c> would pool "lora", "clip_vision", "ipadapter",
    /// "latent_upscale_model" and "other" into one bucket and offer every slot among them every file of all of
    /// them; a typo would do the same thing silently. Failing here means a kind that is not wired to a
    /// loader is impossible to ship rather than merely wrong at runtime.</para>
    /// </summary>
    [AllowMagicStrings("exception message describing an unknown model kind")]
    private static RequirementKind ParseKind(string? k) => (k ?? "").ToLowerInvariant() switch
    {
        RequirementKindWire.Checkpoint => RequirementKind.Checkpoint,
        RequirementKindWire.Unet => RequirementKind.Unet,
        RequirementKindWire.UnetGguf => RequirementKind.UnetGguf,
        RequirementKindWire.Vae => RequirementKind.Vae,
        RequirementKindWire.TextEncoder => RequirementKind.TextEncoder,
        RequirementKindWire.MotionModel => RequirementKind.MotionModel,
        RequirementKindWire.ControlNet => RequirementKind.ControlNet,
        RequirementKindWire.UpscaleModel => RequirementKind.UpscaleModel,
        RequirementKindWire.Lora => RequirementKind.Lora,
        RequirementKindWire.ClipVision => RequirementKind.ClipVision,
        RequirementKindWire.IpAdapter => RequirementKind.IpAdapter,
        RequirementKindWire.LatentUpscaleModel => RequirementKind.LatentUpscaleModel,
        RequirementKindWire.SeedVr2 => RequirementKind.SeedVr2,
        RequirementKindWire.Diffusers => RequirementKind.Diffusers,
        RequirementKindWire.CustomNode => RequirementKind.CustomNode,
        _ => throw new ArgumentException(
            $"Unknown model kind '{k}'. Every kind must name a loader's file list; add it to RequirementKind "
            + "and to ComfyClient.LoaderInputs rather than letting it fall into a shared pool.")
    };

    /// <summary>
    /// Decouple a parameter value from the parsed JsonDocument (which is disposed): clone scalars to CLR primitives
    /// and objects/arrays to an independent JsonElement so the value stays valid after the doc is gone.
    /// </summary>
    private static object? CloneValue(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Number => v.TryGetInt64(out long l) ? l : v.GetDouble(),
        _ => v.Clone()   // object / array (e.g. the aspect dims map, reference_inputs list)
    };

    /// <summary>Build a configuration's optional <c>resolution</c> envelope ({min_w,min_h,max_w,max_h,step}). The block
    /// as a whole is optional (absent → null), but a block that IS declared must be complete: a missing field is a
    /// config error, not a default. Silently filling <c>step</c> with 16, or a side with 0, would bound the render-size
    /// editor by a number the model never stated — 16 for a model that needs 32, an unbounded floor for one with a real
    /// minimum — so the size the author actually meant is the one thing that must not be guessed here.</summary>
    private static ModelResolution? BuildResolution(ResolutionDto? res, string id)
    {
        if (res is null)
        {
            return null;
        }

        int Req(int? v, string k)
        {
            return v ?? throw new InvalidOperationException(
            $"{id}: the resolution block is missing '{k}'. A declared envelope must state min_w/min_h/max_w/max_h and step; "
            + "an omitted field would silently bound the size editor by a number the model never gave.");
        }

        return new ModelResolution
        {
            MinW = Req(res.MinW, ResolutionMember.MinW),
            MinH = Req(res.MinH, ResolutionMember.MinH),
            MaxW = Req(res.MaxW, ResolutionMember.MaxW),
            MaxH = Req(res.MaxH, ResolutionMember.MaxH),
            Step = Req(res.Step, ResolutionMember.Step),
        };
    }

    /// <summary>A catalog string array as the domain wants it: an independent, non-empty-entry array (never null).
    /// Empty and null entries are dropped, matching the loaders' "an empty filename is not a slot" contract.</summary>
    private static string[] Arr(string[]? a) =>
        a is null ? [] : [.. a.Where(x => !string.IsNullOrEmpty(x))];
}

/// <summary>One DB-backed workflow variant, resolved for the catalogue: its own id and friendly name, the shipped
/// configuration it duplicates, and the snapshotted parameter values (canonical key → JSON value) frozen at copy time.
/// <see cref="WorkflowCatalog.SetVariants"/> turns each into a full <see cref="WorkflowConfiguration"/> over its base.</summary>
public sealed record VariantSpec(
    string VariantId, string BaseConfigId, string FriendlyName, IReadOnlyDictionary<string, JsonElement> Params);

/// <summary>LLM/UI-facing decision info for a configuration (its prompting guide + selection hints). Carried in the
/// configuration's <c>card</c> block; surfaced by GET /prompting and GET /workflows.</summary>
public sealed class ModelCard
{
    public string Name { get; init; } = "";
    public string? Id { get; init; }
    public string? FriendlyName { get; init; }
    public string? UiGoodFor { get; init; }
    public string? UiNote { get; init; }
    public string? UiLinkText { get; init; }
    public string? UiLinkUrl { get; init; }
    public string? Architecture { get; init; }
    public string? Summary { get; init; }
    /// <summary>Free-form authoring note about the model (e.g. a licence caveat), or null. Card <c>notes</c>.</summary>
    public string? Notes { get; init; }
    public string[] UseCases { get; init; } = [];
    public string? PromptFormat { get; init; }
    public string? RequiredPrefix { get; init; }
    /// <summary>Optional booru tag groups a tag-model prompt may draw from — each entry a pipe-delimited option set,
    /// or empty. Card <c>prompt.optional_tags</c>.</summary>
    public string[] PromptOptionalTags { get; init; } = [];
    public string? PromptGuidance { get; init; }
    public string? PromptOverview { get; init; }
    public string? PromptInstructions { get; init; }
    public string? PromptExample { get; init; }
    public string[] PromptDo { get; init; } = [];
    public string[] PromptDont { get; init; } = [];
    public string[] PromptExamples { get; init; } = [];
    public string? PromptSource { get; init; }
    [AllowNullable("null = the card doesn't declare negative-prompt support (unknown); distinct from an explicit false")]
    public bool? NegativeSupported { get; init; }
    public string? NegativeGuidance { get; init; }
    public string[] EditUseCases { get; init; } = [];
    /// <summary>The accepted reference media kinds and their per-kind maxes (empty when the editor takes no references),
    /// normalized from the card's <c>reference</c> block.</summary>
    public IReadOnlyList<ReferenceAllowance> EditReferenceTypes { get; init; } = [];
    public string? EditReferenceHint { get; init; }
    public string? Speed { get; init; }
    /// <summary>A short qualitative speed note (e.g. "Fastest model here"), distinct from the benchmarked
    /// <see cref="ExpectedGenNote"/>. Card <c>speed.note</c>.</summary>
    public string? SpeedNote { get; init; }
    [AllowNullable("null = no benchmarked ETA declared for this card; 0.0 would be a real (instant) estimate")]
    public double? ExpectedGenSeconds { get; init; }
    public string? ExpectedGenNote { get; init; }
    public string? NsfwCapable { get; init; }
    public string? CommercialUse { get; init; }
    public string? PickWhen { get; init; }
    /// <summary>The booru tagging block (autocomplete + marker-stripping rules), or null. Drives the gateway's
    /// per-job random-artist append; mirrored client-side by the SPA.</summary>
    public TaggingInfo? Tagging { get; init; }
}

/// <summary>Per-configuration booru tagging capability (from the card's <c>tagging</c> block).</summary>
public sealed class TaggingInfo
{
    public bool Tags { get; init; }
    public bool Artists { get; init; }
    public bool KeepArtistMarker { get; init; }
    public bool UnderscoresToSpaces { get; init; }
}