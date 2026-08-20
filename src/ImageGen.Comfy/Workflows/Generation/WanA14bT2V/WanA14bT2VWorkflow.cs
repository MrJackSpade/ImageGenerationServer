namespace ImageGen.Comfy.Generation.WanA14bT2V;

/// <summary>Wan 2.2 T2V-A14B text→video (two-expert MoE). No source image — an EmptyHunyuanLatentVideo seeds the
/// clip and the conditioning feeds the two KSamplerAdvanced stages directly.</summary>
public sealed class WanA14bT2VWorkflow : Txt2ImgWorkflow<WanA14bT2VParams>
{
    /// <inheritdoc/>
    public override IReadOnlyList<ParamSpec> Schema =>
    [
        .. Txt2ImgWorkflowBase.SharedSchema,
        new() { Key = WorkflowParamKeys.UnetLow, Type = ParamType.String, IsModelRef = true, Label = "Low-noise expert" },
    ];

    public override string Name => "wan22-t2v-a14b";
    public override WorkflowMedia Media => WorkflowMedia.Video;
    /// <summary>Wan VAE: 4× temporal compression → valid clip lengths are 4n+1.</summary>
    public override FrameRule? FrameRule => new(1, 4);

    /// <summary>The MoE experts + samplers ("4"/"5"/"41"/"51"/"3"/"31") are written by Vid; Clip/Vae/Positive/Negative/
    /// Decode/Save reuse the inherited txt2img roles; only the empty video latent is an own node.</summary>
    protected override ComfyWorkflowGraph Build(WanA14bT2VParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new();
        string sampler = ComfyGraph.MapSampler(p.Sampler);
        string scheduler = ComfyGraph.MapScheduler(p.Scheduler);
        (Output<Slot.Model> mh, Output<Slot.Model> ml) = Vid.LoadExperts(g, req.RequiredCheckpoint(), p.UnetLow, p.Shift, p.CkAttention);
        g[EditNodes.Clip] = new CLIPLoader { ClipName = req.TextEncoder(0), Type = ComfyWidgets.ClipType.Wan, Device = ComfyWidgets.Device.Default };
        Output<Slot.Clip> clip = CLIPLoader.ClipOut(EditNodes.Clip);
        g[EditNodes.Vae] = new VAELoader { VaeName = req.RequiredVae() };
        Output<Slot.Vae> vae = VAELoader.VaeOut(EditNodes.Vae);

        (int w, int h) = RenderSize(p, req, inputs);
        int len = p.Length;
        double fps = p.Fps;
        g[Nodes.Positive] = new CLIPTextEncode { Text = inputs.Positive, Clip = clip };
        g[Nodes.Negative] = new CLIPTextEncode { Text = inputs.Negative ?? "", Clip = clip };
        g[WanA14bT2VWorkflowNodes.VideoLatent] = new EmptyHunyuanLatentVideo { Width = w, Height = h, Length = len, BatchSize = 1 };
        Output<Slot.Latent> outLat = Vid.MoESample(g, mh, ml, CLIPTextEncode.Out(Nodes.Positive), CLIPTextEncode.Out(Nodes.Negative), EmptyHunyuanLatentVideo.Out(WanA14bT2VWorkflowNodes.VideoLatent), p.Steps, p.Boundary, p.CfgHigh, p.CfgLow, sampler, scheduler, p.RefinerSteps, ComfyGraph.Seed(p.Seed));
        g[Nodes.Decode] = new VAEDecode { Samples = outLat, Vae = vae };
        g[Nodes.Save] = new SaveAnimatedWEBPLiteralFps { Images = VAEDecode.Out(Nodes.Decode), FilenamePrefix = OutputPrefixes.Generate, Fps = fps, Lossless = false, Quality = 80, Method = ComfyWidgets.WebpMethod.Default };
        return g;
    }
}
