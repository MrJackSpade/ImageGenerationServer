namespace ImageGen.Comfy.Generation.HunyuanVideo15T2V;

/// <summary>HunyuanVideo 1.5 text→video (720p). UNETLoader + the Qwen2.5-VL/ByT5 DualCLIPLoader (type
/// "hunyuan_video_15") + ModelSamplingSD3 + EmptyHunyuanVideo15Latent + a CFGGuider/SamplerCustomAdvanced chain
/// (real CFG, negatives work). The text→video sibling of the 480p i2v editor already in the catalog.</summary>
public sealed class HunyuanVideo15T2VWorkflow : Txt2ImgWorkflow<HunyuanVideo15T2VParams>
{
    public override IReadOnlyList<Type> ParameterContracts =>
        [typeof(HunyuanVideo15T2VParams), typeof(HunyuanVideo15T2VNoSrParams), typeof(HunyuanVideo15T2VSrParams)];
    public override string Name => "hunyuanvideo15-t2v";
    public override WorkflowMedia Media => WorkflowMedia.Video;
    /// <summary>HunyuanVideo 1.5 VAE: valid clip lengths are 4n+1.</summary>
    public override FrameRule? FrameRule => new(1, 4);
    public override IReadOnlyList<ParamSpec> Schema => [.. Txt2ImgWorkflowBase.SharedSchema, .. HunyuanSr.Schema];

    protected override ComfyWorkflowGraph Build(HunyuanVideo15T2VParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new();
        string sampler = ComfyGraph.MapSampler(p.Sampler);
        string scheduler = ComfyGraph.MapScheduler(p.Scheduler);
        long seed = ComfyGraph.Seed(p.Seed);
        g[EditNodes.Model] = ComfyGraph.DiffusionLoaderNode(req.RequiredCheckpoint());
        g[HunyuanVideo15T2VWorkflowNodes.ModelSampling] = new ModelSamplingSD3 { Model = UNETLoader.ModelOut(EditNodes.Model), Shift = p.Shift };
        Output<Slot.Model> model = CkAttention.Apply(g, ModelSamplingSD3.Out(HunyuanVideo15T2VWorkflowNodes.ModelSampling), p.CkAttention, Nodes.CkAttention);
        g[EditNodes.Clip] = new DualCLIPLoader { ClipName1 = req.TextEncoder(0), ClipName2 = req.TextEncoder(1), Type = ComfyWidgets.ClipType.HunyuanVideo15, Device = ComfyWidgets.Device.Default };
        Output<Slot.Clip> clip = DualCLIPLoader.ClipOut(EditNodes.Clip);
        g[EditNodes.Vae] = new VAELoader { VaeName = req.RequiredVae() };
        Output<Slot.Vae> vae = VAELoader.VaeOut(EditNodes.Vae);

        (int w, int h) = RenderSize(p, req, inputs);
        int len = p.Length;
        double fps = p.Fps;
        g[Nodes.Positive] = new CLIPTextEncode { Text = inputs.Positive, Clip = clip };
        g[Nodes.Negative] = new CLIPTextEncode { Text = inputs.Negative ?? "", Clip = clip };
        g[HunyuanVideo15T2VWorkflowNodes.VideoLatent] = new EmptyHunyuanVideo15Latent { Width = w, Height = h, Length = len, BatchSize = 1 };
        g[HunyuanVideo15T2VWorkflowNodes.Scheduler] = new BasicScheduler { Model = model, Scheduler = scheduler, Steps = p.Steps, Denoise = 1.0 };
        g[HunyuanVideo15T2VWorkflowNodes.SamplerSelect] = new KSamplerSelect { SamplerName = sampler };
        g[HunyuanVideo15T2VWorkflowNodes.Noise] = new RandomNoise { NoiseSeed = seed };
        g[HunyuanVideo15T2VWorkflowNodes.Guider] = new CFGGuider { Model = model, Positive = CLIPTextEncode.Out(Nodes.Positive), Negative = CLIPTextEncode.Out(Nodes.Negative), Cfg = p.RequiredCfg() };
        g[Nodes.Sampler] = new SamplerCustomAdvanced { Noise = RandomNoise.Out(HunyuanVideo15T2VWorkflowNodes.Noise), Guider = CFGGuider.Out(HunyuanVideo15T2VWorkflowNodes.Guider), Sampler = KSamplerSelect.Out(HunyuanVideo15T2VWorkflowNodes.SamplerSelect), Sigmas = BasicScheduler.Out(HunyuanVideo15T2VWorkflowNodes.Scheduler), LatentImage = EmptyHunyuanVideo15Latent.Out(HunyuanVideo15T2VWorkflowNodes.VideoLatent) };
        // Optional super-resolution second pass (1080p) — present iff this is the SR contract. t2v has no source image,
        // so no start_image/CLIP-vision cues.
        IHunyuanSrPass? srPass = HunyuanSr.PassOf(p);
        Output<Slot.Latent> outLatent = srPass is null
            ? SamplerCustomAdvanced.Out(Nodes.Sampler)
            : HunyuanSr.Refine(g, srPass, SamplerCustomAdvanced.Out(Nodes.Sampler), CLIPTextEncode.Out(Nodes.Positive), CLIPTextEncode.Out(Nodes.Negative), vae, null, null, sampler, scheduler, seed);
        g[Nodes.Decode] = srPass is not null
            ? new VAEDecodeTiled { Samples = outLatent, Vae = vae, TileSize = 256, Overlap = 64, TemporalSize = 64, TemporalOverlap = 8 }
            : new VAEDecode { Samples = outLatent, Vae = vae };
        g[Nodes.Save] = new SaveAnimatedWEBPLiteralFps { Images = new Output<Slot.Image>(Nodes.Decode, 0), FilenamePrefix = OutputPrefixes.Generate, Fps = fps, Lossless = false, Quality = 80, Method = ComfyWidgets.WebpMethod.Default };
        return g;
    }
}
