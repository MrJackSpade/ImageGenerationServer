using ImageGen.Application.Rendering;

namespace ImageGen.Comfy.Edit.MiniMaxH3ImageEdit;

/// <summary>
/// MiniMax-H3-Image editing: single-image instruction edits and multi-reference composition from the SAME checkpoint as
/// <c>minimax-h3-image</c>. The source is <c>&lt;Picture 1&gt;</c> (the edit target, autogrow <c>images.image_1</c>);
/// attached references follow as <c>&lt;Picture 2&gt;</c>… — the model card allows 16 pictures in total.
///
/// <para>Like the official template, the source is scaled to the megapixel budget on the 32-px canvas grid and its
/// scaled size becomes the canvas, so the output keeps the source's aspect. The node itself resizes every picture for
/// the vision tower and the reference latents; references are passed at their native size.</para>
/// </summary>
public sealed class MiniMaxH3ImageEditWorkflow : EditWorkflow<MiniMaxH3ImageEditParams>
{
    /// <summary>Bounds on the source budget. The model card's working range is ~4–20 MP (16 MP best); below 4 MP the
    /// node still renders but detail drops, so 1 MP is the floor a config may opt into and 20 MP the ceiling.</summary>
    public const double MinMegapixels = 1.0;
    public const double MaxMegapixels = 20.0;

    /// <summary>16 pictures in total per the model card: the edit target plus up to 15 references.</summary>
    public const int MaxExtraReferences = 15;

    public override string Name => "minimax-h3-image-edit";
    public override bool NormalizesSourceResolution => true;
    public override bool SupportsEditQuality => true;

    protected override (double Megapixels, int ResolutionSteps)? EtaBudget(MiniMaxH3ImageEditParams p) =>
        (p.Megapixels, MiniMaxH3ImageGraph.CanvasStep);

    protected override (int Width, int Height) EtaRenderSize(MiniMaxH3ImageEditParams p, ResolvedRequirements req,
        int sourceWidth, int sourceHeight, double? editMegapixels) =>
        BudgetScale.Snap(sourceWidth, sourceHeight, editMegapixels ?? p.Megapixels, MiniMaxH3ImageGraph.CanvasStep);

    protected override ComfyWorkflowGraph Build(MiniMaxH3ImageEditParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        string source = inputs.SourceImageName
            ?? throw new RenderValidationException("MiniMax-H3-Image editing needs a source image (<Picture 1>), but none was provided.");
        IReadOnlyList<string> references = inputs.ImageReferences;
        if (references.Count > p.ReferenceMax)
        {
            throw new RenderValidationException($"This configuration accepts at most {p.ReferenceMax} reference image(s) besides the source; got {references.Count}.");
        }

        ComfyWorkflowGraph g = new();
        (Output<Slot.Model> model, Output<Slot.Clip> clip, Output<Slot.Vae> vae) = MiniMaxH3ImageGraph.Load(g, req);

        double mp = inputs.EditMegapixels ?? p.Megapixels;
        g[MiniMaxH3ImageNodes.Source] = new LoadImage { Image = source };
        g[MiniMaxH3ImageNodes.ScaledSource] = new ImageScaleToTotalPixels
        {
            Image = LoadImage.ImageOut(MiniMaxH3ImageNodes.Source),
            UpscaleMethod = ComfyWidgets.Upscale.Lanczos,
            Megapixels = mp,
            ResolutionSteps = MiniMaxH3ImageGraph.CanvasStep,
        };
        g[MiniMaxH3ImageNodes.SourceSize] = new GetImageSize { Image = ImageScaleToTotalPixels.Out(MiniMaxH3ImageNodes.ScaledSource) };

        List<Output<Slot.Image>> pictures = new(references.Count + 1) { ImageScaleToTotalPixels.Out(MiniMaxH3ImageNodes.ScaledSource) };
        for (int i = 0; i < references.Count; i++)
        {
            string id = (MiniMaxH3ImageGraph.ReferenceNodeBase + i).ToString();
            g[id] = new LoadImage { Image = references[i] };
            pictures.Add(LoadImage.ImageOut(id));
        }

        g[MiniMaxH3ImageNodes.Encode] = new MiniMaxH3ImageEditConditioning
        {
            Clip = clip,
            Vae = vae,
            Prompt = inputs.Positive,
            Width = GetImageSize.WidthOut(MiniMaxH3ImageNodes.SourceSize),
            Height = GetImageSize.HeightOut(MiniMaxH3ImageNodes.SourceSize),
            PictureInputs = MiniMaxH3ImageEditConditioning.Pictures(pictures),
        };
        MiniMaxH3ImageGraph.SampleAndSave(g, new MiniMaxH3ImageSampling(p.Steps, p.Cfg, p.Sampler, p.Scheduler, p.Seed),
            model, vae,
            MiniMaxH3ImageEditConditioning.PositiveOut(MiniMaxH3ImageNodes.Encode),
            MiniMaxH3ImageEditConditioning.LatentOut(MiniMaxH3ImageNodes.Encode),
            OutputPrefixes.Edit);
        return g;
    }
}
