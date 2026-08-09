namespace ImageGen.Comfy.Edit.MiniMaxH3Ref2V;

/// <summary>MiniMax-H3 reference→video (ref2va, with native audio). The open image is the primary subject reference and
/// the edit page's ＋ ref picker adds more references of the KINDS the node accepts (see the card's <c>reference.types</c>):
/// image stills → <c>ref_images</c>, a driving video → <c>ref_videos</c> (decoded to frames), a driving audio clip →
/// <c>ref_audios</c>. Unlike i2v NONE is a first frame — they condition subject/motion/voice through the
/// <see cref="MiniMaxH3ReferenceToVideo"/> node. Its OWN ref2va diffusion checkpoint (reference-driven task weights,
/// distinct from the T2V/I2V fl2va checkpoint); the text encoder and dual VAEs are shared with the siblings.
/// Buckets into the Animate section (<c>media:video</c>).</summary>
public sealed class MiniMaxH3Ref2VWorkflow : EditWorkflow<MiniMaxH3Ref2VParams>
{
    public override string Name => "minimax-h3-ref2v";
    public override WorkflowMedia Media => WorkflowMedia.Video;
    /// <summary>H3 generates a native stereo audio track alongside the video (saved as an mp4 with sound).</summary>
    public override bool HasAudio => true;
    /// <summary>H3 VAE: valid clip length = 17n+5 (mirrors the node's length step=17, min=5).</summary>
    public override FrameRule? FrameRule => new(5, 17);
    public override IReadOnlyList<ParamSpec> Schema => [.. EditWorkflowBase.SharedSchema, .. H3.ExtraSchema, VideoSizeSchema.Megapixels];

    protected override ComfyWorkflowGraph Build(MiniMaxH3Ref2VParams p, ResolvedRequirements req, WorkflowInputs inputs)
        => H3.BuildRef2V(req, inputs, p.AudioVae, p.Length, p.Fps, ComfyGraph.Seed(p.Seed), p.Steps, p.Sampler, p.Scheduler, p.Lora, p.LoraStrength, p.Megapixels, refMax: p.ReferenceMax ?? 0);

    /// <summary>H3 pins the primary reference to the per-config <c>megapixels</c> budget, so the ETA keys on it.</summary>
    protected override (double Megapixels, int ResolutionSteps)? EtaBudget(MiniMaxH3Ref2VParams p) => (p.Megapixels, H3.BudgetSteps);
}