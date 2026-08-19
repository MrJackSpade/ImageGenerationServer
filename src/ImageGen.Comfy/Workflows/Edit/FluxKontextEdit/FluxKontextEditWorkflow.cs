using ImageGen.Application.Rendering;

namespace ImageGen.Comfy.Edit.FluxKontextEdit;

/// <summary>Flux.1 Kontext image edit. Single-image native; multi-image uses the verified ImageStitch method
/// (stitch source+refs into one image, encode as the single reference latent; output stays source-sized).</summary>
public sealed class FluxKontextEditWorkflow : EditWorkflow<FluxKontextParams>
{
    public override bool NormalizesSourceResolution => true;
    public override bool SupportsEditQuality => true;
    public override string Name => "flux1-kontext";

    protected override (int Width, int Height) EtaRenderSize(FluxKontextParams p, ResolvedRequirements req,
        int sourceWidth, int sourceHeight, double? editMegapixels) =>
        BudgetScale.Snap(sourceWidth, sourceHeight,
            editMegapixels ?? EditWorkingResolution.NativeMegapixels, EditWorkingResolution.NativeStep);

    protected override ComfyWorkflowGraph Build(FluxKontextParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new();
        LoadModel(g, p.Loader, p.WeightDtype, p.ClipType, req, inputs, out Output<Slot.Model> model0, out Output<Slot.Clip> clip0, out Output<Slot.Vae> vae0);
        long seed = ComfyGraph.Seed(p.Seed);
        IReadOnlyList<string> refNames = inputs.ImageReferences;

        g[Nodes.Positive] = new CLIPTextEncode { Text = inputs.Positive, Clip = clip0 };
        double budgetMp = inputs.EditMegapixels ?? EditWorkingResolution.NativeMegapixels;
        g[Nodes.SourceScale] = new ImageScaleToTotalPixels { Image = LoadImage.ImageOut(EditNodes.Source),
            UpscaleMethod = ComfyWidgets.Upscale.Lanczos, Megapixels = budgetMp, ResolutionSteps = EditWorkingResolution.NativeStep };
        g[Nodes.SourceEncode] = new VAEEncode { Pixels = ImageScaleToTotalPixels.Out(Nodes.SourceScale), Vae = vae0 };
        // No reference_max declared → this editor takes no refs (capacity 0). Supplying references anyway is REFUSED,
        // not silently ignored.
        int rm = p.ReferenceMax ?? 0;
        if (refNames.Count > rm)
        {
            throw new RenderValidationException($"This configuration accepts at most {rm} reference image(s); got {refNames.Count}.");
        }

        int fn = refNames.Count;
        Output<Slot.Latent> refLatent;
        if (fn > 0)
        {
            Output<Slot.Image> stitched = ImageScaleToTotalPixels.Out(Nodes.SourceScale);
            for (int i = 0; i < fn; i++)
            {
                string load = $"{40 + i}", stitch = $"{50 + i}", scale = $"{60 + i}";
                g[load] = new LoadImage { Image = refNames[i] };
                g[scale] = new ImageScaleToTotalPixels { Image = LoadImage.ImageOut(load), UpscaleMethod = ComfyWidgets.Upscale.Lanczos,
                    Megapixels = budgetMp, ResolutionSteps = EditWorkingResolution.NativeStep };
                g[stitch] = new ImageStitch { Image1 = stitched, Image2 = ImageScaleToTotalPixels.Out(scale), Direction = ComfyWidgets.Stitch.Right, MatchImageSize = true, SpacingWidth = 0, SpacingColor = ComfyWidgets.Spacing.White };
                stitched = ImageStitch.Out(stitch);
            }

            g[Nodes.StitchEncode] = new VAEEncode { Pixels = stitched, Vae = vae0 };
            refLatent = VAEEncode.Out(Nodes.StitchEncode);
        }
        else
        {
            refLatent = VAEEncode.Out(Nodes.SourceEncode);
        }

        g[Nodes.RefLatent] = new ReferenceLatent { Conditioning = CLIPTextEncode.Out(Nodes.Positive), Latent = refLatent };
        g[Nodes.Guidance] = new FluxGuidance { Conditioning = ReferenceLatent.Out(Nodes.RefLatent), Guidance = p.Guidance };
        g[Nodes.NegativeZero] = new ConditioningZeroOut { Conditioning = CLIPTextEncode.Out(Nodes.Positive) };
        g[Nodes.Sampler] = new KSampler
        {
            Seed = seed,
            Steps = p.Steps,
            Cfg = p.Cfg,
            SamplerName = ComfyGraph.MapSampler(p.Sampler),
            Scheduler = ComfyGraph.MapScheduler(p.Scheduler),
            Denoise = 1.0,
            Model = model0,
            Positive = FluxGuidance.Out(Nodes.Guidance),
            Negative = ConditioningZeroOut.Out(Nodes.NegativeZero),
            LatentImage = VAEEncode.Out(Nodes.SourceEncode),
        };
        g[Nodes.Decode] = new VAEDecode { Samples = KSampler.Out(Nodes.Sampler), Vae = vae0 };
        g[Nodes.Save] = new SaveImage { Images = VAEDecode.Out(Nodes.Decode), FilenamePrefix = OutputPrefixes.Edit };
        return g;
    }
}
