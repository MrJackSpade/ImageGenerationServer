using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ImageGen.Application.Rendering;
using Microsoft.Extensions.Logging;

namespace ImageGen.Comfy;

/// <summary>
/// The workflow catalog, loaded from <c>workflows.json</c> (the configurations) + <c>requirements.json</c> (the
/// model-file registry). A configuration binds a workflow class, supplies its parameter settings layer, soft-links
/// its requirements by id, and carries the decision-card/prompting metadata surfaced by /prompting and /workflows.
/// <para>Both files hot-reload when changed on disk. Failure is NOT silent at either point: the startup load throws
/// (a catalog that will not parse is a machine that offers no models, and booting into that state hides the reason),
/// and a failed hot-reload keeps the last-good catalog but says so at Error and does not retry the same broken
/// version. This whole path was previously two bare <c>catch { }</c>s, which made an edit with a stray comma
/// indistinguishable from an edit that changed nothing at all.</para>
/// </summary>
public sealed class WorkflowCatalog
{
    private readonly string _workflowsDir;
    private readonly string _modelsDir;
    private readonly ILogger<WorkflowCatalog> _log;
    private readonly object _lock = new();
    private readonly object _reloadGate = new();
    private Dictionary<string, WorkflowConfiguration> _byId = new(StringComparer.OrdinalIgnoreCase);
    private List<WorkflowConfiguration> _all = new();
    private Dictionary<string, Requirement> _reqById = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Slot id -> the file bound to it on this machine. Refreshed with the catalog and after a UI edit.</summary>
    private Dictionary<string, string> _bindings = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, Dictionary<string, JsonElement>> _paramOverrides = new(StringComparer.OrdinalIgnoreCase);
    private (DateTime Newest, int Count) _wfStamp, _modelStamp;
    /// <summary>The directory-stamp pair whose load threw, so the same broken version is reported once instead of
    /// on every catalog read until someone saves a file again.</summary>
    private ((DateTime, int)? Wf, (DateTime, int)? Models)? _badVersion;

    public WorkflowCatalog(ComfyOptions config, ILogger<WorkflowCatalog> log)
    {
        var root = config.CatalogPath;
        _workflowsDir = root.Length == 0 ? "" : Path.Combine(root, "workflows");
        _modelsDir = root.Length == 0 ? "" : Path.Combine(root, "models");
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
        var copy = new Dictionary<string, string>(bindings, StringComparer.OrdinalIgnoreCase);
        lock (_lock) _bindings = copy;
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
        var copy = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (configId, settings) in overrides)
        {
            var parsed = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, raw) in settings)
            {
                if (!key.StartsWith("param.", StringComparison.OrdinalIgnoreCase)) continue;
                parsed[key["param.".Length..]] = AsJson(raw);
            }
            if (parsed.Count > 0) copy[configId] = parsed;
        }
        lock (_lock) _paramOverrides = copy;
    }

    /// <summary>This machine's overrides for one configuration, or empty.</summary>
    public IReadOnlyDictionary<string, JsonElement> ParamOverridesFor(string configId)
    {
        lock (_lock)
            return _paramOverrides.TryGetValue(configId, out var v)
                ? v
                : (IReadOnlyDictionary<string, JsonElement>)new Dictionary<string, JsonElement>();
    }

    private static JsonElement AsJson(string raw)
    {
        try { return JsonDocument.Parse(raw).RootElement.Clone(); }
        catch (JsonException) { return JsonDocument.Parse(JsonSerializer.Serialize(raw)).RootElement.Clone(); }
    }

    /// <summary>Configuration by its (unique) id, or null. Resolution is by id, then a loose contains match so a
    /// caller can pass the same string it gave generate_image.</summary>
    public WorkflowConfiguration? FindConfig(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        ReloadIfChanged();
        lock (_lock)
        {
            if (_byId.TryGetValue(id, out var c)) return c;
            foreach (var kv in _byId)
                if (kv.Key.Contains(id, StringComparison.OrdinalIgnoreCase)
                    || id.Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            return null;
        }
    }

    /// <summary>
    /// The file this machine has bound to a slot, or "" when the slot is unknown or unbound. Used for parameters
    /// declared <see cref="ParamSpec.IsModelRef"/>, whose value is a slot id rather than a filename.
    /// </summary>
    public string ResolveSlot(string? slotId)
    {
        if (string.IsNullOrWhiteSpace(slotId)) return "";
        ReloadIfChanged();
        lock (_lock) return _bindings.TryGetValue(slotId, out var f) ? f : "";
    }

    /// <summary>
    /// Replace every <see cref="ParamSpec.IsModelRef"/> value in a merged param bag — a SLOT ID — with the file this
    /// machine has bound to it, in place.
    ///
    /// <para>A slot that does not resolve is a HARD FAILURE naming the slot. It used to yield "" and carry on, and
    /// each consumer then substituted its own hardcoded filename, so a configuration whose slot had been deleted
    /// outright rendered perfectly on the one machine that happened to have that file and reported success.</para>
    ///
    /// <para>Lives here rather than inline in <c>ComfyClient.MergeParamsDict</c> because the test suite merges params
    /// too, and a second copy of this loop is what let the resolution rules drift out of step with the renderer once
    /// already.</para>
    /// </summary>
    public void ResolveModelRefs(IWorkflow wf, string configId, IDictionary<string, object?> v)
    {
        foreach (var spec in wf.Schema)
        {
            if (!spec.IsModelRef || !v.TryGetValue(spec.Key, out var raw)) continue;
            var slot = raw is JsonElement je ? (je.ValueKind == JsonValueKind.String ? je.GetString() : null)
                                             : raw as string;
            // Not set at all is legitimate — an optional LoRA is absent, not unbound. Set-but-unresolvable is not.
            if (string.IsNullOrWhiteSpace(slot)) continue;
            var file = ResolveSlot(slot);
            if (string.IsNullOrWhiteSpace(file))
                throw new RenderValidationException(
                    $"Configuration '{configId}' needs a file for '{slot}' ({spec.Key}), and this machine has none bound. "
                    + "Bind it on the models page — or, if the slot no longer exists in the catalogue, the configuration is stale.");
            v[spec.Key] = file;
        }
    }

    /// <summary>
    /// The slot ids a configuration will actually ask for through its <see cref="ParamSpec.IsModelRef"/> parameters —
    /// the configuration's own settings under this machine's overrides, exactly the layering
    /// <see cref="ResolveModelRefs"/> sees.
    ///
    /// <para>A configuration names models in TWO places, and only <c>requirements</c> was ever consulted for
    /// presence-gating. A model ref set in <c>params</c> is every bit as necessary: <c>wan22-i2v-a14b</c> names its
    /// second MoE expert nowhere else. Params the configuration does NOT set are absent by choice, not unbound, so
    /// they are not here — that is what keeps an optional LoRA from hiding every configuration that has none.</para>
    /// </summary>
    public IEnumerable<string> ModelRefSlots(IWorkflow wf, WorkflowConfiguration cfg)
    {
        var overrides = ParamOverridesFor(cfg.Id);
        foreach (var spec in wf.Schema)
        {
            if (!spec.IsModelRef) continue;
            object? raw = overrides.TryGetValue(spec.Key, out var ov) ? ov
                        : cfg.Params.TryGetValue(spec.Key, out var cp) ? cp.Value
                        : null;
            var slot = raw is JsonElement je ? (je.ValueKind == JsonValueKind.String ? je.GetString() : null)
                                             : raw as string;
            if (!string.IsNullOrWhiteSpace(slot)) yield return slot;
        }
    }

    /// <summary>Every bindable slot in the catalogue. Needed to report unbound slots and to run auto-matching.</summary>
    public IReadOnlyList<Requirement> AllRequirements()
    {
        ReloadIfChanged();
        lock (_lock) return _reqById.Values.ToList();
    }

    public IReadOnlyList<WorkflowConfiguration> AllConfigs()
    {
        ReloadIfChanged();
        lock (_lock) return _all.ToList();
    }

    public Requirement? FindRequirement(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        ReloadIfChanged();
        lock (_lock) return _reqById.GetValueOrDefault(id);
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
            string Name(string? rid) => rid is not null && _bindings.TryGetValue(rid, out var f) ? f : "";
            return new ResolvedRequirements
            {
                Checkpoint = Name(cfg.Requirements.Checkpoint),
                TextEncoders = cfg.Requirements.TextEncoders.Select(Name).Where(n => n.Length > 0).ToList(),
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
            return cfg.Requirements.All()
                      .Select(id => _reqById.GetValueOrDefault(id))
                      .OfType<Requirement>()
                      .ToList();
    }

    /// <summary>Decision card for a configuration by id (used by /prompting and the JobQueue's tagging rules).</summary>
    public ModelCard? ResolveCard(string? id) => FindConfig(id)?.Card;

    /// <summary>Every configuration's decision card, for GET /prompting and similar listings.</summary>
    public IReadOnlyList<ModelCard> AllCards()
    {
        ReloadIfChanged();
        lock (_lock) return _all.Select(c => c.Card).ToList();
    }

    /// <summary>Reload when either file's timestamp has moved. A file that is momentarily ABSENT is not a change —
    /// editors that save by rename leave that gap, and reading it as "the catalog is now empty" would wipe a good
    /// catalog mid-save — so only a present file with a moved stamp counts.</summary>
    private void ReloadIfChanged()
    {
        var wfNow = PresentStamp(_workflowsDir);
        var reqNow = PresentStamp(_modelsDir);
        lock (_reloadGate)
        {
            var changed = (wfNow is { } w && w != _wfStamp) || (reqNow is { } r && r != _modelStamp);
            if (!changed) return;
            if (_badVersion == (wfNow, reqNow)) return;   // this exact version already failed and was reported

            try
            {
                Load();
                _badVersion = null;
            }
            catch (Exception ex)
            {
                // Keeping the last-good catalog IN MEMORY is deliberate: tearing a running server's model list down
                // over a half-saved edit helps nobody, and the previous catalog is still the truth about this box.
                // Silence was the defect — the edit is not live, and that is the one thing the operator must be told.
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
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return null;
        var newest = DateTime.MinValue;
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            count++;
            var written = File.GetLastWriteTimeUtc(file);
            if (written > newest) newest = written;
        }
        return (newest, count);
    }

    /// <summary>Parse both files and swap the catalog in. THROWS on a malformed file — the caller decides what that
    /// means (fatal at startup, last-good-plus-a-loud-log on hot reload). It must not decide for them by answering
    /// with an empty catalog, which reads downstream as a perfectly valid machine that offers no models.</summary>
    private void Load()
    {
        // Models first (configurations link to them by slot id).
        var reqById = new Dictionary<string, Requirement>(StringComparer.OrdinalIgnoreCase);
        var modelStamp = _modelStamp;
        if (RequireConfiguredDirectory(_modelsDir, "models"))
        {
            modelStamp = PresentStamp(_modelsDir)
                ?? throw new InvalidOperationException($"The models catalog directory vanished while loading: {_modelsDir}");
            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, dto) in ReadAll(_modelsDir, CatalogJsonContext.Default.ModelFileDto))
            {
                var id = dto.Id;
                if (string.IsNullOrEmpty(id))
                    throw new InvalidOperationException($"{path}: a model file must have an 'id'.");
                RequireIdMatchesFileName(path, id);
                RequireUnique(seen, id, path, "model");

                reqById[id] = new Requirement
                {
                    Id = id,
                    Kind = ParseKind(dto.Kind),
                    Label = dto.Label ?? id,
                    Match = Arr(dto.Match),
                    Node = dto.Node,
                };
            }
        }

        var byId = new Dictionary<string, WorkflowConfiguration>(StringComparer.OrdinalIgnoreCase);
        var all = new List<WorkflowConfiguration>();
        var wfStamp = _wfStamp;
        if (RequireConfiguredDirectory(_workflowsDir, "workflows"))
        {
            wfStamp = PresentStamp(_workflowsDir)
                ?? throw new InvalidOperationException($"The workflows catalog directory vanished while loading: {_workflowsDir}");
            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, dto) in ReadAll(_workflowsDir, CatalogJsonContext.Default.WorkflowFileDto))
            {
                var id = dto.Id;
                var wf = dto.Workflow;
                // A configuration with no id or no workflow class cannot run, and dropping it silently is
                // indistinguishable from this box not being able to afford it.
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(wf))
                    throw new InvalidOperationException($"{path}: a workflow file must have 'id' and 'workflow'.");
                RequireIdMatchesFileName(path, id);
                RequireUnique(seen, id, path, "workflow");

                var entry = BuildConfiguration(dto, id, wf);
                all.Add(entry);
                byId[id] = entry;
            }
        }

        lock (_lock)
        {
            _reqById = reqById; _all = all; _byId = byId;
            _modelStamp = modelStamp; _wfStamp = wfStamp;
        }
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
        foreach (var path in Directory.EnumerateFiles(dir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
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
        var stem = Path.GetFileNameWithoutExtension(path);
        if (!string.Equals(stem, id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"{path}: the file is named '{stem}' but declares id '{id}'. Rename one to match the other.");
    }

    /// <summary>
    /// Two files claiming one id is an error naming BOTH, never a last-one-wins. Silently preferring one is how a
    /// user's file shadows a shipped one — or the reverse — without anybody finding out.
    /// </summary>
    private static void RequireUnique(Dictionary<string, string> seen, string id, string path, string what)
    {
        if (seen.TryGetValue(id, out var first))
            throw new InvalidOperationException($"Two {what} files declare id '{id}': {first} and {path}.");
        seen[id] = path;
    }

    /// <summary>True when <paramref name="path"/> is configured and present. An UNSET path is a legitimate
    /// installation choice (only one of the two directories may ship) and answers false. A path that was configured
    /// and is not on disk is a misconfiguration, and throws rather than loading as an empty section — that difference
    /// is the whole distinction between "this box offers no models" and "nobody can find the files that list them".</summary>
    private static bool RequireConfiguredDirectory(string path, string what)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"The configured {what} catalog directory is not on disk: {path}");
        return true;
    }

    private static WorkflowConfiguration BuildConfiguration(WorkflowFileDto c, string id, string workflow)
    {
        var rl = c.Requirements is { } r
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

        var pars = new Dictionary<string, ConfigParam>(StringComparer.OrdinalIgnoreCase);
        if (c.Params is { } pmap)
            foreach (var (name, p) in pmap)
                pars[name] = new ConfigParam
                {
                    Value = CloneValue(p.Value),
                    Exposed = p.Exposed,
                    Locked = p.Locked,
                    Min = p.Min,
                    Max = p.Max,
                    Step = p.Step,
                };

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

    private static ModelCard BuildCard(CardDto? m, string id, string? friendlyName)
    {
        var prompt = m?.Prompt;
        var speed = m?.Speed;
        var negative = m?.Negative;
        var ui = m?.UiHelp;
        var reference = m?.Reference;
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
            EditReferenceMax = reference?.Max ?? 0,
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
    /// <para>This used to end in <c>_ =&gt; Other</c>, so "lora", "clip_vision", "ipadapter",
    /// "latent_upscale_model" and "other" all became one bucket and every slot among them was offered every file
    /// of all of them. A typo did the same thing silently. Failing here means a kind that is not wired to a
    /// loader is impossible to ship rather than merely wrong at runtime.</para>
    /// </summary>
    private static RequirementKind ParseKind(string? k) => (k ?? "").ToLowerInvariant() switch
    {
        "checkpoint" => RequirementKind.Checkpoint,
        "unet" => RequirementKind.Unet,
        "unet_gguf" => RequirementKind.UnetGguf,
        "vae" => RequirementKind.Vae,
        "text_encoder" => RequirementKind.TextEncoder,
        "motion_model" => RequirementKind.MotionModel,
        "controlnet" => RequirementKind.ControlNet,
        "upscale_model" => RequirementKind.UpscaleModel,
        "lora" => RequirementKind.Lora,
        "clip_vision" => RequirementKind.ClipVision,
        "ipadapter" => RequirementKind.IpAdapter,
        "latent_upscale_model" => RequirementKind.LatentUpscaleModel,
        "seedvr2" => RequirementKind.SeedVr2,
        "custom_node" => RequirementKind.CustomNode,
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
        JsonValueKind.Number => v.TryGetInt64(out var l) ? l : v.GetDouble(),
        _ => v.Clone()   // object / array (e.g. the aspect dims map, reference_inputs list)
    };

    /// <summary>Build a configuration's optional <c>resolution</c> envelope ({min_w,min_h,max_w,max_h,step}). The block
    /// as a whole is optional (absent → null), but a block that IS declared must be complete: a missing field is a
    /// config error, not a default. Silently filling <c>step</c> with 16, or a side with 0, would bound the render-size
    /// editor by a number the model never stated — 16 for a model that needs 32, an unbounded floor for one with a real
    /// minimum — so the size the author actually meant is the one thing that must not be guessed here.</summary>
    private static ModelResolution? BuildResolution(ResolutionDto? res, string id)
    {
        if (res is null) return null;
        int Req(int? v, string k) => v ?? throw new InvalidOperationException(
            $"{id}: the resolution block is missing '{k}'. A declared envelope must state min_w/min_h/max_w/max_h and step; "
            + "an omitted field would silently bound the size editor by a number the model never gave.");
        return new ModelResolution
        {
            MinW = Req(res.MinW, "min_w"),
            MinH = Req(res.MinH, "min_h"),
            MaxW = Req(res.MaxW, "max_w"),
            MaxH = Req(res.MaxH, "max_h"),
            Step = Req(res.Step, "step"),
        };
    }

    /// <summary>A catalog string array as the domain wants it: an independent, non-empty-entry array (never null).
    /// Empty and null entries are dropped, matching the loaders' "an empty filename is not a slot" contract.</summary>
    private static string[] Arr(string[]? a) =>
        a is null ? Array.Empty<string>() : a.Where(x => !string.IsNullOrEmpty(x)).ToArray();
}

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
    public string[] UseCases { get; init; } = Array.Empty<string>();
    public string? PromptFormat { get; init; }
    public string? RequiredPrefix { get; init; }
    /// <summary>Optional booru tag groups a tag-model prompt may draw from — each entry a pipe-delimited option set,
    /// or empty. Card <c>prompt.optional_tags</c>.</summary>
    public string[] PromptOptionalTags { get; init; } = Array.Empty<string>();
    public string? PromptGuidance { get; init; }
    public string? PromptOverview { get; init; }
    public string? PromptInstructions { get; init; }
    public string? PromptExample { get; init; }
    public string[] PromptDo { get; init; } = Array.Empty<string>();
    public string[] PromptDont { get; init; } = Array.Empty<string>();
    public string[] PromptExamples { get; init; } = Array.Empty<string>();
    public string? PromptSource { get; init; }
    public bool? NegativeSupported { get; init; }
    public string? NegativeGuidance { get; init; }
    public string[] EditUseCases { get; init; } = Array.Empty<string>();
    public int EditReferenceMax { get; init; }
    public string? EditReferenceHint { get; init; }
    public string? Speed { get; init; }
    /// <summary>A short qualitative speed note (e.g. "Fastest model here"), distinct from the benchmarked
    /// <see cref="ExpectedGenNote"/>. Card <c>speed.note</c>.</summary>
    public string? SpeedNote { get; init; }
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
