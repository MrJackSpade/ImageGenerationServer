using ImageGen.Application.Workflows;
using ImageGen.Domain;
using ImageGen.Domain.Repositories;
using System.Linq;
using System.Text.Json;

namespace ImageGen.Comfy;

/// <summary>
/// The Comfy-side <see cref="IWorkflowCatalog"/>: wraps the configuration-tree loader (<see cref="WorkflowCatalog"/>), the
/// graph registry (<see cref="WorkflowRegistry"/>), the ComfyUI capability probes (<see cref="ComfyClient"/>), and the
/// gen-timing history, and projects them into the Application's workflow business objects. The eligibility algorithm
/// (VRAM band + requirement-presence gating + shared-friendly-name de-duplication) and the row/guide shaping live here
/// — the core sees only the resulting descriptors and guides.
/// </summary>
public sealed partial class WorkflowCatalogService(
    WorkflowCatalog catalog, WorkflowRegistry registry, ComfyClient comfy, IGenTimingRepository timings,
    ICatalogOverrideRepository overrides, IWorkflowVariantRepository variants, ILogger<WorkflowCatalogService> log) : IWorkflowCatalog
{
    private readonly WorkflowCatalog _catalog = catalog;
    private readonly WorkflowRegistry _registry = registry;
    private readonly ComfyClient _comfy = comfy;
    private readonly IGenTimingRepository _timings = timings;
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
        return new WorkflowInfo(friendly, ToTagging(card.Tagging), preserves, wf?.Media == WorkflowMedia.Video, BuildReference(card));
    }

    /// <summary>The workflow's reference capability, or null when it accepts no references — the single projection of a
    /// card's normalized reference types shared by the descriptor (UI) and <see cref="WorkflowInfo"/> (enqueue validation).</summary>
    private static WorkflowReference? BuildReference(ModelCard card) =>
        card.EditReferenceTypes.Count > 0 ? new WorkflowReference(card.EditReferenceTypes, card.EditReferenceHint) : null;

    /// <summary>Load this machine's variants from the store and fold them into the in-memory catalogue. Pushed
    /// alongside the bindings/overrides for the same reason: the sync submit path resolves a variant id without a
    /// query, so the push has to have happened first.</summary>
    private async Task PushVariantsAsync(string machine, CancellationToken ct)
    {
        IReadOnlyList<WorkflowVariant> rows = await _variants.VariantsAsync(machine, ct);
        List<VariantSpec> specs = [];
        foreach (WorkflowVariant v in rows)
        {
            Dictionary<string, JsonElement> snap = ParseSnapshot(v.VariantId, v.ParamsJson);
            specs.Add(new VariantSpec(v.VariantId, v.BaseConfigId, v.FriendlyName, snap));
        }

        _catalog.SetVariants(specs);
    }

    /// <summary>Parse a variant's stored parameter snapshot; a corrupt blob yields an empty snapshot (the variant then
    /// renders the base's own values) rather than taking the whole catalogue load down.</summary>
    private Dictionary<string, JsonElement> ParseSnapshot(string variantId, string paramsJson)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(paramsJson) ?? [];
        }
        catch (JsonException ex)
        {
            _log.LogError(ex, "Workflow variant '{Id}' has an unreadable parameter snapshot; using the base's values.", variantId);
            return [];
        }
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
        await PushVariantsAsync(Environment.MachineName, ct);
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
        await PushVariantsAsync(Environment.MachineName, ct);
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
        return rows.FirstOrDefault(r => string.Equals(r.VariantId, configId, StringComparison.OrdinalIgnoreCase))?.BaseConfigId
            ?? configId;
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
        // Throws (HttpRequestException/etc.) when ComfyUI is unreachable — the caller maps that to a 502.
        IReadOnlySet<string> present = await _comfy.GetPresentFilesAsync(ct);

        // Bindings say which file on THIS machine fills each slot. Pushed into the catalog as well as read here,
        // so the sync Resolve() used on every submit sees the same snapshot without a query on the render path.
        IReadOnlyDictionary<string, ModelBinding> bindings = await _overrides.BindingsAsync(Environment.MachineName, ct);
        _catalog.SetBindings(bindings.ToDictionary(kv => kv.Key, kv => kv.Value.FileName, StringComparer.OrdinalIgnoreCase));
        // And this machine's per-configuration settings. The settings page only stores rows; the merge reads them
        // from the in-memory catalog, so without pushing them here every override would silently do nothing. Pushed
        // alongside the bindings, for the same reason.
        _catalog.SetParamOverrides(await _overrides.OverridesAsync(Environment.MachineName, ct));
        // And this machine's DB-backed variants, folded into the catalogue so they list and resolve like any config.
        await PushVariantsAsync(Environment.MachineName, ct);

        // Which custom nodes this ComfyUI has, asked once: the file lists cannot answer it, because a pack that
        // loads no weights contributes no filenames to look for.
        List<string> declaredNodes = [.. _catalog.AllRequirements()
            .Where(r => !string.IsNullOrWhiteSpace(r.Node))
            .Select(r => r.Node)
            .OfType<string>()
            .Distinct()];
        IReadOnlySet<string> presentNodes = declaredNodes.Count == 0
            ? new HashSet<string>()
            : await _comfy.GetPresentNodesAsync(declaredNodes, ct);

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

                return bindings.TryGetValue(id, out ModelBinding? bound) && present.Contains(bound.FileName);
            });
            if (!ok)
            {
                continue;
            }

            eligible.Add((cfg, wf));
        }

        // Per-model average runtime (machine-specific, last 10 renders). Purely a decoration on rows that are already
        // decided — the eligibility above is what this method is FOR — so a timings hiccup must not take the model
        // picker down with it. It is reported, though: swallowing it in a bare catch would present a
        // permanently-broken timings table as "no model has ever been run here", with nothing anywhere to disagree.
        IReadOnlyDictionary<string, double> avgs;
        try
        {
            avgs = await _timings.RecentAveragesMsAsync(Environment.MachineName, 10, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Recent-average lookup failed; listing workflows without their ETAs.");
            avgs = new Dictionary<string, double>();
        }

        // Shared display name (within a kind + effect + edit section) → keep the first. The section is part of the
        // identity for the same reason the effect is: "Anima" under the Redraw header and a plain "Anima" editor are
        // different offerings, not duplicates.
        return [.. eligible
            .GroupBy(e => $"{e.wf.Kind} {e.cfg.EffectType} {e.cfg.EditGroup} {(e.cfg.FriendlyName ?? e.cfg.Id).ToLowerInvariant()}")
            .Select(g => g.First())
            .Select(e => ToDescriptor(e.cfg, e.wf,
                avgs.TryGetValue(e.cfg.Id, out double ms) ? (int?)Math.Round(ms / 1000.0) : null))];
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

        // Every parameter the CONFIGURATION sets, not just the ones exposed per generation. The exposed ones are
        // what a caller may vary on a single render; these are what this machine renders with by default, which is
        // a different question and the one this page answers.
        List<ConfigSetting> settings = [.. cfg.Params
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv =>
            {
                ParamSpec? spec = wf.Schema.FirstOrDefault(s => string.Equals(s.Key, kv.Key, StringComparison.OrdinalIgnoreCase));
                return new ConfigSetting(
                    kv.Key,
                    spec?.Label ?? kv.Key,
                    spec?.Help,
                    // `aspect` is its own shape — a map of aspect name to [width, height] — and the page draws it
                    // as three width/height pairs rather than asking anyone to type JSON.
                    string.Equals(kv.Key, WorkflowParamKeys.Aspect, StringComparison.OrdinalIgnoreCase)
                        ? ControlTokens.Aspect
                        : (spec?.Type ?? ParamType.String).ToString().ToLowerInvariant(),
                    kv.Value.Min ?? spec?.Min,
                    kv.Value.Max ?? spec?.Max,
                    kv.Value.Step ?? spec?.Step,
                    spec?.Choices,
                    kv.Value.Value,
                    overrides.TryGetValue(kv.Key, out JsonElement o) ? o : null);
            })];

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
                "When on, the generation page offers a Custom aspect with width/height boxes for this workflow, validated against its resolution envelope.",
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
        }

        // The declared envelope travels with the settings, so the size boxes are bounded by what the model says
        // it supports instead of by a guess.
        ModelResolution? r = cfg.Resolution;
        return new WorkflowSettings(
            cfg.Id, cfg.FriendlyName ?? cfg.Id, settings,
            r is null ? null : new ResolutionEnvelope(r.MinW, r.MinH, r.MaxW, r.MaxH, r.Step));
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
            return WorkflowKind.VideoEdit;   // consumes a clip (the pixel-quantize V2V pass)
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
        WorkflowKind.Generate => KindTokens.Generate,
        WorkflowKind.Edit => KindTokens.Edit,
        WorkflowKind.Inpaint => KindTokens.Inpaint,
        WorkflowKind.Outpaint => KindTokens.Outpaint,
        WorkflowKind.Redraw => KindTokens.Redraw,
        WorkflowKind.Upscale => KindTokens.Upscale,
        WorkflowKind.Effect => KindTokens.Effect,
        WorkflowKind.Animate => KindTokens.Animate,
        WorkflowKind.VideoEdit => KindTokens.VideoEdit,
        _ => KindTokens.Generate,
    };

    /// <summary>The wire tokens for a resolved <see cref="WorkflowKind"/> — one fixed vocabulary shared with the client.</summary>
    private static class KindTokens
    {
        public const string Generate = "generate";
        public const string Edit = "edit";
        public const string Inpaint = "inpaint";
        public const string Outpaint = "outpaint";
        public const string Redraw = "redraw";
        public const string Upscale = "upscale";
        public const string Effect = "effect";
        public const string Animate = "animate";
        public const string VideoEdit = "videoedit";
    }

    /// <summary>The config edit-group magic values the catalog promotes to their own kind.</summary>
    private static class EditGroups
    {
        public const string Redraw = "Redraw";
        public const string Upscale = "Upscale";
    }

    private WorkflowDescriptor ToDescriptor(
        WorkflowConfiguration cfg, IWorkflow wf, int? avgSeconds)
    {
        ModelCard c = cfg.Card;
        // This machine's overrides win here too. Reporting the SHIPPED value while rendering the overridden one
        // would put the composer and the graph into disagreement — the control would read 40 steps and produce 12.
        IReadOnlyDictionary<string, JsonElement> machine = _catalog.ParamOverridesFor(cfg.Id);
        List<WorkflowExposedParam> exposed = [.. cfg.Params
            .Where(kv => kv.Value.Exposed)
            .Select(kv =>
            {
                ParamSpec? spec = wf.Schema.FirstOrDefault(s => string.Equals(s.Key, kv.Key, StringComparison.OrdinalIgnoreCase));
                return new WorkflowExposedParam(
                    kv.Key,
                    (spec?.Type ?? ParamType.String).ToString().ToLowerInvariant(),
                    machine.TryGetValue(kv.Key, out JsonElement o) ? o : kv.Value.Value,
                    kv.Value.Min ?? spec?.Min,
                    kv.Value.Max ?? spec?.Max,
                    kv.Value.Step ?? spec?.Step,
                    spec?.Label ?? kv.Key,
                    spec?.Help,
                    spec?.Choices);
            })];
        bool canEdit = wf.Kind != WorkflowKind.Generate;
        return new WorkflowDescriptor(
            Id: cfg.Id,
            Workflow: cfg.WorkflowName,
            Kind: KindToken(ResolveKind(cfg, wf)),
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
            HasAudio: wf.HasAudio,
            FriendlyName: cfg.FriendlyName ?? c.FriendlyName,
            Default: cfg.Default,
            AvgSeconds: avgSeconds,
            ExposedParams: exposed,
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
                NegativeSupported: c.NegativeSupported,
                EditUseCases: c.EditUseCases is { Length: > 0 } ? c.EditUseCases : null,
                Tagging: ToTagging(c.Tagging)),
            // The composer's LoRA picker opens to this folder for this workflow (per-machine override, Part H);
            // null falls back client-side to a folder matching the workflow, else the root.
            LoraFolder: OverrideString(machine, SettingKeys.TargetLoraFolder),
            // Opt-in per-machine toggle: when set, the composer offers a "Custom" aspect with width/height boxes for
            // this workflow. Read from the same per-machine override store as the settings toggle that sets it.
            CustomSizeEnabled: OverrideBool(machine, SettingKeys.CustomSize),
            // A DB-backed duplicate, not a shipped file — the library marks it and offers Delete only on these.
            IsVariant: _catalog.IsVariant(cfg.Id));
    }

    /// <summary>Settings-page override keys (persisted per machine).</summary>
    private static class SettingKeys
    {
        /// <summary>The per-machine setting key for a workflow's default LoRA folder (a plain string override, not a graph
        /// parameter — no workflow reads it; the composer's picker does).</summary>
        public const string TargetLoraFolder = "targetLoraFolder";

        /// <summary>The settings-page override key for a configuration's render-size aspect map.</summary>
        public const string AspectOverride = "param.aspect";

        /// <summary>The per-machine setting key for whether the composer offers a Custom aspect (width/height boxes) for
        /// this workflow (a plain bool override, not a graph parameter).</summary>
        public const string CustomSize = "customSize";
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
    {
        if (!machine.TryGetValue(key, out JsonElement v))
        {
            return false;
        }

        return v.ValueKind == System.Text.Json.JsonValueKind.True
            || (v.ValueKind == System.Text.Json.JsonValueKind.String && bool.TryParse(v.GetString(), out bool b) && b);
    }

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