namespace ImageGen.Comfy.Edit.LtxV2I2V;

/// <summary>LTX-2 (19B) image-to-video. Same LTXV sampler chain as the 0.9.8 editor, but the model is a GGUF unet
/// (UnetLoaderGGUF) and the text encoder is the Gemma + LTX-connectors pair (DualCLIPLoader, type "ltxv") — both
/// supplied by the shared <see cref="EditWorkflow{TParams}.LoadModel"/> head when the config sets loader=unet_gguf,
/// dual=true. The 11GB distilled Q4 GGUF + 8.8GB Gemma encoder total ~20GB; no offload flags. Distilled: ~8 steps,
/// cfg 1. Animates anime natively without a LoRA.</summary>
public sealed class LtxV2I2VWorkflow : EditWorkflow<LtxV2I2VParams>
{
    public override string Name => "ltx2-i2v";
    public override WorkflowMedia Media => WorkflowMedia.Video;
    /// <summary>LTX VAE: 8× temporal compression → valid clip lengths are 8n+1 (mirrors the node's length step=8).</summary>
    public override FrameRule? FrameRule => new(1, 8);

    /// <summary>The shared edit menu plus the per-config i2v <c>megapixels</c> budget control (#186).</summary>
    public override IReadOnlyList<ParamSpec> Schema => [.. EditWorkflowBase.SharedSchema, VideoSizeSchema.Megapixels, .. CkAttention.Schema];

    /// <summary>LTX-2's i2v snap grid (32-px). The megapixel BUDGET is the per-config <c>megapixels</c> control (#186),
    /// read off the params record.</summary>
    private const int BudgetSteps = 32;

    protected override (double Megapixels, int ResolutionSteps)? EtaBudget(LtxV2I2VParams p) => (p.Megapixels, BudgetSteps);

    protected override ComfyWorkflowGraph Build(LtxV2I2VParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new();
        LoadModel(g, p.Loader, p.WeightDtype, p.ClipType, req, inputs, out Output<Slot.Model> model0, out Output<Slot.Clip> clip0, out Output<Slot.Vae> vae0);
        model0 = ComfyGraph.ApplyLora(g, model0, p.Lora, p.LoraStrength);   // optional anime-style LoRA on the LTX-2 model
        model0 = CkAttention.Apply(g, model0, p.CkAttention, Nodes.CkAttention);
        long seed = ComfyGraph.Seed(p.Seed);
        int frames = p.Length;
        double fps = p.Fps;
        double budgetMp = p.Megapixels;   // the per-config i2v megapixel budget (the source is scaled to it)
        g[Nodes.Scale] = new ImageScaleToTotalPixels { Image = LoadImage.ImageOut(EditNodes.Source), UpscaleMethod = ComfyWidgets.Upscale.Lanczos, Megapixels = budgetMp, ResolutionSteps = BudgetSteps };
        g[Nodes.Size] = new GetImageSize { Image = ImageScaleToTotalPixels.Out(Nodes.Scale) };
        g[Nodes.Positive] = new CLIPTextEncode { Text = inputs.Positive, Clip = clip0 };
        g[Nodes.Negative] = new CLIPTextEncode { Text = inputs.Negative ?? "", Clip = clip0 };
        g[Nodes.ImgToVideo] = new LTXVImgToVideo { Positive = CLIPTextEncode.Out(Nodes.Positive), Negative = CLIPTextEncode.Out(Nodes.Negative), Vae = vae0, Image = ImageScaleToTotalPixels.Out(Nodes.Scale), Width = GetImageSize.WidthOut(Nodes.Size), Height = GetImageSize.HeightOut(Nodes.Size), Length = frames, BatchSize = 1, Strength = 1.0 };
        g[Nodes.Conditioning] = new LTXVConditioning { Positive = LTXVImgToVideo.PositiveOut(Nodes.ImgToVideo), Negative = LTXVImgToVideo.NegativeOut(Nodes.ImgToVideo), FrameRate = fps };
        g[Nodes.Scheduler] = new LTXVScheduler { Steps = p.Steps, MaxShift = 2.05, BaseShift = 0.95, Stretch = true, Terminal = 0.1, Latent = LTXVImgToVideo.LatentOut(Nodes.ImgToVideo) };
        g[Nodes.SamplerSelect] = new KSamplerSelect { SamplerName = ComfyGraph.MapSampler(p.Sampler) };
        g[Nodes.Sampler] = new SamplerCustom { Model = model0, AddNoise = true, NoiseSeed = seed, Cfg = p.Cfg, Positive = LTXVConditioning.PositiveOut(Nodes.Conditioning), Negative = LTXVConditioning.NegativeOut(Nodes.Conditioning), Sampler = KSamplerSelect.Out(Nodes.SamplerSelect), Sigmas = LTXVScheduler.Out(Nodes.Scheduler), LatentImage = LTXVImgToVideo.LatentOut(Nodes.ImgToVideo) };
        g[Nodes.Decode] = new VAEDecode { Samples = SamplerCustom.Out(Nodes.Sampler), Vae = vae0 };
        g[Nodes.Save] = new SaveAnimatedWEBPLiteralFps { Images = VAEDecode.Out(Nodes.Decode), FilenamePrefix = OutputPrefixes.Edit, Fps = fps, Lossless = false, Quality = 80, Method = ComfyWidgets.WebpMethod.Default };
        return g;
    }
}
