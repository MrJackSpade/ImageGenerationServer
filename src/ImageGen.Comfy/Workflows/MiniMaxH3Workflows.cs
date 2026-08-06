using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;
using ImageGen.Domain.CodeAnalysis;

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
/// <summary>Which H3 task the shared graph builds: text→video (no source), image→video (source is the first frame),
/// or reference→video (source + picker images condition the subject/identity, never a first frame).</summary>
file enum H3Mode { T2V, I2V, Ref2V }

file static class H3
{
    /// <summary>The audio VAE — a SECOND vae slot beyond the video VAE (<c>req.Vae</c>). A model-ref param resolved to
    /// this machine's bound file (linked in the config's <c>extra</c>), mirroring how the MoE/SR workflows carry a
    /// second model file.</summary>
    public static readonly ParamSpec[] ExtraSchema = new ParamSpec[]
    {
        new() { Key = WorkflowParamKeys.AudioVae, Type = ParamType.String, IsModelRef = true, Label = "Audio VAE" },
    };

    /// <summary>First id for the per-picker-reference LoadImage nodes in ref2v (the source is ref_image_0, in-place
    /// at <see cref="H3Nodes.Source"/>); each picker reference gets <c>RefImageBase + i</c>. Kept clear of every node id
    /// in <see cref="H3Nodes"/>. A const int (not a node-id string), so it stays out of the pure <see cref="H3Nodes"/>
    /// holder.</summary>
    private const int RefImageBase = 60;

    /// <summary>The shared T2V/I2V graph (typed #93). <paramref name="i2v"/>: the source image is the first frame and
    /// the clip size derives from it (scaled to H3's ~1 MP budget); otherwise the size is the aspect map's
    /// <paramref name="t2vDims"/>. The graph is otherwise identical — one H3 node, one distilled sampler chain, dual
    /// (video+audio) decode, one mp4-with-audio. The scalar knobs are read TYPED off each workflow's params record and
    /// passed in; <paramref name="seed"/> is already resolved (<c>ComfyGraph.Seed</c>) and
    /// <paramref name="sampler"/>/<paramref name="scheduler"/> are the RAW Forge names (mapped here).</summary>
    public static ComfyWorkflowGraph Build(ResolvedRequirements req, WorkflowInputs inputs, H3Mode mode,
        string audioVae, int length, double fps, long seed, int steps, string sampler, string scheduler, (int w, int h)? t2vDims, int refMax = 0)
    {
        ComfyWorkflowGraph g = new ComfyWorkflowGraph();

        // Loaders. Diffusion via DiffusionLoaderNode → plain UNETLoader (int8 ConvRot loads natively, weight_dtype
        // default keeps its INT8). Qwen3-VL text encoder through CLIPLoader type "minimax". TWO VAEs: video (frames)
        // and audio (the native stereo track); the audio VAE is the audio_vae model-ref slot.
        g[H3Nodes.Model] = ComfyGraph.DiffusionLoaderNode(req.RequiredCheckpoint());   // H3 sets no weight_dtype → AutoWeightDtype (native INT8 ConvRot)
        Output<Slot.Model> model = UNETLoader.ModelOut(H3Nodes.Model);
        g[H3Nodes.Clip] = new CLIPLoader { ClipName = req.TextEncoder(0), Type = ComfyWidgets.ClipType.Minimax, Device = ComfyWidgets.Device.Default };
        Output<Slot.Clip> clip = CLIPLoader.ClipOut(H3Nodes.Clip);
        g[H3Nodes.VideoVae] = new VAELoader { VaeName = req.RequiredVae() };
        Output<Slot.Vae> videoVae = VAELoader.VaeOut(H3Nodes.VideoVae);
        g[H3Nodes.AudioVae] = new VAELoader { VaeName = audioVae };
        Output<Slot.Vae> audioVaeRef = VAELoader.VaeOut(H3Nodes.AudioVae);

        // The single H3 conditioning+latent node. It encodes the prompt itself and emits (positive CONDITIONING, LATENT).
        switch (mode)
        {
            case H3Mode.I2V:
            {
                // Source = first frame. Scale to H3's ~1 MP budget (multiple of 32) and use those dims as the clip size, so
                // the clip keeps the source's aspect inside H3's canvas. An optional END frame pins the last frame.
                g[H3Nodes.Source] = new LoadImage { Image = inputs.SourceImageName ?? throw new RenderValidationException("MiniMax-H3 image→video needs a source image (the first frame), but none was provided.") };
                g[H3Nodes.ScaledSource] = new ImageScaleToTotalPixels { Image = LoadImage.ImageOut(H3Nodes.Source), UpscaleMethod = ComfyWidgets.Upscale.Lanczos, Megapixels = 1.0, ResolutionSteps = 32 };
                g[H3Nodes.SourceSize] = new GetImageSize { Image = ImageScaleToTotalPixels.Out(H3Nodes.ScaledSource) };
                Output<Slot.Image>? lastFrame = null;
                if (!string.IsNullOrEmpty(inputs.EndImageName))
                {
                    // Scale the end frame through the SAME node as the first frame (:99), so both reach
                    // MiniMaxH3ImageToVideo at identical dims. The node resizes first_frame to width/height with
                    // crop="disabled" and last_frame with crop="center"; the first frame arrives already at those
                    // exact dims (a no-op), so a raw end frame gets a lone cover/center-crop and lands at a different
                    // framing. A same-image loop (#110) then stretches instead of holding still. Pre-scaling the end
                    // frame identically makes the node's last-frame resize a no-op too → a clean static loop.
                    g[H3Nodes.EndFrame] = new LoadImage { Image = inputs.EndImageName };
                    g[H3Nodes.ScaledEndFrame] = new ImageScaleToTotalPixels { Image = LoadImage.ImageOut(H3Nodes.EndFrame), UpscaleMethod = ComfyWidgets.Upscale.Lanczos, Megapixels = 1.0, ResolutionSteps = 32 };
                    lastFrame = ImageScaleToTotalPixels.Out(H3Nodes.ScaledEndFrame);
                }
                // First/last-frame loop vs plain i2v is a choice of NODE, not a nullable input: the end frame either pins
                // the ending (its own record) or there is none.
                g[H3Nodes.Encode] = lastFrame is { } endFrame
                    ? new MiniMaxH3FirstLastFrameToVideo
                    {
                        Clip = clip,
                        Vae = videoVae,
                        Prompt = inputs.Positive,
                        Length = length,
                        Width = GetImageSize.WidthOut(H3Nodes.SourceSize),
                        Height = GetImageSize.HeightOut(H3Nodes.SourceSize),
                        FirstFrame = ImageScaleToTotalPixels.Out(H3Nodes.ScaledSource),
                        LastFrame = endFrame,
                    }
                    : new MiniMaxH3ImageToVideoI2V
                    {
                        Clip = clip,
                        Vae = videoVae,
                        Prompt = inputs.Positive,
                        Length = length,
                        Width = GetImageSize.WidthOut(H3Nodes.SourceSize),
                        Height = GetImageSize.HeightOut(H3Nodes.SourceSize),
                        FirstFrame = ImageScaleToTotalPixels.Out(H3Nodes.ScaledSource),
                    };
                break;
            }
            case H3Mode.Ref2V:
            {
                // Reference→video: the open image is the PRIMARY subject reference (ref_image_0) and sets the output
                // canvas (scaled to H3's ~1 MP budget, exactly like i2v); any picker references follow as
                // ref_image_1…N. They condition the subject/identity — NOT a first frame — so they enter the ref node's
                // autogrow ref_images input, which resizes each internally (down only). The audio VAE is a direct input.
                g[H3Nodes.Source] = new LoadImage { Image = inputs.SourceImageName ?? throw new RenderValidationException("MiniMax-H3 reference→video needs a source image (the primary subject reference), but none was provided.") };
                g[H3Nodes.ScaledSource] = new ImageScaleToTotalPixels { Image = LoadImage.ImageOut(H3Nodes.Source), UpscaleMethod = ComfyWidgets.Upscale.Lanczos, Megapixels = 1.0, ResolutionSteps = 32 };
                g[H3Nodes.SourceSize] = new GetImageSize { Image = ImageScaleToTotalPixels.Out(H3Nodes.ScaledSource) };

                IReadOnlyList<string> refNames = inputs.ReferenceImageNames;
                if (refNames.Count > refMax)
                    throw new RenderValidationException($"This configuration accepts at most {refMax} reference image(s); got {refNames.Count}.");
                List<Output<Slot.Image>> refs = new(refNames.Count + 1) { LoadImage.ImageOut(H3Nodes.Source) };
                for (int i = 0; i < refNames.Count; i++)
                {
                    string id = (RefImageBase + i).ToString();
                    g[id] = new LoadImage { Image = refNames[i] };
                    refs.Add(LoadImage.ImageOut(id));
                }
                g[H3Nodes.Encode] = new MiniMaxH3ReferenceToVideo
                {
                    Clip = clip,
                    Vae = videoVae,
                    AudioVae = audioVaeRef,
                    Prompt = inputs.Positive,
                    Length = length,
                    Width = GetImageSize.WidthOut(H3Nodes.SourceSize),
                    Height = GetImageSize.HeightOut(H3Nodes.SourceSize),
                    RefImageSize = ComfyWidgets.RefImageSize.Match,
                    RefImages = MiniMaxH3ReferenceToVideo.Refs(refs),
                };
                break;
            }
            default:
            {
                (int w, int h) = t2vDims ?? throw new RenderValidationException("MiniMax-H3 text→video needs a render size, but none was resolved.");
                g[H3Nodes.Encode] = new MiniMaxH3ImageToVideoT2V { Clip = clip, Vae = videoVae, Prompt = inputs.Positive, Length = length, Width = w, Height = h };
                break;
            }
        }
        Output<Slot.Conditioning> positive = new(H3Nodes.Encode, 0);
        Output<Slot.Latent> latent = new(H3Nodes.Encode, 1);

        // Distilled sampling: BasicGuider (no CFG, no negative) + a res_multistep SamplerCustomAdvanced chain.
        g[H3Nodes.Scheduler] = new BasicScheduler { Model = model, Scheduler = ComfyGraph.MapScheduler(scheduler), Steps = steps, Denoise = 1.0 };
        g[H3Nodes.SamplerSelect] = new KSamplerSelect { SamplerName = ComfyGraph.MapSampler(sampler) };
        g[H3Nodes.Noise] = new RandomNoise { NoiseSeed = seed };
        g[H3Nodes.Guider] = new BasicGuider { Model = model, Conditioning = positive };
        g[H3Nodes.Sampler] = new SamplerCustomAdvanced { Noise = RandomNoise.Out(H3Nodes.Noise), Guider = BasicGuider.Out(H3Nodes.Guider), Sampler = KSamplerSelect.Out(H3Nodes.SamplerSelect), Sigmas = BasicScheduler.Out(H3Nodes.Scheduler), LatentImage = latent };

        // Dual decode → one mp4 with audio. The SAME latent decodes to frames (video VAE) and to the native stereo
        // track (audio VAE); CreateVideo muxes them; SaveVideo writes a real mp4 (format/codec auto = h264/aac).
        g[H3Nodes.VideoDecode] = new VAEDecode { Samples = SamplerCustomAdvanced.Out(H3Nodes.Sampler), Vae = videoVae };
        g[H3Nodes.AudioDecode] = new VAEDecodeAudio { Samples = SamplerCustomAdvanced.Out(H3Nodes.Sampler), Vae = audioVaeRef };
        g[H3Nodes.CreateVideo] = new CreateVideo { Images = VAEDecode.Out(H3Nodes.VideoDecode), Fps = fps, Audio = VAEDecodeAudio.Out(H3Nodes.AudioDecode) };
        g[H3Nodes.Save] = new SaveVideo { Video = CreateVideo.Out(H3Nodes.CreateVideo), FilenamePrefix = mode == H3Mode.T2V ? OutputPrefixes.Generate : OutputPrefixes.Edit, Format = ComfyWidgets.SaveFormat.Auto, Codec = ComfyWidgets.VideoCodec.Auto };
        return g;
    }
}

/// <summary>The shared T2V/I2V graph's node ids, named by role. The VALUE is the graph-local node key (preserved
/// exactly, so the emitted graph stays byte-identical); the NAME replaces the bare numeric literals at the use
/// sites.</summary>
file static class H3Nodes
{
    public const string Model = "4";
    public const string Clip = "20";
    public const string VideoVae = "21";
    public const string AudioVae = "22";
    public const string Source = "10";
    public const string ScaledSource = "11";
    public const string SourceSize = "15";
    public const string EndFrame = "12";
    public const string ScaledEndFrame = "13";
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
    [JsonPropertyName(WorkflowParamKeys.Steps)]
    [Range(ParamBounds.StepsMin, ParamBounds.StepsMax)] public required int Steps { get; init; }
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
        => H3.Build(req, inputs, H3Mode.T2V, p.AudioVae, p.Length, p.Fps, ComfyGraph.Seed(p.Seed), p.Steps, p.Sampler, p.Scheduler,
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
        => H3.Build(req, inputs, H3Mode.I2V, p.AudioVae, p.Length, p.Fps, ComfyGraph.Seed(p.Seed), p.Steps, p.Sampler, p.Scheduler, t2vDims: null);
}

/// <summary>MiniMax-H3 reference→video parameters — the same native-audio + sampler knobs as i2v, plus the
/// <c>reference_max</c> cap (nullable: absent → no picker references beyond the source). The audio VAE (resolved model
/// ref), clip <c>length</c>, playback <c>fps</c> and sampler settings are <c>required</c>; <c>seed</c> is the app's
/// single-sourced seed (defaulted).</summary>
public sealed record MiniMaxH3Ref2VParams
{
    [JsonPropertyName(WorkflowParamKeys.AudioVae)]     public required string AudioVae { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Length)]       public required int Length { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Fps)]          public required double Fps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Steps)]
    [Range(ParamBounds.StepsMin, ParamBounds.StepsMax)] public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Sampler)]      public required string Sampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Scheduler)]    public required string Scheduler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ReferenceMax)] [AllowNullable("null = the config didn't set reference_max; absent means no picker references beyond the source (treated as 0), distinct from a config that explicitly caps at a real 0")] public int? ReferenceMax { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)]         public long Seed { get; init; }
}

/// <summary>MiniMax-H3 reference→video (ref2va, with native audio). The open image is the primary subject reference and
/// the edit page's ＋ ref picker (<c>reference.max &gt; 0</c>) adds more; unlike i2v NONE of them is a first frame — they
/// condition the subject/identity through the <see cref="MiniMaxH3ReferenceToVideo"/> node. Same fl2va model, text
/// encoder and dual VAEs as the T2V/I2V siblings — no new weights. Buckets into the Animate section (<c>media:video</c>).</summary>
public sealed class MiniMaxH3Ref2VWorkflow : EditWorkflow<MiniMaxH3Ref2VParams>
{
    public override string Name => "minimax-h3-ref2v";
    public override WorkflowMedia Media => WorkflowMedia.Video;
    /// <summary>H3 generates a native stereo audio track alongside the video (saved as an mp4 with sound).</summary>
    public override bool HasAudio => true;
    /// <summary>H3 VAE: valid clip length = 17n+5 (mirrors the node's length step=17, min=5).</summary>
    public override FrameRule? FrameRule => new(5, 17);
    public override IReadOnlyList<ParamSpec> Schema => EditWorkflowBase.SharedSchema.Concat(H3.ExtraSchema).ToArray();

    protected override ComfyWorkflowGraph Build(MiniMaxH3Ref2VParams p, ResolvedRequirements req, WorkflowInputs inputs)
        => H3.Build(req, inputs, H3Mode.Ref2V, p.AudioVae, p.Length, p.Fps, ComfyGraph.Seed(p.Seed), p.Steps, p.Sampler, p.Scheduler, t2vDims: null, refMax: p.ReferenceMax ?? 0);
}
