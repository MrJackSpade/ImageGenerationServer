namespace ImageGen.Comfy.Edit.WanI2V;

/// <summary>Wan 2.2 TI2V-5B image-to-video: the source image is the first frame; output is an animated WEBP. The
/// text prompt drives the motion/scene.</summary>
public sealed class WanI2VWorkflow : EditWorkflow<WanI2VParams>
{
    public override string Name => "wan22-ti2v-5b";
    public override WorkflowMedia Media => WorkflowMedia.Video;
    /// <summary>Wan VAE: 4× temporal compression → valid clip lengths are 4n+1 (mirrors the node's length step=4).</summary>
    public override FrameRule? FrameRule => new(1, 4);

    /// <summary>Flow shift. The Wan2.2 repo's ti2v_5B config runs 5.0; without an explicit node ComfyUI silently
    /// applies its own Wan default of 8.0, so the graph pins the reference value.</summary>
    public override IReadOnlyList<ParamSpec> Schema => _schema;
    private static readonly IReadOnlyList<ParamSpec> _schema =
    [
        .. EditWorkflowBase.SharedSchema,
        new() { Key = WorkflowParamKeys.Shift, Type = ParamType.Double, Min = 1.0, Max = 12.0, Step = 0.1, Label = "Flow shift" },
    ];

    protected override ComfyWorkflowGraph Build(WanI2VParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new();
        LoadModel(g, p.Loader, p.WeightDtype, p.ClipType, req, inputs, out Output<Slot.Model> model0, out Output<Slot.Clip> clip0, out Output<Slot.Vae> vae0);
        model0 = ComfyGraph.ApplyLora(g, model0, p.Lora, p.LoraStrength);   // optional anime-style LoRA (e.g. Flat Color) on the WAN model
        g[Nodes.ModelSampling] = new ModelSamplingSD3 { Model = model0, Shift = p.Shift };
        model0 = ModelSamplingSD3.Out(Nodes.ModelSampling);
        long seed = ComfyGraph.Seed(p.Seed);
        int len = p.Length;
        double fps = p.Fps;
        double budgetMp = 0.9;   // Wan's native i2v megapixel budget — always applied (the source is scaled to it)
        g[Nodes.ScaleSource] = new ImageScaleToTotalPixels { Image = LoadImage.ImageOut(EditNodes.Source), UpscaleMethod = ComfyWidgets.Upscale.Lanczos, Megapixels = budgetMp, ResolutionSteps = 32 };
        g[Nodes.ImageSize] = new GetImageSize { Image = ImageScaleToTotalPixels.Out(Nodes.ScaleSource) };
        g[Nodes.Positive] = new CLIPTextEncode { Text = inputs.Positive, Clip = clip0 };
        g[Nodes.Negative] = new CLIPTextEncode { Text = inputs.Negative ?? "", Clip = clip0 };
        g[Nodes.Latent] = new Wan22ImageToVideoLatent { Vae = vae0, Width = GetImageSize.WidthOut(Nodes.ImageSize), Height = GetImageSize.HeightOut(Nodes.ImageSize), Length = len, BatchSize = 1, StartImage = ImageScaleToTotalPixels.Out(Nodes.ScaleSource) };
        g[Nodes.Sampler] = new KSampler
        {
            Seed = seed,
            Steps = p.Steps,
            Cfg = p.Cfg,
            SamplerName = ComfyGraph.MapSampler(p.Sampler),
            Scheduler = ComfyGraph.MapScheduler(p.Scheduler),
            Denoise = 1.0,
            Model = model0,
            Positive = CLIPTextEncode.Out(Nodes.Positive),
            Negative = CLIPTextEncode.Out(Nodes.Negative),
            LatentImage = Wan22ImageToVideoLatent.Out(Nodes.Latent),
        };
        g[Nodes.Decode] = new VAEDecode { Samples = KSampler.Out(Nodes.Sampler), Vae = vae0 };
        g[Nodes.Save] = new SaveAnimatedWEBPLiteralFps { Images = VAEDecode.Out(Nodes.Decode), FilenamePrefix = OutputPrefixes.Edit, Fps = fps, Lossless = false, Quality = 80, Method = ComfyWidgets.WebpMethod.Default };
        return g;
    }
}