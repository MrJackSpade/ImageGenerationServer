using ImageGen.Comfy;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;
using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Comfy.Edit.PixelVideo;

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
