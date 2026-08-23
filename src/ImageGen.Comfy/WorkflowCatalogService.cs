using ImageGen.Application.Snapshots;
using ImageGen.Application.Workflows;
using ImageGen.Comfy.Snapshots;
using ImageGen.Domain;
using ImageGen.Domain.Repositories;
using System.Linq;
using System.Text.Json;

namespace ImageGen.Comfy;

/// <summary>
/// The Comfy-side <see cref="IWorkflowCatalog"/>: wraps the configuration-tree loader (<see cref="WorkflowCatalog"/>), the
/// graph registry (<see cref="WorkflowRegistry"/>), the ComfyUI capability probes (<see cref="ComfyClient"/>), and the
/// gen-timing history, and projects them into the Application's workflow business objects. The eligibility algorithm
/// (requirement-presence gating + shared-friendly-name de-duplication) and the row/guide shaping live here
/// — the core sees only the resulting descriptors and guides.
/// </summary>
public sealed partial class WorkflowCatalogService(
    WorkflowCatalog catalog, WorkflowRegistry registry, ComfyProbeSnapshots probes, CatalogSnapshots snapshots,
    ISnapshot<GenTimingAverages> timings, ICatalogOverrideRepository overrides, IWorkflowVariantRepository variants,
    ILogger<WorkflowCatalogService> log) : IWorkflowCatalog
{
    private readonly WorkflowCatalog _catalog = catalog;
    private readonly WorkflowRegistry _registry = registry;
    private readonly ComfyProbeSnapshots _probes = probes;
    private readonly CatalogSnapshots _snapshots = snapshots;
    private readonly ISnapshot<GenTimingAverages> _timings = timings;
    private readonly ICatalogOverrideRepository _overrides = overrides;
    private readonly IWorkflowVariantRepository _variants = variants;
    private readonly ILogger<WorkflowCatalogService> _log = log;

    /// <inheritdoc/>
    public WorkflowInfo? ResolveInfo(string? configId)
    {
        WorkflowConfiguration? cfg = _catalog.FindConfig(configId);
        if (cfg is null)
        {
            return null;
        }

        ModelCard card = cfg.Card;
        string friendly = card.FriendlyName ?? cfg.FriendlyName ?? cfg.Id;
        IWorkflow? wf = _registry.Find(cfg.WorkflowName);
        bool preserves = wf?.PreservesComposition ?? false;
        string kind = wf is null ? "" : KindToken(ResolveKind(cfg, wf));
        IReadOnlyDictionary<string, JsonElement> machine = _catalog.ParamOverridesFor(cfg.Id);
        return new WorkflowInfo(
            friendly, ToTagging(card.Tagging), preserves, wf?.Media == WorkflowMedia.Video, BuildReference(card),
            HasAudio(cfg, wf), kind, EffectiveBool(machine, SettingKeys.TagGenerator, card.Tagging?.Tags == true),
            wf?.SupportsReferenceOnly == true && BuildReference(card)?.MaxImages > 0);
    }

    /// <summary>The workflow's reference capability, or null when it accepts no references — the single projection of a
    /// card's normalized reference types shared by the descriptor (UI) and <see cref="WorkflowInfo"/> (enqueue validation).</summary>
    private static WorkflowReference? BuildReference(ModelCard card) =>
        card.EditReferenceTypes.Count > 0 ? new WorkflowReference(card.EditReferenceTypes, card.EditReferenceHint) : null;

    /// <summary>The workflow's BASE tags: the card's own <c>tags</c> with the derived capability tags
    /// (<see cref="WorkflowTagTokens.Reference"/> when it accepts a reference, <see cref="WorkflowTagTokens.Inpaint"/>
    /// when it is an inpaint config) prepended so they read first in the picker. A card that already spells one stays
    /// deduped. Null when there are none, matching the other empty-array-as-null card fields.</summary>
    internal static string[]? ComposeBaseTags(ModelCard card, bool takesReference, bool isInpaint)
    {
        List<string> tags = [];
        if (takesReference)
        {
            tags.Add(WorkflowTagTokens.Reference);
        }

        if (isInpaint)
        {
            tags.Add(WorkflowTagTokens.Inpaint);
        }

        foreach (string tag in card.Tags)
        {
            if (!string.IsNullOrWhiteSpace(tag) && !tags.Contains(tag, StringComparer.Ordinal))
            {
                tags.Add(tag);
            }
        }

        return tags.Count > 0 ? [.. tags] : null;
    }

    /// <inheritdoc/>
    public async Task<string> DuplicateWorkflowAsync(string baseConfigId, string friendlyName, CancellationToken ct)
    {
        WorkflowConfiguration? baseCfg = _catalog.FindConfig(baseConfigId)
            ?? throw new ArgumentException($"Unknown workflow '{baseConfigId}'.", nameof(baseConfigId));
        string name = friendlyName?.Trim() ?? "";
        if (name.Length == 0)
        {
            throw new ArgumentException("A variant name is required.", nameof(friendlyName));
        }

        // A variant's stored base is ALWAYS a shipped file — that is the invariant SetVariants resolves against. When
        // duplicating a variant, root the copy at the SAME file (not the variant, which no file table knows), while
        // snapshotting the source's current effective params below. So a copy-of-a-copy is still a file-rooted variant.
        string fileBaseId = await FileRootOfAsync(baseCfg.Id, ct);
        string variantId = NextVariantId(baseCfg.Id);
        string paramsJson = JsonSerializer.Serialize(SnapshotParams(baseCfg));
        await _variants.AddAsync(Environment.MachineName, new WorkflowVariant(variantId, fileBaseId, name, paramsJson), ct);
        try
        {
            // Only explicit pins have rows, so copying the source rows naturally keeps inherited slots inherited.
            await _overrides.CopyConfigBindingsAsync(Environment.MachineName, baseCfg.Id, variantId, ct);
        }
        catch
        {
            // Do not leave a half-created variant if its model intent could not be copied.
            await _variants.DeleteAsync(Environment.MachineName, variantId, ct);
            throw;
        }
        // Flush the variants snapshot and await its rebuild, which re-reads and folds the new variant into the in-memory
        // catalog — so the caller (and the sync submit path) sees it immediately, without the old inline re-read/push.
        _snapshots.Variants.Invalidate();
        _snapshots.Bindings.Invalidate();
        _ = await _snapshots.Variants.GetAsync(ct);
        _ = await _snapshots.Bindings.GetAsync(ct);
        return variantId;
    }

    /// <inheritdoc/>
    public async Task DeleteVariantAsync(string variantId, CancellationToken ct)
    {
        // A shipped file config cannot be deleted — only a DB-backed variant. Refuse rather than silently no-op, so
        // the UI never offers delete on something it can't remove.
        if (!_catalog.IsVariant(variantId))
        {
            throw new ArgumentException($"'{variantId}' is not a deletable variant.", nameof(variantId));
        }

        await _variants.DeleteAsync(Environment.MachineName, variantId, ct);
        // Its per-variant tweaks are overrides keyed on the variant id; drop them so they can't outlive it.
        await _overrides.ClearOverridesAsync(Environment.MachineName, variantId, ct);
        await _overrides.ClearConfigBindingsAsync(Environment.MachineName, variantId, ct);
        // Delete touched BOTH stores — flush both snapshots and await their rebuilds so the removal is observed.
        _snapshots.Variants.Invalidate();
        _snapshots.ParamOverrides.Invalidate();
        _snapshots.Bindings.Invalidate();
        _ = await _snapshots.Variants.GetAsync(ct);
        _ = await _snapshots.ParamOverrides.GetAsync(ct);
        _ = await _snapshots.Bindings.GetAsync(ct);
    }

    /// <summary>The shipped-file id a config is ultimately rooted at: itself when it is a file, else the base its
    /// variant row names (a variant's base is always a file, so this is one hop, never a chain).</summary>
    private async Task<string> FileRootOfAsync(string configId, CancellationToken ct)
    {
        if (!_catalog.IsVariant(configId))
        {
            return configId;
        }

        IReadOnlyList<WorkflowVariant> rows = await _variants.VariantsAsync(Environment.MachineName, ct);
        return FileRootOf(configId, isVariant: true, rows);
    }

    /// <summary>Resolve a variant to its shipped-file root. A catalog/repository disagreement is an invariant failure,
    /// not permission to treat the variant id as a filename root and read/write the wrong configuration.</summary>
    internal static string FileRootOf(string configId, bool isVariant, IReadOnlyList<WorkflowVariant> rows)
    {
        if (!isVariant)
        {
            return configId;
        }

        return rows.FirstOrDefault(r => string.Equals(r.VariantId, configId, StringComparison.OrdinalIgnoreCase))?.BaseConfigId
            ?? throw new InvalidOperationException(
                $"Workflow variant '{configId}' exists in the catalog but its persisted variant row is missing.");
    }

    /// <summary>The first free <c>&lt;base&gt;-2</c>, <c>&lt;base&gt;-3</c>… id — unique against both the shipped files
    /// and the variants already on this machine (an exact-id check, not <see cref="WorkflowCatalog.FindConfig"/>'s
    /// loose match, which a numbered suffix would always satisfy against its own base).</summary>
    private string NextVariantId(string baseId)
    {
        for (int n = 2; ; n++)
        {
            string candidate = $"{baseId}-{n}";
            if (!_catalog.HasConfigId(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>Snapshot the base's EFFECTIVE parameters at copy time: each param's value with this machine's override
    /// applied, so the variant starts identical to the base as it stands and stays frozen against later base edits.</summary>
    private Dictionary<string, JsonElement> SnapshotParams(WorkflowConfiguration baseCfg)
    {
        IReadOnlyDictionary<string, JsonElement> overrides = _catalog.ParamOverridesFor(baseCfg.Id);
        Dictionary<string, JsonElement> snap = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, ConfigParam param) in baseCfg.Params)
        {
            snap[key] = overrides.TryGetValue(key, out JsonElement ov) ? ov.Clone() : JsonSerializer.SerializeToElement(param.Value);
        }

        return snap;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<WorkflowDescriptor>> ListEligibleAsync(CancellationToken ct)
    {
        // Pure in-memory projection over the snapshot sources (#201): ZERO ComfyUI round trips and ZERO SQL reads on
        // this path. Each read blocks only if its source is mid-rebuild; a ComfyUI-unreachable fault rethrows the
        // loader's HttpRequestException, which the caller maps to a 502 exactly as the live probes did.
        ComfyFilesByKind presentFiles = await _probes.FilesByKind.GetAsync(ct);
        _ = await _snapshots.Bindings.GetAsync(ct);
        IReadOnlySet<string> presentNodes = (await _probes.PresentNodes.GetAsync(ct)).Nodes;
        // ParamOverrides and Variants are consumed through the in-memory catalog (their rebuilds do the push).
        // Awaiting them here guarantees those pushes have landed before AllConfigs()/ParamOverridesFor() read below.
        _ = await _snapshots.ParamOverrides.GetAsync(ct);
        _ = await _snapshots.Variants.GetAsync(ct);

        List<(WorkflowConfiguration cfg, IWorkflow wf)> eligible = [];
        foreach (WorkflowConfiguration cfg in _catalog.AllConfigs())
        {
            IWorkflow? wf = _registry.Find(cfg.WorkflowName);
            if (wf is null)
            {
                continue;
            }

            // A model workflow with no checkpoint is misconfigured; a model-free one (quantizer) is fine.
            if (wf.RequiresModel && string.IsNullOrEmpty(cfg.Requirements.Checkpoint))
            {
                continue;
            }
            // A slot is satisfied when this machine has BOUND a file to it and that file is still present. Shipping
            // the filename in the catalogue would make anyone whose copy is named differently fail this check and
            // lose the workflow with no explanation.
            // A configuration names a model in two places. Consulting only `requirements` would let a configuration
            // whose params model ref is unbound appear in the picker and then fail at submit with an empty filename
            // reaching a loader — wan22-i2v-a14b names its second MoE expert (unet_low) nowhere but params. One rule
            // for both: a slot the configuration ASKS FOR must be bound to a file this renderer still has. A param it
            // does not set is absent by choice and asks for nothing.
            bool ok = cfg.Requirements.All().Concat(_catalog.ModelRefSlots(wf, cfg)).All(id =>
            {
                Requirement? r = _catalog.FindRequirement(id);
                if (r is null)
                {
                    return false;
                }
                // A node requirement is met by ComfyUI having the node registered. It has no file, so it can never
                // have a binding — demanding one would exclude EVERY configuration that declares a node pack from the
                // picker, however well installed the pack is, while /forge/catalog/status reports it ready.
                if (!string.IsNullOrWhiteSpace(r.Node))
                {
                    return presentNodes.Contains(r.Node);
                }

                string? effective = _catalog.ResolveBinding(cfg.Id, id).EffectiveFile;
                return effective is not null
                    && presentFiles.ByKind.TryGetValue(r.Kind, out IReadOnlyList<string>? present)
                    && present.Contains(effective, StringComparer.OrdinalIgnoreCase);
            });
            if (!ok)
            {
                continue;
            }

            eligible.Add((cfg, wf));
        }

        // Per-model MATCHED average runtime (machine-specific: recent renders whose parameter signature equals the
        // config's most recent render — never a blend across signatures). Purely a decoration on rows that are already
        // decided, so a timings hiccup must not take the model picker down with it — the degrade-with-log catch stays
        // AT THIS CONSUMER around the snapshot read (#200): a faulted GenTimingAverages lists workflows without ETAs,
        // it does not 502. Do not widen this catch's scope.
        GenTimingAverages avgs;
        try
        {
            avgs = await _timings.GetAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Recent-average lookup failed; listing workflows without their ETAs.");
            avgs = new GenTimingAverages(new Dictionary<string, double>());
        }

        // Validate every masked-sibling link across the WHOLE catalogue and collect the ids that are the target of one
        // — a broken link is an authoring error regardless of eligibility, and a target must be marked hidden.
        HashSet<string> hiddenTargets = MaskLinkTargets();
        hiddenTargets.UnionWith(ReferenceLinkTargets());

        // Shared display name (within a RESOLVED kind + effect + edit section) → keep the first. The resolved kind is
        // important here: the workflow class only says Edit, while media/config capabilities refine that to Upscale,
        // Animate, VideoEdit, etc. Using the class kind collapsed SeedVR2's still-image Upscale and video-source
        // VideoEdit into one row; whichever config happened to enumerate first made the other picker lose SeedVR2.
        return [.. eligible
            .GroupBy(e => PickerIdentity(e.cfg, e.wf))
            .Select(g => g.First())
            .Select(e => ToDescriptor(e.cfg, e.wf,
                avgs.SecondsFor(e.cfg.Id) is double s ? (int?)Math.Round(s) : null,
                hiddenTargets.Contains(e.cfg.Id)))];
    }

    /// <summary>The visible picker identity used to collapse true aliases without merging workflows routed to
    /// different media tabs. Kind must be the fully resolved kind, not the coarse kind declared by the class.</summary>
    internal static string PickerIdentity(WorkflowConfiguration cfg, IWorkflow wf) =>
        $"{ResolveKind(cfg, wf)} {cfg.EffectType} {cfg.EditGroup} {(cfg.FriendlyName ?? cfg.Id).ToLowerInvariant()}";

    /// <summary>
    /// Validate every configuration's <see cref="WorkflowConfiguration.MaskWorkflow"/> link and return the set of ids
    /// that are the TARGET of one (suppressed from the picker but kept in the payload). A link is only valid when the
    /// SOURCE is a plain Edit config and the TARGET exists, resolves to <see cref="WorkflowKind.Inpaint"/>, and
    /// preserves composition — anything else is a boot/authoring error and THROWS rather than silently dropping the
    /// link (which would leave the client unable to route a mask and give no reason).
    /// </summary>
    private HashSet<string> MaskLinkTargets() => ValidateMaskLinks(_catalog.AllConfigs(), _registry);

    /// <summary>The validation body of <see cref="MaskLinkTargets"/>, pure over its inputs so it is unit-testable
    /// without the snapshot machinery <see cref="ListEligibleAsync"/> needs.</summary>
    internal static HashSet<string> ValidateMaskLinks(IReadOnlyList<WorkflowConfiguration> configs, WorkflowRegistry registry)
    {
        Dictionary<string, WorkflowConfiguration> byId = configs.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
        HashSet<string> targets = new(StringComparer.OrdinalIgnoreCase);
        foreach (WorkflowConfiguration cfg in configs)
        {
            if (string.IsNullOrEmpty(cfg.MaskWorkflow))
            {
                continue;
            }

            IWorkflow? srcWf = registry.Find(cfg.WorkflowName);
            if (srcWf is null || ResolveKind(cfg, srcWf) != WorkflowKind.Edit)
            {
                throw new InvalidOperationException(
                    $"Configuration '{cfg.Id}' declares mask_workflow '{cfg.MaskWorkflow}', but only a plain Edit "
                    + "configuration may link a masked sibling.");
            }

            if (!byId.TryGetValue(cfg.MaskWorkflow, out WorkflowConfiguration? target))
            {
                throw new InvalidOperationException(
                    $"Configuration '{cfg.Id}' declares mask_workflow '{cfg.MaskWorkflow}', which is not a known configuration.");
            }

            IWorkflow? targetWf = registry.Find(target.WorkflowName);
            if (targetWf is null || ResolveKind(target, targetWf) != WorkflowKind.Inpaint || !targetWf.PreservesComposition)
            {
                throw new InvalidOperationException(
                    $"Configuration '{cfg.Id}' declares mask_workflow '{cfg.MaskWorkflow}', but the target is not a "
                    + "composition-preserving Inpaint workflow — the only kind that consumes a mask in-graph.");
            }

            _ = targets.Add(target.Id);
        }

        return targets;
    }

    /// <summary>Validate browser-routed first/last-frame → reference-conditioned sibling links and return their
    /// hidden target ids. Both configs must resolve to image→video, accept endpoint frames, and the target must expose
    /// at least one reference kind. The API remains explicit: this link is catalog metadata for the browser.</summary>
    private HashSet<string> ReferenceLinkTargets() => ValidateReferenceLinks(_catalog.AllConfigs(), _registry);

    internal static HashSet<string> ValidateReferenceLinks(IReadOnlyList<WorkflowConfiguration> configs, WorkflowRegistry registry)
    {
        Dictionary<string, WorkflowConfiguration> byId = configs.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
        HashSet<string> targets = new(StringComparer.OrdinalIgnoreCase);
        foreach (WorkflowConfiguration cfg in configs)
        {
            if (string.IsNullOrEmpty(cfg.ReferenceWorkflow))
            {
                continue;
            }

            IWorkflow? sourceWorkflow = registry.Find(cfg.WorkflowName);
            if (sourceWorkflow is null || ResolveKind(cfg, sourceWorkflow) != WorkflowKind.Animate || !sourceWorkflow.SupportsEndFrame)
            {
                throw new InvalidOperationException(
                    $"Configuration '{cfg.Id}' declares reference_workflow '{cfg.ReferenceWorkflow}', but only a first/last-frame Animate configuration may link a reference sibling.");
            }

            if (!byId.TryGetValue(cfg.ReferenceWorkflow, out WorkflowConfiguration? target) ||
                string.Equals(cfg.Id, cfg.ReferenceWorkflow, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Configuration '{cfg.Id}' declares reference_workflow '{cfg.ReferenceWorkflow}', which is not a distinct known configuration.");
            }

            IWorkflow? targetWorkflow = registry.Find(target.WorkflowName);
            if (targetWorkflow is null || ResolveKind(target, targetWorkflow) != WorkflowKind.Animate ||
                !targetWorkflow.SupportsEndFrame || target.Card.EditReferenceTypes.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Configuration '{cfg.Id}' declares reference_workflow '{cfg.ReferenceWorkflow}', but the target is not a reference-capable first/last-frame Animate workflow.");
            }

            _ = targets.Add(target.Id);
        }

        return targets;
    }

    /// <inheritdoc/>
    public WorkflowSettings? GetSettings(string? configId)
    {
        WorkflowConfiguration? cfg = _catalog.FindConfig(configId);
        if (cfg is null)
        {
            return null;
        }

        IWorkflow? wf = _registry.Find(cfg.WorkflowName);
        if (wf is null)
        {
            return null;
        }

        IReadOnlyDictionary<string, JsonElement> overrides = _catalog.ParamOverridesFor(cfg.Id);
        bool allowUntrainedResolution = wf.Kind == WorkflowKind.Generate
            && wf.Media == WorkflowMedia.Image
            && WorkflowResolutionPolicy.IsEnabled(overrides);
        bool allowUntrainedFrameCounts = wf.Media == WorkflowMedia.Video
            && cfg.Params.ContainsKey(WorkflowParamKeys.Length)
            && WorkflowFrameCountPolicy.IsEnabled(overrides);

        // Every parameter the CONFIGURATION sets, not just the ones exposed per generation. The exposed ones are
        // what a caller may vary on a single render; these are what this machine renders with by default, which is
        // a different question and the one this page answers.
        List<ConfigSetting> settings = [.. cfg.Params
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            // A model-ref param is a foreign key into the requirements/slot table — a VAE, a LoRA, an extra expert. It
            // is not a free value this page may edit; the model it points at is chosen with the slot picker in "Models
            // for this machine", not by typing a filename. Surface none of them here (issue: editable VAE/LoRA fields).
            .Where(kv => ParamSpecFor(wf, kv.Key) is not { IsModelRef: true })
            .Select(kv =>
            {
                ParamSpec? spec = ParamSpecFor(wf, kv.Key)
                    ?? (string.Equals(kv.Key, WorkflowParamKeys.PromptTemplate, StringComparison.OrdinalIgnoreCase)
                        ? PromptTemplates.Schema
                        : null);
                bool arbitraryFrames = allowUntrainedFrameCounts
                    && string.Equals(kv.Key, WorkflowParamKeys.Length, StringComparison.OrdinalIgnoreCase);
                return new ConfigSetting(
                    kv.Key,
                    spec?.Label ?? kv.Key,
                    spec?.Help,
                    // `aspect` is its own shape — a map of aspect name to [width, height] — and the page draws it
                    // as three width/height pairs rather than asking anyone to type JSON.
                    string.Equals(kv.Key, WorkflowParamKeys.Aspect, StringComparison.OrdinalIgnoreCase)
                        ? ControlTokens.Aspect
                        : (spec?.Type ?? ParamType.String).ToString().ToLowerInvariant(),
                    arbitraryFrames ? 1 : kv.Value.Min ?? spec?.Min,
                    arbitraryFrames ? null : kv.Value.Max ?? spec?.Max,
                    arbitraryFrames ? 1 : kv.Value.Step ?? spec?.Step,
                    spec?.Choices,
                    kv.Value.Value,
                    overrides.TryGetValue(kv.Key, out JsonElement o) ? o : null,
                    kv.Value.Visibility.Token());
            })];

        // A field may declare a wider hard range, but enabling it is a WORKFLOW decision for this machine — not a
        // checkbox repeated on every generation form. Surface one synthetic bool beside each capable field. Its value
        // is stored through the same per-config override path as custom size and the tag generator.
        foreach ((string paramKey, ConfigParam parameter) in cfg.Params.Where(p => p.Value.RangeOverride is not null))
        {
            // Stepped video length now has one cross-workflow policy below. Keep reading the old H3 override as a
            // migration fallback, but do not render two overlapping checkboxes.
            if (wf.Media == WorkflowMedia.Video
                && string.Equals(paramKey, WorkflowParamKeys.Length, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (parameter.RangeOverride is not { } alternate)
            {
                continue;
            }

            string settingKey = WorkflowRangeOverridePolicy.SettingKey(paramKey);
            ConfigSetting allowExtended = new(
                settingKey,
                alternate.Label,
                alternate.Warning,
                ControlTokens.Bool, null, null, null, null,
                Shipped: false,
                Override: overrides.TryGetValue(settingKey, out JsonElement value) ? (object?)value : null);

            int paramIndex = settings.FindIndex(s => string.Equals(s.Key, paramKey, StringComparison.OrdinalIgnoreCase));
            if (paramIndex >= 0)
            {
                settings.Insert(paramIndex + 1, allowExtended);
            }
            else
            {
                settings.Add(allowExtended);
            }
        }

        // A per-machine "default LoRA folder" for this workflow's composer picker. It is NOT a config/graph param, so
        // it's surfaced here as a synthetic string setting the settings page renders and saves through the same
        // override path (param.targetLoraFolder) as every other setting — no new settings UI needed.
        if (wf.Kind == WorkflowKind.Generate && wf.Media == WorkflowMedia.Image)
        {
            settings.Add(new ConfigSetting(
                SettingKeys.TargetLoraFolder, "Default LoRA folder",
                "The composer's LoRA picker opens to this subfolder for this workflow. Blank = a folder matching the workflow, else all LoRAs.",
                ControlTokens.String, null, null, null, null,
                Shipped: null,
                Override: overrides.TryGetValue(SettingKeys.TargetLoraFolder, out JsonElement lf) ? (object?)lf : null));
        }

        // Random prompt is a per-WORKFLOW capability, not a statement that the model uses booru prompt syntax. A prose
        // model can opt in and consume the generated comma-separated list as ordinary text. Existing tag-speaking
        // generate workflows ship ON so this migration preserves today's UI; every other generate workflow ships OFF
        // and can be enabled here for this machine.
        if (wf.Kind == WorkflowKind.Generate)
        {
            settings.Add(new ConfigSetting(
                SettingKeys.TagGenerator, "Tag generator on the generation page",
                "When on, the generation page offers the Random prompt slider for this workflow.",
                ControlTokens.Bool, null, null, null, null,
                Shipped: cfg.Card.Tagging?.Tags == true,
                Override: overrides.TryGetValue(SettingKeys.TagGenerator, out JsonElement tg) ? (object?)tg : null));
        }

        // A per-machine "custom size" toggle for this workflow's composer. Like the LoRA folder above it is not a
        // config/graph param, so it's surfaced as a synthetic bool setting the settings page renders and saves through
        // the same override path (param.customSize). Only image generators can offer a Custom aspect. When on, the
        // descriptor's CustomSizeEnabled flips and the composer shows the Custom width/height boxes.
        //
        // It governs the aspect/render-size editor, so it is placed immediately after the aspect setting rather than
        // appended last — the settings page renders in list order, and the toggle belongs next to the sizes it drives.
        if (wf.Kind == WorkflowKind.Generate && wf.Media == WorkflowMedia.Image)
        {
            ConfigSetting customSize = new(
                SettingKeys.CustomSize, "Custom size on the generation page",
                "When on, the generation page offers a Custom aspect with width/height boxes for this workflow, validated against its resolution envelope. Allowing untrained resolutions also turns Custom sizing on.",
                ControlTokens.Bool, null, null, null, null,
                Shipped: null,
                Override: overrides.TryGetValue(SettingKeys.CustomSize, out JsonElement cs) ? (object?)cs : null);

            int aspectIndex = settings.FindIndex(s => string.Equals(s.Type, ControlTokens.Aspect, StringComparison.Ordinal));
            if (aspectIndex >= 0)
            {
                settings.Insert(aspectIndex + 1, customSize);
            }
            else
            {
                settings.Add(customSize);
            }

            ConfigSetting untrainedResolution = new(
                WorkflowResolutionText.SettingKey,
                WorkflowResolutionText.Label,
                WorkflowResolutionText.Warning,
                ControlTokens.Bool, null, null, null, null,
                Shipped: false,
                Override: overrides.TryGetValue(WorkflowResolutionText.SettingKey, out JsonElement ur)
                    ? (object?)ur
                    : null);
            int customIndex = settings.FindIndex(s =>
                string.Equals(s.Key, SettingKeys.CustomSize, StringComparison.OrdinalIgnoreCase));
            settings.Insert(customIndex >= 0 ? customIndex + 1 : settings.Count, untrainedResolution);
        }

        // Every stepped video workflow gets one machine-owned escape hatch. It removes both the configured trained
        // range and temporal-cadence snap; the generated request carries only the resulting positive frame count.
        if (wf.Media == WorkflowMedia.Video && cfg.Params.ContainsKey(WorkflowParamKeys.Length))
        {
            object? frameOverride = overrides.TryGetValue(WorkflowFrameCountText.SettingKey, out JsonElement fc)
                ? fc
                : overrides.TryGetValue(
                    WorkflowRangeOverridePolicy.SettingKey(WorkflowParamKeys.Length), out JsonElement legacy)
                    ? legacy
                    : null;
            ConfigSetting untrainedFrames = new(
                WorkflowFrameCountText.SettingKey,
                WorkflowFrameCountText.Label,
                WorkflowFrameCountText.Warning,
                ControlTokens.Bool, null, null, null, null,
                Shipped: false,
                Override: frameOverride);
            int lengthIndex = settings.FindIndex(s =>
                string.Equals(s.Key, WorkflowParamKeys.Length, StringComparison.OrdinalIgnoreCase));
            settings.Insert(lengthIndex >= 0 ? lengthIndex + 1 : settings.Count, untrainedFrames);
        }

        // The declared envelope travels with the settings, so the size boxes are bounded by what the model says
        // it supports instead of by a guess.
        ModelResolution? r = cfg.Resolution ?? wf.ResolutionEnvelope;
        return new WorkflowSettings(
            cfg.Id, cfg.FriendlyName ?? cfg.Id, settings,
            r is null ? null : new ResolutionEnvelope(r.MinW, r.MinH, r.MaxW, r.MaxH, r.Step),
            allowUntrainedResolution,
            allowUntrainedFrameCounts);
    }

    /// <inheritdoc/>
    public PromptingGuide? GetGuide(string? configId)
    {
        ModelCard? card = _catalog.ResolveCard(configId);
        return card is null ? null : ToGuide(card);
    }

    /// <inheritdoc/>
    public IReadOnlyList<PromptingGuide> AllGuides() => [.. _catalog.AllCards().Select(ToGuide)];

    private static WorkflowTagging? ToTagging(TaggingInfo? t) =>
        t is null ? null : new WorkflowTagging(t.Tags, t.Artists, t.KeepArtistMarker, t.UnderscoresToSpaces);

    /// <summary>
    /// The specific kind of ONE configuration, resolved once and emitted as the single authoritative field the client
    /// badges and routes on (#163). The class knows only <see cref="WorkflowKind.Generate"/>/<see cref="WorkflowKind.Edit"/>
    /// and the dedicated <see cref="WorkflowKind.Inpaint"/>/<see cref="WorkflowKind.Outpaint"/>; this folds in the
    /// config's media, edit-group and effect-type to name the rest (Animate/VideoEdit/Redraw/Upscale/Effect), so the
    /// edit page never re-derives a tab from a workflow name, a magic-string edit-group, or an effect-type presence.
    /// <para>Resolved even when the workflow is unavailable — it reads the registered class and the config, neither of
    /// which depends on a slot file being bound — so a disabled edit workflow still badges its true kind.</para>
    /// </summary>
    internal static WorkflowKind ResolveKind(WorkflowConfiguration cfg, IWorkflow wf) => wf.Kind switch
    {
        // The class already names these; take them verbatim.
        WorkflowKind.Generate => WorkflowKind.Generate,
        WorkflowKind.Inpaint => WorkflowKind.Inpaint,
        WorkflowKind.Outpaint => WorkflowKind.Outpaint,
        // A plain edit class — the specific kind is a property of THIS configuration.
        _ => ResolveEditKind(cfg, wf),
    };

    private static WorkflowKind ResolveEditKind(WorkflowConfiguration cfg, IWorkflow wf)
    {
        if (wf.SourceMedia == WorkflowMedia.Video)
        {
            return WorkflowKind.VideoEdit;   // consumes a clip (temporal restoration or a deterministic frame effect)
        }

        if (wf.Media == WorkflowMedia.Video)
        {
            return WorkflowKind.Animate;     // still source, video output (image→video)
        }

        if (string.Equals(cfg.EditGroup, EditGroups.Redraw, StringComparison.Ordinal))
        {
            return WorkflowKind.Redraw;
        }

        if (string.Equals(cfg.EditGroup, EditGroups.Upscale, StringComparison.Ordinal))
        {
            return WorkflowKind.Upscale;
        }

        return cfg.EffectType is { Length: > 0 } ? WorkflowKind.Effect : WorkflowKind.Edit;
    }

    /// <summary>The client token for a resolved kind — the value the badge and the edit-page tab routing read.</summary>
    internal static string KindToken(WorkflowKind kind) => kind switch
    {
        WorkflowKind.Generate => WorkflowKindTokens.Generate,
        WorkflowKind.Edit => WorkflowKindTokens.Edit,
        WorkflowKind.Inpaint => WorkflowKindTokens.Inpaint,
        WorkflowKind.Outpaint => WorkflowKindTokens.Outpaint,
        WorkflowKind.Redraw => WorkflowKindTokens.Redraw,
        WorkflowKind.Upscale => WorkflowKindTokens.Upscale,
        WorkflowKind.Effect => WorkflowKindTokens.Effect,
        WorkflowKind.Animate => WorkflowKindTokens.Animate,
        WorkflowKind.VideoEdit => WorkflowKindTokens.VideoEdit,
        _ => WorkflowKindTokens.Generate,
    };

    /// <summary>The config edit-group magic values the catalog promotes to their own kind.</summary>
    private static class EditGroups
    {
        public const string Redraw = "Redraw";
        public const string Upscale = "Upscale";
    }

    private WorkflowDescriptor ToDescriptor(
        WorkflowConfiguration cfg, IWorkflow wf, int? avgSeconds, bool hiddenFromPicker)
    {
        ModelCard c = cfg.Card;
        // This machine's overrides win here too. Reporting the SHIPPED value while rendering the overridden one
        // would put the composer and the graph into disagreement — the control would read 40 steps and produce 12.
        IReadOnlyDictionary<string, JsonElement> machine = _catalog.ParamOverridesFor(cfg.Id);
        bool allowUntrainedResolution = wf.Kind == WorkflowKind.Generate
            && wf.Media == WorkflowMedia.Image
            && WorkflowResolutionPolicy.IsEnabled(machine);
        List<WorkflowExposedParam> exposed = [.. cfg.Params
            .Where(kv => kv.Value.Visibility == ParamVisibility.Exposed)
            .Select(kv => ExposedParam(kv, wf, cfg, machine))];
        // The SHIPPED default-hidden-but-revealable set, projected exactly like the exposed one so a param a user
        // reveals renders through the same control path (including the length→seconds conversion). The per-user
        // reveal/hide overlay is applied client-side from the account's visibility prefs — this stays user-free.
        List<WorkflowExposedParam> revealable = [.. cfg.Params
            .Where(kv => kv.Value.Visibility == ParamVisibility.Hidden)
            .Select(kv => ExposedParam(kv, wf, cfg, machine))];
        bool canEdit = wf.Kind != WorkflowKind.Generate;
        WorkflowKind kind = ResolveKind(cfg, wf);
        // Base tags = what the card authored, plus tags DERIVED from capability: "Ref" for a workflow that accepts a
        // reference, "Inpaint" for an inpaint config. Deriving them (not hand-authoring per card) is what lets the
        // picker drop its Reference/Non-Reference section split and lets a masked Edit config read as inpaint-capable —
        // the tag IS the differentiator, and it can never drift from the actual capability.
        string[]? baseTags = ComposeBaseTags(
            c,
            takesReference: canEdit && c.EditReferenceTypes.Count > 0,
            isInpaint: kind == WorkflowKind.Inpaint);
        return new WorkflowDescriptor(
            Id: cfg.Id,
            Workflow: cfg.WorkflowName,
            Kind: KindToken(kind),
            Media: wf.Media == WorkflowMedia.Video ? "video" : "image",
            SourceMedia: wf.SourceMedia == WorkflowMedia.Video ? "video" : "image",
            EffectType: cfg.EffectType,
            EditGroup: cfg.EditGroup,
            PromptDirectsMotion: wf.PromptDirectsMotion,
            PromptSemantics: wf.PromptSemantics switch
            {
                PromptSemantics.WholeImage => "whole_image",
                PromptSemantics.MaskedRegion => "masked_region",
                _ => "instruction",
            },
            TakesPrompt: wf.TakesPrompt,
            SupportsLastFrame: wf.SupportsEndFrame,
            HasAudio: HasAudio(cfg, wf),
            FriendlyName: cfg.FriendlyName ?? c.FriendlyName,
            ShortName: cfg.ShortName,
            Default: cfg.Default,
            AvgSeconds: avgSeconds,
            ExposedParams: exposed,
            HiddenParams: revealable,
            CanEdit: canEdit,
            Reference: canEdit ? BuildReference(c) : null,
            Card: new WorkflowCardSummary(
                FriendlyName: c.FriendlyName,
                Architecture: c.Architecture,
                Summary: c.Summary,
                UseCases: c.UseCases is { Length: > 0 } ? c.UseCases : null,
                PromptFormat: c.PromptFormat,
                RequiredPrefix: c.RequiredPrefix,
                PromptGuidance: c.PromptGuidance,
                Example: c.PromptExample,
                UiGoodFor: c.UiGoodFor,
                UiNote: c.UiNote,
                UiLink: c.UiLinkUrl is null ? null : new WorkflowLink(c.UiLinkText, c.UiLinkUrl),
                NsfwCapable: c.NsfwCapable,
                CommercialUse: c.CommercialUse,
                Speed: c.Speed,
                ExpectedGenSeconds: c.ExpectedGenSeconds,
                // The Edit tab (the instruction editors) never offers a user negative — the box is category-wide
                // suppressed here so no current or future edit config can re-expose it (#211). The model's own
                // default negative still applies via ComfyGraph.ComposeNegative; only the user-typed field is gone.
                NegativeSupported: kind == WorkflowKind.Edit ? false : c.NegativeSupported,
                EditUseCases: c.EditUseCases is { Length: > 0 } ? c.EditUseCases : null,
                Tagging: ToTagging(c.Tagging),
                Tags: baseTags),
            // The composer's LoRA picker opens to this folder for this workflow (per-machine override, Part H);
            // null falls back client-side to a folder matching the workflow, else the root.
            LoraFolder: OverrideString(machine, SettingKeys.TargetLoraFolder),
            // Opt-in per-machine toggle: when set, the composer offers a "Custom" aspect with width/height boxes for
            // this workflow. Read from the same per-machine override store as the settings toggle that sets it.
            CustomSizeEnabled: allowUntrainedResolution || OverrideBool(machine, SettingKeys.CustomSize),
            // Per-machine random-prompt toggle. Defaults on for the workflows whose tagging block enabled the feature
            // before the toggle existed; prose workflows remain off until explicitly enabled.
            TagGeneratorEnabled: EffectiveBool(machine, SettingKeys.TagGenerator, c.Tagging?.Tags == true),
            // A DB-backed duplicate, not a shipped file — the library marks it and offers Delete only on these.
            IsVariant: _catalog.IsVariant(cfg.Id),
            SupportsReferenceOnly: wf.SupportsReferenceOnly && BuildReference(c)?.MaxImages > 0,
            SupportsReferenceAspectWithSource: wf.SupportsReferenceAspectWithSource && BuildReference(c)?.MaxImages > 0,
            // Each config's aspect→[w,h] map travels to the composer, which writes a clicked shape's dims into its
            // width/height controls and submits the dims (#209). Null for a config with no aspect map.
            Aspects: RequestSize.BuildAspectMap(cfg, machine),
            // The masked sibling this Edit config routes to when a mask is drawn ("" = none), and whether this config
            // is itself the hidden target of a routing link (kept in the payload, filtered from the picker client-side).
            MaskWorkflow: cfg.MaskWorkflow,
            ReferenceWorkflow: cfg.ReferenceWorkflow,
            HiddenFromPicker: hiddenFromPicker);
    }

    /// <summary>One exposed parameter as the composer sees it. Normally a straight projection of the schema/config, but
    /// a stepped video model's <c>length</c> (frames) is offered in SECONDS instead (issue #194): the control's value,
    /// range and step are converted with the model's own <c>fps</c>, and the composer sends back
    /// <see cref="WorkflowParamKeys.DurationSeconds"/>, which <see cref="FrameNormalization"/> turns back into frames at
    /// enqueue. People think in "a 5-second clip", not "121 frames".</summary>
    internal static WorkflowExposedParam ExposedParam(
        KeyValuePair<string, ConfigParam> kv, IWorkflow wf, WorkflowConfiguration cfg,
        IReadOnlyDictionary<string, JsonElement> machine)
    {
        ParamSpec? spec = ParamSpecFor(wf, kv.Key)
            ?? (string.Equals(kv.Key, WorkflowParamKeys.PromptTemplate, StringComparison.OrdinalIgnoreCase)
                ? PromptTemplates.Schema
                : null);
        object? value = machine.TryGetValue(kv.Key, out JsonElement o) ? o : kv.Value.Value;
        bool videoLength = wf.Media == WorkflowMedia.Video
            && string.Equals(kv.Key, WorkflowParamKeys.Length, StringComparison.OrdinalIgnoreCase)
            && cfg.Params.ContainsKey(WorkflowParamKeys.Length);
        bool allowUntrainedFrameCounts = videoLength && WorkflowFrameCountPolicy.IsEnabled(machine);
        bool explicitFramePolicy = videoLength && machine.ContainsKey(WorkflowFrameCountText.SettingKey);
        ConfigParamRangeOverride? activeRange = !explicitFramePolicy
            && WorkflowRangeOverridePolicy.IsEnabled(machine, kv.Key)
                ? kv.Value.RangeOverride
                : null;

        if (string.Equals(kv.Key, WorkflowParamKeys.Length, StringComparison.OrdinalIgnoreCase)
            && wf.FrameRule is { } fr
            && TryFps(cfg, machine, out double fps))
        {
            double frames = ParamsCodec.AsDouble(value);
            double? minFrames = kv.Value.Min ?? spec?.Min;
            double? maxFrames = kv.Value.Max ?? spec?.Max;
            return new WorkflowExposedParam(
                WorkflowParamKeys.DurationSeconds,
                ParamType.Double.ToString().ToLowerInvariant(),
                Math.Round(frames / fps, 2),
                allowUntrainedFrameCounts ? Math.Max(0.01, Math.Round(1 / fps, 2)) : Math.Round((minFrames ?? fr.Base) / fps, 2),
                allowUntrainedFrameCounts ? null : maxFrames is { } hi ? Math.Round(hi / fps, 2) : null,
                // Step by the model's real frame cadence (in hundredths of a second), not a flat step — so the control
                // shows the exact increments this model actually renders instead of pretending to a resolution it
                // hasn't got. The exact frame is settled by the nearest-cadence snap at generation.
                allowUntrainedFrameCounts ? 0.01 : Math.Max(0.01, Math.Round(fr.Step / fps, 2)),
                "Length (seconds)",
                allowUntrainedFrameCounts
                    ? WorkflowFrameCountText.Warning
                    : "Steps by this model’s frame cadence; the exact length snaps to the nearest it can render.",
                null,
                allowUntrainedFrameCounts ? null : ProjectRangeOverride(activeRange, fps));
        }

        return new WorkflowExposedParam(
            kv.Key,
            (spec?.Type ?? ParamType.String).ToString().ToLowerInvariant(),
            value,
            allowUntrainedFrameCounts ? 1 : kv.Value.Min ?? spec?.Min,
            allowUntrainedFrameCounts ? null : kv.Value.Max ?? spec?.Max,
            allowUntrainedFrameCounts ? 1 : kv.Value.Step ?? spec?.Step,
            spec?.Label ?? kv.Key,
            allowUntrainedFrameCounts ? WorkflowFrameCountText.Warning : spec?.Help,
            spec?.Choices,
            allowUntrainedFrameCounts ? null : ProjectRangeOverride(activeRange));
    }

    private static WorkflowParamRangeOverride? ProjectRangeOverride(
        ConfigParamRangeOverride? alternate, double? divisor = null)
    {
        if (alternate is null)
        {
            return null;
        }

        double? Project(double? value) => value is double n
            ? divisor is double d ? Math.Round(n / d, 2) : n
            : null;
        return new WorkflowParamRangeOverride(
            Project(alternate.Min), Project(alternate.Max), alternate.Warning);
    }

    /// <summary>Ordinary workflow schema plus the four shared quality settings for workflows that opt in.</summary>
    private static ParamSpec? ParamSpecFor(IWorkflow wf, string key) =>
        wf.Schema.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase))
        ?? (wf.SupportsEditQuality ? EditQuality.Spec(key) : null);

    /// <summary>This machine's effective frames-per-second for a config (machine override, else the config's scalar),
    /// or false when none is declared — a video model without an fps can't have its length shown in seconds.</summary>
    private static bool TryFps(
        WorkflowConfiguration cfg, IReadOnlyDictionary<string, JsonElement> machine, out double fps)
    {
        if (machine.TryGetValue(WorkflowParamKeys.Fps, out JsonElement mo)
            && mo.ValueKind == JsonValueKind.Number && mo.TryGetDouble(out fps) && fps > 0)
        {
            return true;
        }

        if (cfg.Params.TryGetValue(WorkflowParamKeys.Fps, out ConfigParam? cp))
        {
            fps = ParamsCodec.AsDouble(cp.Value);
            if (fps > 0)
            {
                return true;
            }
        }

        fps = 0;
        return false;
    }

    /// <summary>Settings-page override keys (persisted per machine).</summary>
    private static class SettingKeys
    {
        public const string ParamPrefix = "param.";
        public const string EditQualityPrefix = "param.edit_quality";
        /// <summary>The per-machine setting key for a workflow's default LoRA folder (a plain string override, not a graph
        /// parameter — no workflow reads it; the composer's picker does).</summary>
        public const string TargetLoraFolder = "targetLoraFolder";

        /// <summary>The settings-page override key for a configuration's render-size aspect map.</summary>
        public const string AspectOverride = "param.aspect";

        /// <summary>The settings-page override key for a configuration's prompt template.</summary>
        public const string PromptTemplate = ParamPrefix + WorkflowParamKeys.PromptTemplate;

        /// <summary>The per-machine setting key for whether the composer offers a Custom aspect (width/height boxes) for
        /// this workflow (a plain bool override, not a graph parameter).</summary>
        public const string CustomSize = "customSize";

        /// <summary>The per-machine setting key for whether the composer offers the built-in random tag generator for
        /// this workflow. Independent of the card's booru tagging syntax.</summary>
        public const string TagGenerator = "tagGenerator";

        /// <summary>The per-machine setting key for allowing positive image-generation dimensions outside the model's
        /// documented training envelope.</summary>
        public const string UntrainedResolution = ParamPrefix + WorkflowResolutionText.SettingKey;

        /// <summary>The stored setting key for arbitrary positive video frame counts.</summary>
        public const string UntrainedFrameCounts = ParamPrefix + WorkflowFrameCountText.SettingKey;
    }

    /// <summary>Control tokens the settings page uses to pick an input widget.</summary>
    private static class ControlTokens
    {
        /// <summary>The lowercased CLR-type control token for a plain string setting (matches
        /// <c>ParamType.String.ToString().ToLowerInvariant()</c>).</summary>
        public const string String = "string";

        /// <summary>The control token for a boolean toggle (matches <c>ParamType.Bool.ToString().ToLowerInvariant()</c>);
        /// the settings page renders it as a checkbox.</summary>
        public const string Bool = "bool";

        /// <summary>The control token for the render-size map; the settings page draws it as width/height pairs.</summary>
        public const string Aspect = "aspect";
    }

    /// <summary>Read a string-valued per-machine param override, or null when unset/blank.</summary>
    private static string? OverrideString(IReadOnlyDictionary<string, System.Text.Json.JsonElement> machine, string key)
    {
        if (!machine.TryGetValue(key, out JsonElement v))
        {
            return null;
        }

        string? s = v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : v.ToString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    /// <summary>Read a boolean per-machine override, accepting either a real JSON boolean or the string "true"/"false"
    /// (the settings page persists it as a string). Missing/blank/anything-else is false.</summary>
    private static bool OverrideBool(IReadOnlyDictionary<string, System.Text.Json.JsonElement> machine, string key)
        => EffectiveBool(machine, key, false);

    /// <summary>Read a boolean per-machine override, falling back to the workflow's shipped value when absent or
    /// malformed. Accepts real JSON booleans and the settings page's string representation.</summary>
    private static bool EffectiveBool(
        IReadOnlyDictionary<string, System.Text.Json.JsonElement> machine, string key, bool fallback)
    {
        if (!machine.TryGetValue(key, out JsonElement v))
        {
            return fallback;
        }

        if (v.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
        {
            return v.GetBoolean();
        }

        return v.ValueKind == System.Text.Json.JsonValueKind.String && bool.TryParse(v.GetString(), out bool b)
            ? b
            : fallback;
    }

    /// <summary>Audio is normally a workflow-class property. LTX-2.5 is the configuration-specific exception because
    /// the same workflow class also serves older video-only LTX configurations; its locked audio-VAE model ref is the
    /// authoritative opt-in.</summary>
    private static bool HasAudio(WorkflowConfiguration cfg, IWorkflow? wf) =>
        (wf?.HasAudio ?? false) || cfg.Params.ContainsKey(WorkflowParamKeys.AudioVae);

    private static PromptingGuide ToGuide(ModelCard c) => new(
        Name: c.Name,
        FriendlyName: c.FriendlyName,
        Architecture: c.Architecture,
        CanEdit: c.EditUseCases.Length > 0 || c.EditReferenceTypes.Count > 0,
        Format: c.PromptFormat,
        Overview: c.PromptOverview ?? c.PromptGuidance,
        Guidance: c.PromptGuidance,
        Instructions: c.PromptInstructions,
        RequiredPrefix: c.RequiredPrefix,
        NegativeSupported: c.NegativeSupported,
        NegativeGuidance: c.NegativeGuidance,
        Do: c.PromptDo is { Length: > 0 } d ? d : null,
        Dont: c.PromptDont is { Length: > 0 } dn ? dn : null,
        Examples: c.PromptExamples is { Length: > 0 } ex ? ex : null,
        Source: c.PromptSource,
        MaxReferenceImages: c.EditReferenceTypes.FirstOrDefault(t => t.Kind == ReferenceKindNames.Image)?.Max ?? 0,
        ReferenceTechnique: c.EditReferenceHint);
}
