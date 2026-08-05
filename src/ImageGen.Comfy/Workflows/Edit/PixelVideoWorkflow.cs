using System.Reflection;

namespace ImageGen.Comfy;

/// <summary>
/// Pixel-art VIDEO: wraps any i2v base and pixel-quantizes its decoded frames (locked palette → temporally consistent).
/// The <c>guided</c> param (default false) additionally patches the model with <c>PixelManifoldProjection</c>, so the
/// pixels are projected into the latent every step (baked into the motion, no shimmer) at a real speed cost. One
/// decorator per base; one config per model variant, with <c>guided</c> as a boolean toggle.
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

    public Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = _inner.Build(p, req, inputs);
        if (p.Bool(WorkflowParamKeys.Guided, false))
            PixelVideoGraph.PatchModelProjection(wf, p);   // steer the latent onto the manifold every step
        PixelVideoGraph.QuantizeFrames(wf, p);              // authoritative crisp final render (always)
        return wf;
    }
}
