namespace ImageGen.Comfy.Generation.LtxV2T2V;

/// <summary>LTX-2 (and 2.3 / 2.5) TEXT-to-video — the generation-side sibling of <see cref="Edit.LtxV2I2V.LtxV2I2VWorkflow"/>.
/// Same LTXV sampler chain (<c>LTXVConditioning → LTXVScheduler → SamplerCustom</c>), but the latent is a from-scratch
/// <c>EmptyLTXVLatentVideo</c> sized by the request rather than an image-conditioned <c>LTXVImgToVideo</c>. Loader head is
/// a UNETLoader (int8-convrot safetensors) + a single <c>CLIPLoader</c> (type "ltxv") for the Gemma text encoder +
/// VAELoader, all driven off the config's requirements. Configurations with an <c>audio_vae</c> model ref build and
/// sample a joint AV latent and save an MP4 with synchronized audio; older video-only configurations retain the
/// silent animated-WEBP path.</summary>
public sealed class LtxV2T2VWorkflow : Txt2ImgWorkflow<LtxV2T2VParams>
{
    public override string Name => "ltx2-t2v";
    public override WorkflowMedia Media => WorkflowMedia.Video;
    public override bool PromptDirectsMotion => true;
    public override IReadOnlyList<ParamSpec> Schema =>
    [
        .. Txt2ImgWorkflowBase.SharedSchema,
        new() { Key = WorkflowParamKeys.AudioVae, Type = ParamType.String, IsModelRef = true, Label = "Audio VAE" },
    ];

    /// <summary>LTX VAE: 8× temporal compression → valid clip lengths are 8n+1 (mirrors the length step=8).</summary>
    public override FrameRule? FrameRule => new(1, 8);

    protected override ComfyWorkflowGraph Build(LtxV2T2VParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new();
        g[Nodes.Model] = ComfyGraph.DiffusionLoaderNode(req.RequiredCheckpoint());   // UNETLoader (.safetensors int8-convrot, native dtype)
        Output<Slot.Model> model0 = UNETLoader.ModelOut(Nodes.Model);
        model0 = ComfyGraph.ApplyLora(g, model0, p.Lora, p.LoraStrength);   // optional style LoRA
        model0 = CkAttention.Apply(g, model0, p.CkAttention, Nodes.CkAttention);
        // Single CLIPLoader: the LTX-2.5 gemma-with-proj encoder is a self-contained file the ltxv clip path detects and
        // loads as the LTXAV Gemma encoder. (LTX-2/2.3 split gemma + projection across two files and used DualCLIPLoader.)
        g[Nodes.Clip] = new CLIPLoader { ClipName = req.TextEncoder(0), Type = p.RequiredClipType(), Device = ComfyWidgets.Device.Default };
        Output<Slot.Clip> clip0 = new(Nodes.Clip, 0);
        g[Nodes.Vae] = new VAELoader { VaeName = req.RequiredVae() };
        Output<Slot.Vae> vae0 = VAELoader.VaeOut(Nodes.Vae);
        Output<Slot.Vae>? audioVae = null;
        if (!string.IsNullOrWhiteSpace(p.AudioVae))
        {
            g[T2VNodes.AudioVae] = new VAELoader { VaeName = p.AudioVae };
            audioVae = VAELoader.VaeOut(T2VNodes.AudioVae);
        }

        (int w, int h) = RenderSize(p, req, inputs);
        int frames = p.Length;
        double fps = p.Fps;
        long seed = ComfyGraph.Seed(p.Seed);

        g[Nodes.Positive] = new CLIPTextEncode { Text = inputs.Positive, Clip = clip0 };
        g[Nodes.Negative] = new CLIPTextEncode { Text = inputs.Negative ?? "", Clip = clip0 };
        g[Nodes.Latent] = new EmptyLTXVLatentVideo { Width = w, Height = h, Length = frames, BatchSize = 1 };
        Output<Slot.Latent> sampleLatent = EmptyLTXVLatentVideo.Out(Nodes.Latent);
        if (audioVae is { } av)
        {
            g[T2VNodes.AudioLatent] = new LTXVEmptyLatentAudio { FramesNumber = frames, FrameRate = fps, BatchSize = 1, AudioVae = av };
            g[T2VNodes.AvLatent] = new LTXVConcatAVLatent { VideoLatent = sampleLatent, AudioLatent = LTXVEmptyLatentAudio.Out(T2VNodes.AudioLatent) };
            sampleLatent = LTXVConcatAVLatent.Out(T2VNodes.AvLatent);
        }

        g[T2VNodes.Conditioning] = new LTXVConditioning { Positive = CLIPTextEncode.Out(Nodes.Positive), Negative = CLIPTextEncode.Out(Nodes.Negative), FrameRate = fps };
        g[T2VNodes.Scheduler] = new LTXVScheduler { Steps = p.Steps, MaxShift = 2.05, BaseShift = 0.95, Stretch = true, Terminal = 0.1, Latent = sampleLatent };
        g[T2VNodes.SamplerSelect] = new KSamplerSelect { SamplerName = ComfyGraph.MapSampler(p.Sampler) };
        g[Nodes.Sampler] = new SamplerCustom { Model = model0, AddNoise = true, NoiseSeed = seed, Cfg = p.RequiredCfg(), Positive = LTXVConditioning.PositiveOut(T2VNodes.Conditioning), Negative = LTXVConditioning.NegativeOut(T2VNodes.Conditioning), Sampler = KSamplerSelect.Out(T2VNodes.SamplerSelect), Sigmas = LTXVScheduler.Out(T2VNodes.Scheduler), LatentImage = sampleLatent };
        if (audioVae is { } decodeAudioVae)
        {
            g[T2VNodes.SeparateAv] = new LTXVSeparateAVLatent { AvLatent = SamplerCustom.Out(Nodes.Sampler) };
            g[Nodes.Decode] = new VAEDecode { Samples = LTXVSeparateAVLatent.VideoOut(T2VNodes.SeparateAv), Vae = vae0 };
            g[T2VNodes.AudioDecode] = new LTXVAudioVAEDecode { Samples = LTXVSeparateAVLatent.AudioOut(T2VNodes.SeparateAv), AudioVae = decodeAudioVae };
            g[T2VNodes.CreateVideo] = new CreateVideo { Images = VAEDecode.Out(Nodes.Decode), Fps = fps, Audio = LTXVAudioVAEDecode.Out(T2VNodes.AudioDecode) };
            g[Nodes.Save] = new SaveVideo { Video = CreateVideo.Out(T2VNodes.CreateVideo), FilenamePrefix = OutputPrefixes.Generate, Format = ComfyWidgets.SaveFormat.Auto, Codec = ComfyWidgets.VideoCodec.Auto };
        }
        else
        {
            g[Nodes.Decode] = new VAEDecode { Samples = SamplerCustom.Out(Nodes.Sampler), Vae = vae0 };
            g[Nodes.Save] = new SaveAnimatedWEBPLiteralFps { Images = VAEDecode.Out(Nodes.Decode), FilenamePrefix = OutputPrefixes.Generate, Fps = fps, Lossless = false, Quality = 80, Method = ComfyWidgets.WebpMethod.Default };
        }

        return g;
    }
}

/// <summary>This workflow's own node ids for the LTX sampler chain; the shared txt2img <c>Nodes</c> covers
/// model/clip/vae/encode/latent/sampler/decode/save. Distinct from those ids so the emitted graph has no collisions.</summary>
file static class T2VNodes
{
    public const string Conditioning = "54";
    public const string Scheduler = "55";
    public const string SamplerSelect = "56";
    public const string AudioVae = "57";
    public const string AudioLatent = "58";
    public const string AvLatent = "59";
    public const string SeparateAv = "60";
    public const string AudioDecode = "61";
    public const string CreateVideo = "62";
}
