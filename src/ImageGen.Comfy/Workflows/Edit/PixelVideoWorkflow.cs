using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;

namespace ImageGen.Comfy;

/// <summary>
/// Pixel-art VIDEO: wraps any i2v base and pixel-quantizes its decoded frames (locked palette → temporally consistent).
/// The <c>guided</c> param (default false) additionally patches the model with <c>PixelManifoldProjection</c>, so the
/// pixels are projected into the latent every step (baked into the motion, no shimmer) at a real speed cost. One
/// decorator per base; one config per model variant, with <c>guided</c> as a boolean toggle.
/// <para>A decorator has TWO parameter contracts in one bag: the inner base's (which <see cref="IWorkflow.Build"/>
/// reads through its own typed deserialize) and its own pixel knobs. It forwards the whole merged bag to the inner
/// unchanged, then deserializes ONLY its own <see cref="PixelVideoParams"/> off the same bag (unmapped keys ignored) —
/// so both contracts are honoured without either seeing the other's keys.</para>
/// </summary>
public sealed class PixelVideoWorkflow : IWorkflow
{
    private readonly IWorkflow _inner;
    private readonly IReadOnlyList<ParamSpec> _schema;

    public PixelVideoWorkflow(IWorkflow inner)
    {
        _inner = inner;
        _schema = _inner.Schema.Concat(PixelVideoGraph.Params).ToArray();
    }

    public string Name => _inner.Name + "-pixel";
    public WorkflowKind Kind => _inner.Kind;
    public WorkflowMedia Media => WorkflowMedia.Video;
    public bool PromptDirectsMotion => _inner.PromptDirectsMotion;
    public bool SupportsEndFrame => _inner.SupportsEndFrame;
    public bool PreservesComposition => true;
    public bool RequiresModel => _inner.RequiresModel;
    /// <summary>The pixel decorator inherits its base i2v model's frame rule (LTX/Wan), so a snapped length applies.</summary>
    public FrameRule? FrameRule => _inner.FrameRule;
    public ModelResolution? ResolutionEnvelope => _inner.ResolutionEnvelope;
    public IReadOnlyList<ParamSpec> Schema => _schema;

    public ComfyWorkflowGraph Build(IReadOnlyDictionary<string, object?> p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph graph = _inner.Build(p, req, inputs);         // the inner reads its OWN typed params off the same bag
        PixelVideoParams pv = ParamsCodec.Deserialize<PixelVideoParams>(p);   // just this decorator's pixel knobs (unmapped inner keys ignored)
        if (pv.Guided)
            PixelVideoGraph.PatchModelProjection(graph, pv);   // steer the latent onto the manifold every step
        PixelVideoGraph.QuantizeFrames(graph, pv);              // authoritative crisp final render (always)
        return graph;
    }
}

/// <summary>The pixel-video decorator's own knobs. The quantizer's grid/palette/method/virtual-resolution are
/// <c>required</c> (every pixel-video config sets them and <see cref="PixelVideoGraph.QuantizeFrames"/> always reads
/// them); <c>guided</c> is a defaulted toggle; the projection ramp (<c>w_start</c>/<c>w_end</c>/<c>start_percent</c>/
/// <c>end_percent</c>/<c>project_every</c>) is nullable — read only when <c>guided</c>, via the <c>Required*</c>
/// accessors that refuse an absent value exactly as <c>DblReq</c>/<c>IntReq</c> would.</summary>
public sealed record PixelVideoParams
{
    [JsonPropertyName(WorkflowParamKeys.VirtualResolution)] public required int VirtualResolution { get; init; }
    [JsonPropertyName(WorkflowParamKeys.GridW)]             public required int GridW { get; init; }
    [JsonPropertyName(WorkflowParamKeys.GridH)]             public required int GridH { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Palette)]           public required string Palette { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Method)]            public required string Method { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Guided)]            public bool Guided { get; init; }
    [JsonPropertyName(WorkflowParamKeys.WStart)]            public double? WStart { get; init; }
    [JsonPropertyName(WorkflowParamKeys.WEnd)]              public double? WEnd { get; init; }
    [JsonPropertyName(WorkflowParamKeys.StartPercent)]      public double? StartPercent { get; init; }
    [JsonPropertyName(WorkflowParamKeys.EndPercent)]        public double? EndPercent { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ProjectEvery)]      public int? ProjectEvery { get; init; }

    public double RequiredWStart() => WStart ?? throw Missing(WorkflowParamKeys.WStart);
    public double RequiredWEnd() => WEnd ?? throw Missing(WorkflowParamKeys.WEnd);
    public double RequiredStartPercent() => StartPercent ?? throw Missing(WorkflowParamKeys.StartPercent);
    public double RequiredEndPercent() => EndPercent ?? throw Missing(WorkflowParamKeys.EndPercent);
    public int RequiredProjectEvery() => ProjectEvery ?? throw Missing(WorkflowParamKeys.ProjectEvery);

    private static RenderValidationException Missing(string key) => new(
        $"This configuration needs a value for '{key}' and none is set. It must supply one — there is no default.");
}
