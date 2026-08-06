using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;
using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Comfy.Edit.MiniMaxH3Ref2V;

/// <summary>MiniMax-H3 reference→video (ref2va, with native audio). The open image is the primary subject reference and
/// the edit page's ＋ ref picker (<c>reference.max &gt; 0</c>) adds more; unlike i2v NONE of them is a first frame — they
/// condition the subject/identity through the <see cref="MiniMaxH3ReferenceToVideo"/> node. Same fl2va model, text
/// encoder and dual VAEs as the T2V/I2V siblings — no new weights. Buckets into the Animate section (<c>media:video</c>).</summary>
public sealed class MiniMaxH3Ref2VWorkflow : EditWorkflow<MiniMaxH3Ref2VParams>
{
    public override string Name => "minimax-h3-ref2v";
    public override WorkflowMedia Media => WorkflowMedia.Video;
    /// <summary>H3 generates a native stereo audio track alongside the video (saved as an mp4 with sound).</summary>
    public override bool HasAudio => true;
    /// <summary>H3 VAE: valid clip length = 17n+5 (mirrors the node's length step=17, min=5).</summary>
    public override FrameRule? FrameRule => new(5, 17);
    public override IReadOnlyList<ParamSpec> Schema => EditWorkflowBase.SharedSchema.Concat(H3.ExtraSchema).ToArray();

    protected override ComfyWorkflowGraph Build(MiniMaxH3Ref2VParams p, ResolvedRequirements req, WorkflowInputs inputs)
        => H3.Build(req, inputs, H3Mode.Ref2V, p.AudioVae, p.Length, p.Fps, ComfyGraph.Seed(p.Seed), p.Steps, p.Sampler, p.Scheduler, t2vDims: null, refMax: p.ReferenceMax ?? 0);
}
