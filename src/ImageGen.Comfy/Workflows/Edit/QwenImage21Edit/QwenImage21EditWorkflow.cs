using ImageGen.Application.Rendering;

namespace ImageGen.Comfy.Edit.QwenImage21Edit;

/// <summary>
/// Qwen-Image 2.1 unified image editor. The source and up to nine additional references enter the encoder's autogrow
/// group; its latent output follows image 1, which is the edit target. The prompt refers to them as
/// <c>&lt;image1&gt;</c> through <c>&lt;image10&gt;</c>.
/// </summary>
public sealed class QwenImage21EditWorkflow : EditWorkflow<MageFlowEditParams>
{
    private const double DefaultMegapixels = 1.0;
    private const int ResolutionStep = 32;

    public override string Name => "qwen-image-2-1-edit";
    public override bool NormalizesSourceResolution => true;
    public override bool SupportsEditQuality => true;
    public override ModelResolution? ResolutionEnvelope => new() { MinW = 256, MinH = 256, MaxW = 2048, MaxH = 2048, Step = ResolutionStep };

    protected override (double Megapixels, int ResolutionSteps)? EtaBudget(MageFlowEditParams p) =>
        (DefaultMegapixels, ResolutionStep);

    protected override (int Width, int Height) EtaRenderSize(MageFlowEditParams p, ResolvedRequirements req,
        int sourceWidth, int sourceHeight, double? editMegapixels) =>
        BudgetScale.Snap(sourceWidth, sourceHeight, editMegapixels ?? DefaultMegapixels, ResolutionStep);

    protected override ComfyWorkflowGraph Build(MageFlowEditParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        string source = inputs.SourceImageName
            ?? throw new RenderValidationException("Qwen-Image 2.1 editing needs a source image.");
        int referenceMax = p.ReferenceMax ?? 0;
        if (inputs.ImageReferences.Count > referenceMax)
        {
            throw new RenderValidationException($"This configuration accepts at most {referenceMax} additional reference image(s); got {inputs.ImageReferences.Count}.");
        }

        ComfyWorkflowGraph g = new();
        (Output<Slot.Model> model, Output<Slot.Clip> clip, Output<Slot.Vae> vae) = QwenImage21Graph.Load(g, req);
        g[QwenImage21Nodes.Source] = new LoadImage { Image = source };

        Dictionary<string, object> extra = new()
        {
            [QwenImage21Inputs.Vae] = vae,
            [QwenImage21Inputs.Image1] = LoadImage.ImageOut(QwenImage21Nodes.Source),
        };
        for (int i = 0; i < inputs.ImageReferences.Count; i++)
        {
            string id = $"{21 + i}";
            g[id] = new LoadImage { Image = inputs.ImageReferences[i] };
            extra[$"images.image_{i + 2}"] = LoadImage.ImageOut(id);
        }

        double mp = inputs.EditMegapixels ?? DefaultMegapixels;
        int resolution = Math.Max(ResolutionStep,
            (int)Math.Round(Math.Sqrt(mp * 1024d * 1024d) / ResolutionStep) * ResolutionStep);
        g[QwenImage21Nodes.Encode] = new TextEncodeQwenImage21
        {
            Clip = clip,
            Prompt = inputs.Positive,
            NegativePrompt = inputs.Negative ?? "",
            Resolution = resolution,
            Extra = extra,
        };

        Txt2ImgParams sampler = new()
        {
            Steps = p.Steps,
            Cfg = p.Cfg,
            Sampler = p.Sampler,
            Scheduler = p.Scheduler,
            Seed = p.Seed,
        };
        QwenImage21Graph.SampleAndSave(g, sampler, model, vae,
            TextEncodeQwenImage21.PositiveOut(QwenImage21Nodes.Encode),
            TextEncodeQwenImage21.NegativeOut(QwenImage21Nodes.Encode),
            TextEncodeQwenImage21.LatentOut(QwenImage21Nodes.Encode));
        return g;
    }
}

internal static class QwenImage21Inputs
{
    public const string Vae = "vae";
    public const string Image1 = "images.image_1";
}
