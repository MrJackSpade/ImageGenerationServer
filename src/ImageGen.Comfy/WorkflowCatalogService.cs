using ImageGen.Application.Workflows;
using ImageGen.Domain.Repositories;
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
    ICatalogOverrideRepository overrides, ILogger<WorkflowCatalogService> log) : IWorkflowCatalog
{
    private readonly WorkflowCatalog _catalog = catalog;
    private readonly WorkflowRegistry _registry = registry;
    private readonly ComfyClient _comfy = comfy;
    private readonly IGenTimingRepository _timings = timings;
    private readonly ICatalogOverrideRepository _overrides = overrides;
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
        return new WorkflowInfo(friendly, ToTagging(card.Tagging), preserves, wf?.Media == WorkflowMedia.Video);
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
                        ? "aspect"
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
        if (wf.Kind == WorkflowKind.Generate && wf.Media == WorkflowMedia.Image)
        {
            settings.Add(new ConfigSetting(
                SettingKeys.CustomSize, "Custom size on the generation page",
                "When on, the generation page offers a Custom aspect with width/height boxes for this workflow, validated against its resolution envelope.",
                ControlTokens.Bool, null, null, null, null,
                Shipped: null,
                Override: overrides.TryGetValue(SettingKeys.CustomSize, out JsonElement cs) ? (object?)cs : null));
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
        bool canEdit = wf.Kind == WorkflowKind.Edit;
        return new WorkflowDescriptor(
            Id: cfg.Id,
            Workflow: cfg.WorkflowName,
            Kind: canEdit ? "edit" : "generate",
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
            Reference: canEdit && c.EditReferenceMax > 0 ? new WorkflowReference(c.EditReferenceMax, c.EditReferenceHint) : null,
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
            CustomSizeEnabled: OverrideBool(machine, SettingKeys.CustomSize));
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
        CanEdit: c.EditUseCases.Length > 0 || c.EditReferenceMax > 0,
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
        MaxReferenceImages: c.EditReferenceMax,
        ReferenceTechnique: c.EditReferenceHint);
}