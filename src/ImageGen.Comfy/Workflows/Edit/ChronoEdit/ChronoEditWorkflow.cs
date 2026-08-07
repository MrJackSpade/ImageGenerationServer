namespace ImageGen.Comfy.Edit.ChronoEdit;

/// <summary>
/// ChronoEdit-14B instruction image editor (NVIDIA). It's a Wan2.1-I2V backbone repurposed for editing: the source
/// image conditions a very short "trajectory" (a few frames) and we keep the LAST frame as the edited result
/// ("temporal reasoning"). Runs entirely on native ComfyUI nodes — no custom node. Reuses the Wan UMT5 text encoder
/// and the Wan 2.1 VAE, plus the standard CLIP-ViT-H clip-vision. A distilled LoRA enables the fast 20-step/CFG4 path.
/// Mirrors the official <c>image_chrono_edit_14B</c> template.
/// </summary>
public sealed class ChronoEditWorkflow : EditWorkflow<ChronoEditParams>
{
    public override string Name => "chronoedit";

    /// <summary>ChronoEdit's native ~0.5&#160;MP budget (source scaled to it on a 32-px grid) — single source for both
    /// the graph's scale node and the ETA render-size.</summary>
    private const double BudgetMp = 0.52;
    private const int BudgetSteps = 32;

    protected override (double Megapixels, int ResolutionSteps)? EtaBudget(ChronoEditParams p) => (BudgetMp, BudgetSteps);

    protected override ComfyWorkflowGraph Build(ChronoEditParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new();
        LoadModel(g, p.Loader, p.WeightDtype, p.ClipType, req, inputs, out Output<Slot.Model> model0, out Output<Slot.Clip> clip0, out Output<Slot.Vae> vae0);   // 4=unet,5=clip(wan),6=vae(wan2.1),10=LoadImage
        model0 = ComfyGraph.ApplyLora(g, model0, p.Lora, p.LoraStrength);               // distilled LoRA (fast 20-step path)
        long seed = ComfyGraph.Seed(p.Seed);
        int len = p.Length;                                                            // ChronoEdit's short trajectory
        double budgetMp = BudgetMp;   // ChronoEdit's native ~0.5MP budget (720² ≈ 0.52MP) — always applied (the source is scaled to it)

        // Sampling fix-ups the template applies to the Wan model for ChronoEdit.
        g[Nodes.ModelSampling] = new ModelSamplingSD3 { Model = model0, Shift = 5.0 };
        g[Nodes.ScaleRope] = new ScaleROPE { Model = ModelSamplingSD3.Out(Nodes.ModelSampling), ScaleX = 1.0, ShiftX = 0.0, ScaleY = 1.0, ShiftY = 0.0, ScaleT = 1.0, ShiftT = 0.0 };
        Output<Slot.Model> ksModel = ScaleROPE.Out(Nodes.ScaleRope);

        // Source image, scaled to a ~0.5MP budget (preserves aspect; 720² ≈ 0.52MP), reused as both the i2v start
        // frame and the clip-vision input.
        g[Nodes.ScaledSource] = new ImageScaleToTotalPixels { Image = LoadImage.ImageOut(EditNodes.Source), UpscaleMethod = ComfyWidgets.Upscale.Lanczos, Megapixels = budgetMp, ResolutionSteps = BudgetSteps };
        g[Nodes.SourceSize] = new GetImageSize { Image = ImageScaleToTotalPixels.Out(Nodes.ScaledSource) };
        g[Nodes.ClipVisionLoaderNode] = new CLIPVisionLoader { ClipName = p.ClipVision };
        g[Nodes.ClipVisionEncodeNode] = new CLIPVisionEncode { ClipVision = CLIPVisionLoader.Out(Nodes.ClipVisionLoaderNode), Image = ImageScaleToTotalPixels.Out(Nodes.ScaledSource), Crop = ComfyWidgets.Crop.None };

        g[Nodes.PositiveEncode] = new CLIPTextEncode { Text = inputs.Positive, Clip = clip0 };
        g[Nodes.NegativeEncode] = new CLIPTextEncode { Text = Nodes.Negative, Clip = clip0 };

        // Wan2.1 i2v conditioning node: bakes the start image + clip-vision into pos/neg conditioning + the latent.
        g[Nodes.I2VConditioning] = new WanImageToVideo
        {
            Positive = CLIPTextEncode.Out(Nodes.PositiveEncode),
            Negative = CLIPTextEncode.Out(Nodes.NegativeEncode),
            Vae = vae0,
            ClipVisionOutput = CLIPVisionEncode.Out(Nodes.ClipVisionEncodeNode),
            Width = GetImageSize.WidthOut(Nodes.SourceSize),
            Height = GetImageSize.HeightOut(Nodes.SourceSize),
            Length = len,
            BatchSize = 1,
            StartImage = ImageScaleToTotalPixels.Out(Nodes.ScaledSource),
        };
        g[Nodes.Sampler] = new KSampler
        {
            Seed = seed,
            Steps = p.Steps,
            Cfg = p.Cfg,
            SamplerName = ComfyGraph.MapSampler(p.Sampler),
            Scheduler = ComfyGraph.MapScheduler(p.Scheduler),
            Denoise = 1.0,
            Model = ksModel,
            Positive = WanImageToVideo.PositiveOut(Nodes.I2VConditioning),
            Negative = WanImageToVideo.NegativeOut(Nodes.I2VConditioning),
            LatentImage = WanImageToVideo.LatentOut(Nodes.I2VConditioning),
        };
        g[Nodes.Decode] = new VAEDecode { Samples = KSampler.Out(Nodes.Sampler), Vae = vae0 };
        // Keep the LAST frame of the short trajectory as the edited still.
        g[Nodes.LastFrame] = new ImageFromBatch { Image = VAEDecode.Out(Nodes.Decode), BatchIndex = Math.Max(0, len - 1), Length = 1 };
        g[Nodes.Save] = new SaveImage { Images = ImageFromBatch.Out(Nodes.LastFrame), FilenamePrefix = OutputPrefixes.Edit };
        return g;
    }
}
