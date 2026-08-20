namespace ImageGen.Comfy.Generation.MiniMaxH3T2V;

/// <summary>MiniMax-H3 text→video (with native audio). The fl2va model, no source frame — <c>MiniMaxH3ImageToVideo</c>
/// conditions on the prompt alone.</summary>
public sealed class MiniMaxH3T2VWorkflow : Txt2ImgWorkflow<MiniMaxH3Params>
{
    public override string Name => "minimax-h3-t2v";
    public override WorkflowMedia Media => WorkflowMedia.Video;
    /// <summary>H3 generates a native stereo audio track alongside the video (saved as an mp4 with sound).</summary>
    public override bool HasAudio => true;
    /// <summary>H3 VAE: valid clip length = 17n+5 (mirrors the node's length step=17, min=5).</summary>
    public override FrameRule? FrameRule => new(H3.MinFrames, H3.FrameStep);
    public override IReadOnlyList<ParamSpec> Schema =>
    [
        .. Txt2ImgWorkflowBase.SharedSchema.Where(p => p.Key != WorkflowParamKeys.Length),
        H3.LengthSchema,
        .. H3.ExtraSchema,
    ];

    protected override ComfyWorkflowGraph Build(MiniMaxH3Params p, ResolvedRequirements req, WorkflowInputs inputs)
        => H3.BuildT2V(req, inputs, p.AudioVae, p.Length, p.Fps, ComfyGraph.Seed(p.Seed), p.Steps, p.Sampler, p.Scheduler,
            p.Lora, p.LoraStrength, p.CkAttention, RenderSize(p, req, inputs));
}
