using ImageGen.Application.Rendering;
using ImageGen.Domain.CodeAnalysis;

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

    /// <summary><c>TextEncodeQwenImage21</c>'s <c>resolution</c> value that keeps each image at its own size.</summary>
    private const int KeepSourceSize = 0;

    public override string Name => "qwen-image-2-1-edit";
    public override bool NormalizesSourceResolution => true;
    public override bool SupportsEditQuality => true;
    [AllowNullable("the exact-source grid step; null on the base means the workflow has no exact-source path")]
    public override int? ExactSourceStep => ResolutionStep;
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

        // The encoder applies ONE resolution rule to every image it is given. Exact-source mode sets that rule to
        // "keep each image at its own size", which is right for the source and unbounded for an attached reference.
        if (inputs.ExactSourceResolution && inputs.ImageReferences.Count > 0)
        {
            throw new RenderValidationException("'exact_source_resolution' cannot be combined with reference images on Qwen-Image 2.1 editing.");
        }

        ComfyWorkflowGraph g = new();
        (Output<Slot.Model> model, Output<Slot.Clip> clip, Output<Slot.Vae> vae) = QwenImage21Graph.Load(g, req);
        g[QwenImage21Nodes.Source] = new LoadImage { Image = source };

        // Exact-source: hand the encoder the source padded onto its 32-px grid and tell it not to resize
        // (resolution 0). An on-grid image passes through the node untouched, so the latent it returns is the padded
        // source's own size and the decode is cropped back below.
        (int Width, int Height) sourceSize = (inputs.SourceWidth, inputs.SourceHeight);
        (int Width, int Height) paddedSize = (inputs.TargetWidth, inputs.TargetHeight);
        Output<Slot.Image> image1 = inputs.ExactSourceResolution
            ? ExactSourceCanvas.Pad(g, QwenImage21Nodes.ExactPad, LoadImage.ImageOut(QwenImage21Nodes.Source), sourceSize, paddedSize)
            : LoadImage.ImageOut(QwenImage21Nodes.Source);

        Dictionary<string, object> extra = new()
        {
            [QwenImage21Inputs.Vae] = vae,
            [QwenImage21Inputs.Image1] = image1,
        };
        for (int i = 0; i < inputs.ImageReferences.Count; i++)
        {
            string id = $"{21 + i}";
            g[id] = new LoadImage { Image = inputs.ImageReferences[i] };
            extra[$"images.image_{i + 2}"] = LoadImage.ImageOut(id);
        }

        double mp = inputs.EditMegapixels ?? DefaultMegapixels;
        int resolution = inputs.ExactSourceResolution
            ? KeepSourceSize
            : Math.Max(ResolutionStep,
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
        Output<Slot.Image> decoded = QwenImage21Graph.SampleAndDecode(g, sampler, model, vae,
            TextEncodeQwenImage21.PositiveOut(QwenImage21Nodes.Encode),
            TextEncodeQwenImage21.NegativeOut(QwenImage21Nodes.Encode),
            TextEncodeQwenImage21.LatentOut(QwenImage21Nodes.Encode));
        QwenImage21Graph.Save(g, inputs.ExactSourceResolution
            ? ExactSourceCanvas.Crop(g, QwenImage21Nodes.ExactCrop, decoded, sourceSize, paddedSize)
            : decoded);
        return g;
    }
}

internal static class QwenImage21Inputs
{
    public const string Vae = "vae";
    public const string Image1 = "images.image_1";
}
