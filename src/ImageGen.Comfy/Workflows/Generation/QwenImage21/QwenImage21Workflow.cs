namespace ImageGen.Comfy.Generation.QwenImage21;

/// <summary>
/// Qwen-Image 2.1 text-to-image. Unlike the earlier 20B Qwen model it uses the unified
/// <c>TextEncodeQwenImage21</c> node, a standard empty latent, and the model's built-in flow schedule.
/// </summary>
public sealed class QwenImage21Workflow : Txt2ImgWorkflow<Txt2ImgParams>
{
    public override string Name => "qwen-image-2-1";

    protected override ComfyWorkflowGraph Build(Txt2ImgParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        (int w, int h) = RenderSize(p, req, inputs);
        ComfyWorkflowGraph g = new();
        (Output<Slot.Model> model, Output<Slot.Clip> clip, Output<Slot.Vae> vae) = QwenImage21Graph.Load(g, req);

        g[QwenImage21Nodes.Encode] = new TextEncodeQwenImage21
        {
            Clip = clip,
            Prompt = inputs.Positive,
            NegativePrompt = inputs.Negative ?? "",
            Resolution = 1024,
        };
        g[QwenImage21Nodes.Latent] = new EmptyLatent(ComfyNodeTypes.EmptyLatentImage)
        {
            Width = w,
            Height = h,
            BatchSize = 1,
        };
        QwenImage21Graph.SampleAndSave(g, p, model, vae,
            TextEncodeQwenImage21.PositiveOut(QwenImage21Nodes.Encode),
            TextEncodeQwenImage21.NegativeOut(QwenImage21Nodes.Encode),
            EmptyLatent.Out(QwenImage21Nodes.Latent));
        return g;
    }
}
