using ImageGen.Application.Workflows;
using ImageGen.Domain.Repositories;
using Microsoft.Extensions.Logging;

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
        var cfg = _catalog.FindConfig(configId);
        if (cfg is null) return null;
        var card = cfg.Card;
        var friendly = card.FriendlyName ?? cfg.FriendlyName ?? cfg.Id;
        var wf = _registry.Find(cfg.WorkflowName);
        var preserves = wf?.PreservesComposition ?? false;
        return new WorkflowInfo(friendly, ToTagging(card.Tagging), preserves, wf?.Media == WorkflowMedia.Video);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<WorkflowDescriptor>> ListEligibleAsync(CancellationToken ct)
    {
        // Throws (HttpRequestException/etc.) when ComfyUI is unreachable — the caller maps that to a 502.
        var present = await _comfy.GetPresentFilesAsync(ct);

        // Bindings say which file on THIS machine fills each slot. Pushed into the catalog as well as read here,
        // so the sync Resolve() used on every submit sees the same snapshot without a query on the render path.
        var bindings = await _overrides.BindingsAsync(Environment.MachineName, ct);
        _catalog.SetBindings(bindings.ToDictionary(kv => kv.Key, kv => kv.Value.FileName, StringComparer.OrdinalIgnoreCase));
        // And this machine's per-configuration settings. These were written by the settings page and read by
        // nothing: the endpoint stored rows, the merge never looked at them, so every override silently did
        // nothing. Pushed into the catalog alongside the bindings, for the same reason.
        _catalog.SetParamOverrides(await _overrides.OverridesAsync(Environment.MachineName, ct));

        // Which custom nodes this ComfyUI has, asked once: the file lists cannot answer it, because a pack that
        // loads no weights contributes no filenames to look for.
        var declaredNodes = _catalog.AllRequirements()
            .Where(r => !string.IsNullOrWhiteSpace(r.Node))
            .Select(r => r.Node!)
            .Distinct()
            .ToList();
        var presentNodes = declaredNodes.Count == 0
            ? (IReadOnlySet<string>)new HashSet<string>()
            : await _comfy.GetPresentNodesAsync(declaredNodes, ct);

        var eligible = new List<(WorkflowConfiguration cfg, IWorkflow wf)>();
        foreach (var cfg in _catalog.AllConfigs())
        {
            var wf = _registry.Find(cfg.WorkflowName);
            if (wf is null) continue;

            // A model workflow with no checkpoint is misconfigured; a model-free one (quantizer) is fine.
            if (wf.RequiresModel && string.IsNullOrEmpty(cfg.Requirements.Checkpoint)) continue;
            // A slot is satisfied when this machine has BOUND a file to it and that file is still present. The
            // filename used to be shipped in the catalogue, so anyone whose copy was named differently failed this
            // check and lost the workflow with no explanation.
            // Both places a configuration names a model. `requirements` was the only one consulted, so a
            // configuration whose params model ref was unbound still appeared in the picker and then failed at
            // submit with an empty filename reaching a loader — wan22-i2v-a14b names its second MoE expert
            // (unet_low) nowhere but params. One rule for both: a slot the configuration ASKS FOR must be bound to
            // a file this renderer still has. A param it does not set is absent by choice and asks for nothing.
            bool ok = cfg.Requirements.All().Concat(_catalog.ModelRefSlots(wf, cfg)).All(id =>
            {
                var r = _catalog.FindRequirement(id);
                if (r is null) return false;
                // A node requirement is met by ComfyUI having the node registered. It has no file, so it can never
                // have a binding — demanding one excluded EVERY configuration that declares a node pack from the
                // picker, however well installed the pack was, while /forge/catalog/status reported it ready.
                if (!string.IsNullOrWhiteSpace(r.Node)) return presentNodes.Contains(r.Node!);
                return bindings.TryGetValue(id, out var bound) && present.Contains(bound.FileName);
            });
            if (!ok) continue;

            eligible.Add((cfg, wf));
        }

        // Per-model average runtime (machine-specific, last 10 renders). Purely a decoration on rows that are already
        // decided — the eligibility above is what this method is FOR — so a timings hiccup must not take the model
        // picker down with it. It is reported, though: this was a bare catch, so a permanently-broken timings table
        // presented as "no model has ever been run here" and nothing anywhere disagreed.
        IReadOnlyDictionary<string, double> avgs;
        try { avgs = await _timings.RecentAveragesMsAsync(Environment.MachineName, 10, ct); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Recent-average lookup failed; listing workflows without their ETAs.");
            avgs = new Dictionary<string, double>();
        }

        // Shared display name (within a kind + effect + edit section) → keep the first. It used to keep the one with
        // the highest VRAM floor, which is how the "-hq" sibling won on a big card; both the siblings and the floor
        // are gone. The section is part of the identity for the same reason the effect is: "Anima" under the Redraw
        // header and a plain "Anima" editor are different offerings, not duplicates.
        return eligible
            .GroupBy(e => $"{e.wf.Kind} {e.cfg.EffectType} {e.cfg.EditGroup} {(e.cfg.FriendlyName ?? e.cfg.Id).ToLowerInvariant()}")
            .Select(g => g.First())
            .Select(e => ToDescriptor(e.cfg, e.wf,
                avgs.TryGetValue(e.cfg.Id, out var ms) ? (int?)Math.Round(ms / 1000.0) : null))
            .ToList();
    }

    /// <inheritdoc/>
    public WorkflowSettings? GetSettings(string? configId)
    {
        var cfg = _catalog.FindConfig(configId);
        if (cfg is null) return null;
        var wf = _registry.Find(cfg.WorkflowName);
        if (wf is null) return null;

        var overrides = _catalog.ParamOverridesFor(cfg.Id);

        // Every parameter the CONFIGURATION sets, not just the ones exposed per generation. The exposed ones are
        // what a caller may vary on a single render; these are what this machine renders with by default, which is
        // a different question and the one this page answers.
        var settings = cfg.Params
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv =>
            {
                var spec = wf.Schema.FirstOrDefault(s => string.Equals(s.Key, kv.Key, StringComparison.OrdinalIgnoreCase));
                return new ConfigSetting(
                    kv.Key,
                    spec?.Label ?? kv.Key,
                    spec?.Help,
                    // `aspect` is its own shape — a map of aspect name to [width, height] — and the page draws it
                    // as three width/height pairs rather than asking anyone to type JSON.
                    string.Equals(kv.Key, "aspect", StringComparison.OrdinalIgnoreCase)
                        ? "aspect"
                        : (spec?.Type ?? ParamType.String).ToString().ToLowerInvariant(),
                    kv.Value.Min ?? spec?.Min,
                    kv.Value.Max ?? spec?.Max,
                    kv.Value.Step ?? spec?.Step,
                    spec?.Choices,
                    kv.Value.Value,
                    overrides.TryGetValue(kv.Key, out var o) ? o : null);
            })
            .ToList();

        // A per-machine "default LoRA folder" for this workflow's composer picker. It is NOT a config/graph param, so
        // it's surfaced here as a synthetic string setting the settings page renders and saves through the same
        // override path (param.targetLoraFolder) as every other setting — no new settings UI needed.
        if (wf.Kind == WorkflowKind.Generate && wf.Media == WorkflowMedia.Image)
            settings.Add(new ConfigSetting(
                TargetLoraFolderKey, "Default LoRA folder",
                "The composer's LoRA picker opens to this subfolder for this workflow. Blank = a folder matching the workflow, else all LoRAs.",
                "string", null, null, null, null,
                Shipped: null,
                Override: overrides.TryGetValue(TargetLoraFolderKey, out var lf) ? (object?)lf : null));

        // The declared envelope travels with the settings, so the size boxes are bounded by what the model says
        // it supports instead of by a guess.
        var r = cfg.Resolution;
        return new WorkflowSettings(
            cfg.Id, cfg.FriendlyName ?? cfg.Id, settings,
            r is null ? null : new ResolutionEnvelope(r.MinW, r.MinH, r.MaxW, r.MaxH, r.Step));
    }

    /// <inheritdoc/>
    public PromptingGuide? GetGuide(string? configId)
    {
        var card = _catalog.ResolveCard(configId);
        return card is null ? null : ToGuide(card);
    }

    /// <inheritdoc/>
    public IReadOnlyList<PromptingGuide> AllGuides() => _catalog.AllCards().Select(ToGuide).ToList();

    private static WorkflowTagging? ToTagging(TaggingInfo? t) =>
        t is null ? null : new WorkflowTagging(t.Tags, t.Artists, t.KeepArtistMarker, t.UnderscoresToSpaces);

    private WorkflowDescriptor ToDescriptor(
        WorkflowConfiguration cfg, IWorkflow wf, int? avgSeconds)
    {
        var c = cfg.Card;
        // This machine's overrides win here too. Reporting the SHIPPED value while rendering the overridden one
        // would put the composer and the graph into disagreement — the control would read 40 steps and produce 12.
        var machine = _catalog.ParamOverridesFor(cfg.Id);
        var exposed = cfg.Params
            .Where(kv => kv.Value.Exposed)
            .Select(kv =>
            {
                var spec = wf.Schema.FirstOrDefault(s => string.Equals(s.Key, kv.Key, StringComparison.OrdinalIgnoreCase));
                return new WorkflowExposedParam(
                    kv.Key,
                    (spec?.Type ?? ParamType.String).ToString().ToLowerInvariant(),
                    machine.TryGetValue(kv.Key, out var o) ? o : kv.Value.Value,
                    kv.Value.Min ?? spec?.Min,
                    kv.Value.Max ?? spec?.Max,
                    kv.Value.Step ?? spec?.Step,
                    spec?.Label ?? kv.Key,
                    spec?.Help,
                    spec?.Choices);
            })
            .ToList();
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
            LoraFolder: OverrideString(machine, TargetLoraFolderKey));
    }

    /// <summary>The per-machine setting key for a workflow's default LoRA folder (a plain string override, not a graph
    /// parameter — no workflow reads it; the composer's picker does).</summary>
    private const string TargetLoraFolderKey = "targetLoraFolder";

    /// <summary>Read a string-valued per-machine param override, or null when unset/blank.</summary>
    private static string? OverrideString(IReadOnlyDictionary<string, System.Text.Json.JsonElement> machine, string key)
    {
        if (!machine.TryGetValue(key, out var v)) return null;
        var s = v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : v.ToString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
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
