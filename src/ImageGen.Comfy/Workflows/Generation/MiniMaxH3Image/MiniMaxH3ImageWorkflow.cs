namespace ImageGen.Comfy.Generation.MiniMaxH3Image;

/// <summary>
/// MiniMax-H3-Image text-to-image. <c>MiniMaxH3ImageConditioning</c> with no pictures encodes the prompt and emits
/// the image latent at the resolved canvas; the distilled 6-step, CFG-1 tail is shared with the editor
/// (<see cref="MiniMaxH3ImageGraph"/>).
/// </summary>
public sealed class MiniMaxH3ImageWorkflow : Txt2ImgWorkflow<Txt2ImgParams>
{
    public override string Name => "minimax-h3-image";

    protected override ComfyWorkflowGraph Build(Txt2ImgParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        (int w, int h) = RenderSize(p, req, inputs);
        ComfyWorkflowGraph g = new();
        (Output<Slot.Model> model, Output<Slot.Clip> clip, Output<Slot.Vae> vae) = MiniMaxH3ImageGraph.Load(g, req);
        (model, clip) = ApplyRuntimeModelInputs(g, model, clip, inputs, p.CkAttention, MiniMaxH3ImageNodes.CkAttention);

        g[MiniMaxH3ImageNodes.Encode] = new MiniMaxH3ImageConditioning
        {
            Clip = clip,
            Vae = vae,
            Prompt = inputs.Positive,
            Width = w,
            Height = h,
        };
        MiniMaxH3ImageGraph.SampleAndSave(g, new MiniMaxH3ImageSampling(p.Steps, p.RequiredCfg(), p.Sampler, p.Scheduler, p.Seed),
            model, vae,
            MiniMaxH3ImageConditioning.PositiveOut(MiniMaxH3ImageNodes.Encode),
            MiniMaxH3ImageConditioning.LatentOut(MiniMaxH3ImageNodes.Encode),
            OutputPrefixes.Generate);
        return g;
    }
}
