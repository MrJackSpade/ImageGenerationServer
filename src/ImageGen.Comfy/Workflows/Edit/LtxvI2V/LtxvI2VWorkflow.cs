namespace ImageGen.Comfy.Edit.LtxvI2V;

/// <summary>LTX-Video image-to-video: fast distilled model; source conditions frame 0. LTX has no CLIP in the
/// checkpoint — it loads an external T5.</summary>
public sealed class LtxvI2VWorkflow : EditWorkflow<LtxvI2VParams>
{
    public override string Name => "ltxv-i2v";
    public override WorkflowMedia Media => WorkflowMedia.Video;
    /// <summary>LTX VAE: 8× temporal compression → valid clip lengths are 8n+1 (mirrors the node's length step=8).</summary>
    public override FrameRule? FrameRule => new(1, 8);

    protected override ComfyWorkflowGraph Build(LtxvI2VParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new();
        LoadModel(g, p.Loader, p.WeightDtype, p.ClipType, req, inputs, out Output<Slot.Model> model0, out _, out Output<Slot.Vae> vae0);
        model0 = ComfyGraph.ApplyLora(g, model0, p.Lora, p.LoraStrength);   // optional anime-style LoRA on the LTX model
        long seed = ComfyGraph.Seed(p.Seed);
        int frames = p.Length;
        double fps = p.Fps;
        // LTX loads its own external T5 (clip_type "ltxv").
        g[Nodes.T5Loader] = new CLIPLoader { ClipName = req.TextEncoder(0), Type = ComfyWidgets.ClipType.Ltxv, Device = ComfyWidgets.Device.Default };
        Output<Slot.Clip> ltxClip = CLIPLoader.ClipOut(Nodes.T5Loader);
        double budgetMp = 0.39;   // LTX's native i2v megapixel budget — always applied (the source is scaled to it)
        g[Nodes.ScaledSource] = new ImageScaleToTotalPixels { Image = LoadImage.ImageOut(EditNodes.Source), UpscaleMethod = ComfyWidgets.Upscale.Lanczos, Megapixels = budgetMp, ResolutionSteps = 32 };
        g[Nodes.SourceSize] = new GetImageSize { Image = ImageScaleToTotalPixels.Out(Nodes.ScaledSource) };
        g[Nodes.Positive] = new CLIPTextEncode { Text = inputs.Positive, Clip = ltxClip };
        g[Nodes.Negative] = new CLIPTextEncode { Text = inputs.Negative ?? "", Clip = ltxClip };
        g[Nodes.ImgToVideo] = new LTXVImgToVideo { Positive = CLIPTextEncode.Out(Nodes.Positive), Negative = CLIPTextEncode.Out(Nodes.Negative), Vae = vae0, Image = ImageScaleToTotalPixels.Out(Nodes.ScaledSource), Width = GetImageSize.WidthOut(Nodes.SourceSize), Height = GetImageSize.HeightOut(Nodes.SourceSize), Length = frames, BatchSize = 1, Strength = 1.0 };
        g[Nodes.Conditioning] = new LTXVConditioning { Positive = LTXVImgToVideo.PositiveOut(Nodes.ImgToVideo), Negative = LTXVImgToVideo.NegativeOut(Nodes.ImgToVideo), FrameRate = fps };
        g[Nodes.Scheduler] = new LTXVScheduler { Steps = p.Steps, MaxShift = 2.05, BaseShift = 0.95, Stretch = true, Terminal = 0.1, Latent = LTXVImgToVideo.LatentOut(Nodes.ImgToVideo) };
        g[Nodes.SamplerSelect] = new KSamplerSelect { SamplerName = ComfyGraph.MapSampler(p.Sampler) };
        g[Nodes.Sampler] = new SamplerCustom { Model = model0, AddNoise = true, NoiseSeed = seed, Cfg = p.Cfg, Positive = LTXVConditioning.PositiveOut(Nodes.Conditioning), Negative = LTXVConditioning.NegativeOut(Nodes.Conditioning), Sampler = KSamplerSelect.Out(Nodes.SamplerSelect), Sigmas = LTXVScheduler.Out(Nodes.Scheduler), LatentImage = LTXVImgToVideo.LatentOut(Nodes.ImgToVideo) };
        g[Nodes.Decode] = new VAEDecode { Samples = SamplerCustom.Out(Nodes.Sampler), Vae = vae0 };
        g[Nodes.Save] = new SaveAnimatedWEBPLiteralFps { Images = VAEDecode.Out(Nodes.Decode), FilenamePrefix = OutputPrefixes.Edit, Fps = fps, Lossless = false, Quality = 80, Method = ComfyWidgets.WebpMethod.Default };
        return g;
    }
}
