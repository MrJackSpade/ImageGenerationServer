namespace ImageGen.Comfy.Edit.MiniMaxH3I2V;

/// <summary>MiniMax-H3 image→video (with native audio). The source image is the first frame; an optional last frame
/// (<see cref="SupportsEndFrame"/>) pins the ending. Same fl2va model as the T2V sibling.</summary>
public sealed class MiniMaxH3I2VWorkflow : EditWorkflow<MiniMaxH3I2VParams>
{
    public override string Name => "minimax-h3-i2v";
    public override WorkflowMedia Media => WorkflowMedia.Video;
    public override bool SupportsEndFrame => true;
    /// <summary>H3 generates a native stereo audio track alongside the video (saved as an mp4 with sound).</summary>
    public override bool HasAudio => true;
    /// <summary>H3 VAE: valid clip length = 17n+5 (mirrors the node's length step=17, min=5).</summary>
    public override FrameRule? FrameRule => new(5, 17);
    public override IReadOnlyList<ParamSpec> Schema => [.. EditWorkflowBase.SharedSchema, .. H3.ExtraSchema];

    protected override ComfyWorkflowGraph Build(MiniMaxH3I2VParams p, ResolvedRequirements req, WorkflowInputs inputs)
        => H3.Build(req, inputs, H3Mode.I2V, p.AudioVae, p.Length, p.Fps, ComfyGraph.Seed(p.Seed), p.Steps, p.Sampler, p.Scheduler, t2vDims: null);

    /// <summary>H3 pins the source to its ~1&#160;MP budget, so the ETA keys on the post-budget size, not the upload.</summary>
    protected override (double Megapixels, int ResolutionSteps)? EtaBudget(MiniMaxH3I2VParams p) => (H3.BudgetMp, H3.BudgetSteps);
}