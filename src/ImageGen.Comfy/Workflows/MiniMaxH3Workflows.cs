using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;

namespace ImageGen.Comfy;

/// <summary>
/// MiniMax-H3 — an omni-modal video model with NATIVE stereo audio (voice/SFX/music generated in the same forward
/// pass, not layered on after). A single "fl2va" diffusion model serves both text→video and image→video; the two
/// differ only in whether a first frame is fed to the one H3-specific node, <c>MiniMaxH3ImageToVideo</c>, which
/// encodes the prompt itself (no separate CLIPTextEncode) and emits (positive, latent). Distilled sampling
/// (BasicGuider, no CFG/negative) through a res_multistep SamplerCustomAdvanced chain, exactly like the official
/// ComfyUI templates (video_minimax_h3_{t2v,i2v}.json).
///
/// <para>UNLIKE every other video model here, H3 does NOT end in the silent <c>SaveAnimatedWEBP</c> — its whole
/// point is the audio, which WEBP cannot carry. The video latent decodes to frames and the SAME latent decodes to
/// audio; <c>CreateVideo</c> muxes them and <c>SaveVideo</c> writes a real mp4 with a baked-in stereo track. The
/// render pipeline stores/serves that mp4 by content-type (see <c>RenderOrchestrator.RunSlotAsync</c> +
/// <c>/image/{id}/mp4</c>), audio intact.</para>
///
/// <para>Weights: the int8-ConvRot diffusion + int8-ConvRot Qwen3-VL 32B text encoder (native tensor-core INT8 on
/// Ampere; the upstream template's nvfp4_awq encoder is Blackwell-only), both loaded through the plain
/// <c>UNETLoader</c>/<c>CLIPLoader</c> that read the embedded ConvRot metadata. Requires ComfyUI ≥ v0.30.1, which
/// adds <c>MiniMaxH3ImageToVideo</c> and the CLIPLoader <c>minimax</c> type.</para>
/// </summary>
file static class H3
{
    /// <summary>The audio VAE — a SECOND vae slot beyond the video VAE (<c>req.Vae</c>). A model-ref param resolved to
    /// this machine's bound file (linked in the config's <c>extra</c>), mirroring how the MoE/SR workflows carry a
    /// second model file.</summary>
    public static readonly ParamSpec[] ExtraSchema = new ParamSpec[]
    {
        new() { Key = WorkflowParamKeys.AudioVae, Type = ParamType.String, IsModelRef = true, Label = "Audio VAE" },
    };

    /// <summary>The shared T2V/I2V graph's node ids, named by role. The VALUE is the graph-local node key (preserved
    /// exactly, so the emitted graph stays byte-identical); the NAME replaces the bare numeric literals at the use
    /// sites.</summary>
    private static class Nodes
    {
        public const string Model = "4";
        public const string Clip = "20";
        public const string VideoVae = "21";
        public const string AudioVae = "22";
        public const string Source = "10";
        public const string ScaledSource = "11";
        public const string SourceSize = "15";
        public const string EndFrame = "12";
        public const string Encode = "14";
        public const string Scheduler = "55";
        public const string SamplerSelect = "56";
        public const string Noise = "57";
        public const string Guider = "58";
        public const string Sampler = "3";
        public const string VideoDecode = "8";
        public const string AudioDecode = "40";
        public const string CreateVideo = "41";
        public const string Save = "9";
    }

    /// <summary>The shared T2V/I2V graph (typed #93). <paramref name="i2v"/>: the source image is the first frame and
    /// the clip size derives from it (scaled to H3's ~1 MP budget); otherwise the size is the aspect map's
    /// <paramref name="t2vDims"/>. The graph is otherwise identical — one H3 node, one distilled sampler chain, dual
    /// (video+audio) decode, one mp4-with-audio. The scalar knobs are read TYPED off each workflow's params record and
    /// passed in; <paramref name="seed"/> is already resolved (<c>ComfyGraph.Seed</c>) and
    /// <paramref name="sampler"/>/<paramref name="scheduler"/> are the RAW Forge names (mapped here).</summary>
    public static ComfyWorkflowGraph Build(ResolvedRequirements req, WorkflowInputs inputs, bool i2v,
        string audioVae, int length, double fps, long seed, int steps, string sampler, string scheduler, (int w, int h)? t2vDims)
    {
        ComfyWorkflowGraph g = new ComfyWorkflowGraph();

        // Loaders. Diffusion via DiffusionLoaderNode → plain UNETLoader (int8 ConvRot loads natively, weight_dtype
        // default keeps its INT8). Qwen3-VL text encoder through CLIPLoader type "minimax". TWO VAEs: video (frames)
        // and audio (the native stereo track); the audio VAE is the audio_vae model-ref slot.
        g[Nodes.Model] = ComfyGraph.DiffusionLoaderNode(req.RequiredCheckpoint());   // H3 sets no weight_dtype → AutoWeightDtype (native INT8 ConvRot)
        Output<Slot.Model> model = UNETLoader.ModelOut(Nodes.Model);
        g[Nodes.Clip] = new CLIPLoader { ClipName = req.TextEncoder(0), Type = "minimax", Device = "default" };
        Output<Slot.Clip> clip = CLIPLoader.ClipOut(Nodes.Clip);
        g[Nodes.VideoVae] = new VAELoader { VaeName = req.RequiredVae() };
        Output<Slot.Vae> videoVae = VAELoader.VaeOut(Nodes.VideoVae);
        g[Nodes.AudioVae] = new VAELoader { VaeName = audioVae };
        Output<Slot.Vae> audioVaeRef = VAELoader.VaeOut(Nodes.AudioVae);

        // The single H3 conditioning+latent node. It encodes the prompt itself and emits (positive CONDITIONING, LATENT).
        if (i2v)
        {
            // Source = first frame. Scale to H3's ~1 MP budget (multiple of 32) and use those dims as the clip size, so
            // the clip keeps the source's aspect inside H3's canvas. An optional END frame pins the last frame.
            g[Nodes.Source] = new LoadImage { Image = inputs.SourceImageName ?? throw new RenderValidationException("MiniMax-H3 image→video needs a source image (the first frame), but none was provided.") };
            g[Nodes.ScaledSource] = new ImageScaleToTotalPixels { Image = LoadImage.ImageOut(Nodes.Source), UpscaleMethod = "lanczos", Megapixels = 1.0, ResolutionSteps = 32 };
            g[Nodes.SourceSize] = new GetImageSize { Image = ImageScaleToTotalPixels.Out(Nodes.ScaledSource) };
            Output<Slot.Image>? lastFrame = null;
            if (!string.IsNullOrEmpty(inputs.EndImageName))
            {
                g[Nodes.EndFrame] = new LoadImage { Image = inputs.EndImageName };
                lastFrame = LoadImage.ImageOut(Nodes.EndFrame);
            }
            g[Nodes.Encode] = new MiniMaxH3ImageToVideoI2V
            {
                Clip = clip,
                Vae = videoVae,
                Prompt = inputs.Positive,
                Length = length,
                Width = GetImageSize.WidthOut(Nodes.SourceSize),
                Height = GetImageSize.HeightOut(Nodes.SourceSize),
                FirstFrame = ImageScaleToTotalPixels.Out(Nodes.ScaledSource),
                LastFrame = lastFrame,
            };
        }
        else
        {
            (int w, int h) = t2vDims ?? throw new RenderValidationException("MiniMax-H3 text→video needs a render size, but none was resolved.");
            g[Nodes.Encode] = new MiniMaxH3ImageToVideoT2V { Clip = clip, Vae = videoVae, Prompt = inputs.Positive, Length = length, Width = w, Height = h };
        }
        Output<Slot.Conditioning> positive = new(Nodes.Encode, 0);
        Output<Slot.Latent> latent = new(Nodes.Encode, 1);

        // Distilled sampling: BasicGuider (no CFG, no negative) + a res_multistep SamplerCustomAdvanced chain.
        g[Nodes.Scheduler] = new BasicScheduler { Model = model, Scheduler = ComfyGraph.MapScheduler(scheduler), Steps = steps, Denoise = 1.0 };
        g[Nodes.SamplerSelect] = new KSamplerSelect { SamplerName = ComfyGraph.MapSampler(sampler) };
        g[Nodes.Noise] = new RandomNoise { NoiseSeed = seed };
        g[Nodes.Guider] = new BasicGuider { Model = model, Conditioning = positive };
        g[Nodes.Sampler] = new SamplerCustomAdvanced { Noise = RandomNoise.Out(Nodes.Noise), Guider = BasicGuider.Out(Nodes.Guider), Sampler = KSamplerSelect.Out(Nodes.SamplerSelect), Sigmas = BasicScheduler.Out(Nodes.Scheduler), LatentImage = latent };

        // Dual decode → one mp4 with audio. The SAME latent decodes to frames (video VAE) and to the native stereo
        // track (audio VAE); CreateVideo muxes them; SaveVideo writes a real mp4 (format/codec auto = h264/aac).
        g[Nodes.VideoDecode] = new VAEDecode { Samples = SamplerCustomAdvanced.Out(Nodes.Sampler), Vae = videoVae };
        g[Nodes.AudioDecode] = new VAEDecodeAudio { Samples = SamplerCustomAdvanced.Out(Nodes.Sampler), Vae = audioVaeRef };
        g[Nodes.CreateVideo] = new CreateVideo { Images = VAEDecode.Out(Nodes.VideoDecode), Fps = fps, Audio = VAEDecodeAudio.Out(Nodes.AudioDecode) };
        g[Nodes.Save] = new SaveVideo { Video = CreateVideo.Out(Nodes.CreateVideo), FilenamePrefix = i2v ? "forgemcp_edit" : "forgemcp", Format = "auto", Codec = "auto" };
        return g;
    }
}

/// <summary>MiniMax-H3 text→video parameters — the shared txt2img knobs plus the native-audio extras: the audio VAE
/// (a resolved model ref), the clip <c>length</c> (frames) and playback <c>fps</c>. The render size is read via the
/// base <c>Txt2ImgParams.Dims</c> (aspect map). <c>steps</c>/<c>sampler</c>/<c>scheduler</c> are the base's
/// <c>required</c> members; <c>seed</c> the single-sourced seed.</summary>
public sealed record MiniMaxH3Params : Txt2ImgParams
{
    [JsonPropertyName(WorkflowParamKeys.AudioVae)] public required string AudioVae { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Length)]   public required int Length { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Fps)]      public required double Fps { get; init; }
}

/// <summary>MiniMax-H3 image→video parameters (its own record — the H3 graph emits its own loaders, so none of the
/// shared edit loader-head knobs apply). The audio VAE (resolved model ref), clip <c>length</c>, playback <c>fps</c>,
/// sampler settings are <c>required</c>; <c>seed</c> is the app's single-sourced seed (defaulted).</summary>
public sealed record MiniMaxH3I2VParams
{
    [JsonPropertyName(WorkflowParamKeys.AudioVae)]  public required string AudioVae { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Length)]    public required int Length { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Fps)]       public required double Fps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Steps)]     public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Sampler)]   public required string Sampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Scheduler)] public required string Scheduler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)]      public long Seed { get; init; }
}

/// <summary>MiniMax-H3 text→video (with native audio). The fl2va model, no source frame — <c>MiniMaxH3ImageToVideo</c>
/// conditions on the prompt alone.</summary>
public sealed class MiniMaxH3T2VWorkflow : Txt2ImgWorkflow<MiniMaxH3Params>
{
    public override string Name => "minimax-h3-t2v";
    public override WorkflowMedia Media => WorkflowMedia.Video;
    /// <summary>H3 generates a native stereo audio track alongside the video (saved as an mp4 with sound).</summary>
    public override bool HasAudio => true;
    /// <summary>H3 VAE: valid clip length = 17n+5 (mirrors the node's length step=17, min=5).</summary>
    public override FrameRule? FrameRule => new(5, 17);
    public override IReadOnlyList<ParamSpec> Schema => Txt2ImgWorkflowBase.SharedSchema.Concat(H3.ExtraSchema).ToArray();

    protected override ComfyWorkflowGraph Build(MiniMaxH3Params p, ResolvedRequirements req, WorkflowInputs inputs)
        => H3.Build(req, inputs, i2v: false, p.AudioVae, p.Length, p.Fps, ComfyGraph.Seed(p.Seed), p.Steps, p.Sampler, p.Scheduler,
            p.Dims(ComfyGraph.NormalizeAspect(inputs.Aspect)));
}

/// <summary>MiniMax-H3 image→video (with native audio). The source image is the first frame; an optional last frame
/// (<see cref="SupportsEndFrame"/>) pins the ending. Same fl2va model as the T2V sibling.</summary>
public sealed class MiniMaxH3I2VWorkflow : EditWorkflow<MiniMaxH3I2VParams>
{
    public override string Name => "minimax-h3-i2v";
    public override WorkflowMedia Media => WorkflowMedia.Video;
    public override bool SupportsEndFrame => true;
    /// <summary>H3 generates a native stereo audio track alongside the video (saved as an mp4 with sound).</summary>
    public override bool HasAudio => true;
    /// <summary>H3 VAE: valid clip length = 17n+5 (mirrors the node's length step=17, min=5).</summary>
    public override FrameRule? FrameRule => new(5, 17);
    public override IReadOnlyList<ParamSpec> Schema => EditWorkflowBase.SharedSchema.Concat(H3.ExtraSchema).ToArray();

    protected override ComfyWorkflowGraph Build(MiniMaxH3I2VParams p, ResolvedRequirements req, WorkflowInputs inputs)
        => H3.Build(req, inputs, i2v: true, p.AudioVae, p.Length, p.Fps, ComfyGraph.Seed(p.Seed), p.Steps, p.Sampler, p.Scheduler, t2vDims: null);
}
