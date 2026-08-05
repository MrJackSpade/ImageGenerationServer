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
    /// sites. The h3-dict STRING KEYS below are node-INPUT names, not node ids — those are left as-is.</summary>
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

    /// <summary>The MiniMaxH3ImageToVideo node's input-field names (the h3-dict keys). Values are the ComfyUI input
    /// names, preserved exactly so the emitted graph stays byte-identical.</summary>
    private static class Inputs
    {
        public const string Clip = "clip";
        public const string Vae = "vae";
        public const string Prompt = "prompt";
        public const string Length = "length";
        public const string Width = "width";
        public const string Height = "height";
        public const string FirstFrame = "first_frame";
        public const string LastFrame = "last_frame";
    }

    /// <summary>The shared T2V/I2V graph. <paramref name="i2v"/>: the source image is the first frame and the clip
    /// size derives from it (scaled to H3's ~1 MP budget); otherwise the size comes from the aspect map. The graph is
    /// otherwise identical — one H3 node, one distilled sampler chain, dual (video+audio) decode, one mp4-with-audio.</summary>
    public static Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs, bool i2v)
    {
        Dictionary<string, object> wf = new Dictionary<string, object>();

        // Loaders. Diffusion via DiffusionLoader → plain UNETLoader (int8 ConvRot loads natively, weight_dtype default
        // keeps its INT8). Qwen3-VL text encoder through CLIPLoader type "minimax". TWO VAEs: video (frames) and audio
        // (the native stereo track); the audio VAE is the audio_vae model-ref slot.
        wf[Nodes.Model] = ComfyGraph.DiffusionLoader(req.RequiredCheckpoint());   // H3 sets no weight_dtype → AutoWeightDtype (native INT8 ConvRot)
        object model = ComfyGraph.Ref(Nodes.Model, 0);
        wf[Nodes.Clip] = ComfyGraph.Node(ComfyNodeTypes.CLIPLoader, new { clip_name = req.TextEncoder(0), type = "minimax", device = "default" });
        object clip = ComfyGraph.Ref(Nodes.Clip, 0);
        wf[Nodes.VideoVae] = ComfyGraph.Node(ComfyNodeTypes.VAELoader, new { vae_name = req.RequiredVae() });
        object videoVae = ComfyGraph.Ref(Nodes.VideoVae, 0);
        wf[Nodes.AudioVae] = ComfyGraph.Node(ComfyNodeTypes.VAELoader, new { vae_name = p.Model(WorkflowParamKeys.AudioVae) });
        object audioVae = ComfyGraph.Ref(Nodes.AudioVae, 0);

        int len = p.IntReq(WorkflowParamKeys.Length);   // frames; the default (124 = 17*7+5 ≈ 5s @ 24fps) lives in the config JSON, not here
        double fps = p.DblReq(WorkflowParamKeys.Fps);

        // The single H3 conditioning+latent node. It encodes the prompt itself and emits (positive CONDITIONING, LATENT).
        Dictionary<string, object> h3 = new Dictionary<string, object>
        {
            [Inputs.Clip] = clip,
            [Inputs.Vae] = videoVae,
            [Inputs.Prompt] = inputs.Positive,
            [Inputs.Length] = len,
        };
        if (i2v)
        {
            // Source = first frame. Scale to H3's ~1 MP budget (multiple of 32) and use those dims as the clip size, so
            // the clip keeps the source's aspect inside H3's canvas. An optional END frame pins the last frame.
            wf[Nodes.Source] = ComfyGraph.Node(ComfyNodeTypes.LoadImage, new { image = inputs.SourceImageName ?? throw new RenderValidationException("MiniMax-H3 image→video needs a source image (the first frame), but none was provided.") });
            wf[Nodes.ScaledSource] = ComfyGraph.Node(ComfyNodeTypes.ImageScaleToTotalPixels, new { image = ComfyGraph.Ref(Nodes.Source, 0), upscale_method = "lanczos", megapixels = 1.0, resolution_steps = 32 });
            wf[Nodes.SourceSize] = ComfyGraph.Node(ComfyNodeTypes.GetImageSize, new { image = ComfyGraph.Ref(Nodes.ScaledSource, 0) });
            h3[Inputs.Width] = ComfyGraph.Ref(Nodes.SourceSize, 0);
            h3[Inputs.Height] = ComfyGraph.Ref(Nodes.SourceSize, 1);
            h3[Inputs.FirstFrame] = ComfyGraph.Ref(Nodes.ScaledSource, 0);
            if (!string.IsNullOrEmpty(inputs.EndImageName))
            {
                wf[Nodes.EndFrame] = ComfyGraph.Node(ComfyNodeTypes.LoadImage, new { image = inputs.EndImageName });
                h3[Inputs.LastFrame] = ComfyGraph.Ref(Nodes.EndFrame, 0);
            }
        }
        else
        {
            (int w, int h) = p.DimsReq(WorkflowParamKeys.Aspect, ComfyGraph.NormalizeAspect(inputs.Aspect));
            h3[Inputs.Width] = w;
            h3[Inputs.Height] = h;
        }
        wf[Nodes.Encode] = ComfyGraph.Node(ComfyNodeTypes.MiniMaxH3ImageToVideo, h3);
        object positive = ComfyGraph.Ref(Nodes.Encode, 0), latent = ComfyGraph.Ref(Nodes.Encode, 1);

        // Distilled sampling: BasicGuider (no CFG, no negative) + a res_multistep SamplerCustomAdvanced chain.
        wf[Nodes.Scheduler] = ComfyGraph.Node(ComfyNodeTypes.BasicScheduler, new { model, scheduler = ComfyGraph.MapScheduler(p.StrReq(WorkflowParamKeys.Scheduler)), steps = p.IntReq(WorkflowParamKeys.Steps), denoise = 1.0 });
        wf[Nodes.SamplerSelect] = ComfyGraph.Node(ComfyNodeTypes.KSamplerSelect, new { sampler_name = ComfyGraph.MapSampler(p.StrReq(WorkflowParamKeys.Sampler)) });
        wf[Nodes.Noise] = ComfyGraph.Node(ComfyNodeTypes.RandomNoise, new { noise_seed = ComfyGraph.Seed(p) });
        wf[Nodes.Guider] = ComfyGraph.Node(ComfyNodeTypes.BasicGuider, new { model, conditioning = positive });
        wf[Nodes.Sampler] = ComfyGraph.Node(ComfyNodeTypes.SamplerCustomAdvanced, new { noise = ComfyGraph.Ref(Nodes.Noise, 0), guider = ComfyGraph.Ref(Nodes.Guider, 0), sampler = ComfyGraph.Ref(Nodes.SamplerSelect, 0), sigmas = ComfyGraph.Ref(Nodes.Scheduler, 0), latent_image = latent });

        // Dual decode → one mp4 with audio. The SAME latent decodes to frames (video VAE) and to the native stereo
        // track (audio VAE); CreateVideo muxes them; SaveVideo writes a real mp4 (format/codec auto = h264/aac).
        wf[Nodes.VideoDecode] = ComfyGraph.Node(ComfyNodeTypes.VAEDecode, new { samples = ComfyGraph.Ref(Nodes.Sampler, 0), vae = videoVae });
        wf[Nodes.AudioDecode] = ComfyGraph.Node(ComfyNodeTypes.VAEDecodeAudio, new { samples = ComfyGraph.Ref(Nodes.Sampler, 0), vae = audioVae });
        wf[Nodes.CreateVideo] = ComfyGraph.Node(ComfyNodeTypes.CreateVideo, new { images = ComfyGraph.Ref(Nodes.VideoDecode, 0), fps, audio = ComfyGraph.Ref(Nodes.AudioDecode, 0) });
        wf[Nodes.Save] = ComfyGraph.Node(ComfyNodeTypes.SaveVideo, new { video = ComfyGraph.Ref(Nodes.CreateVideo, 0), filename_prefix = i2v ? "forgemcp_edit" : "forgemcp", format = "auto", codec = "auto" });
        return wf;
    }
}

/// <summary>MiniMax-H3 text→video (with native audio). The fl2va model, no source frame — <c>MiniMaxH3ImageToVideo</c>
/// conditions on the prompt alone.</summary>
public sealed class MiniMaxH3T2VWorkflow : Txt2ImgWorkflowBase
{
    public override string Name => "minimax-h3-t2v";
    public override WorkflowMedia Media => WorkflowMedia.Video;
    /// <summary>H3 generates a native stereo audio track alongside the video (saved as an mp4 with sound).</summary>
    public override bool HasAudio => true;
    /// <summary>H3 VAE: valid clip length = 17n+5 (mirrors the node's length step=17, min=5).</summary>
    public override FrameRule? FrameRule => new(5, 17);
    public override IReadOnlyList<ParamSpec> Schema => base.Schema.Concat(H3.ExtraSchema).ToArray();

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
        => H3.Build(p, req, inputs, i2v: false);
}

/// <summary>MiniMax-H3 image→video (with native audio). The source image is the first frame; an optional last frame
/// (<see cref="SupportsEndFrame"/>) pins the ending. Same fl2va model as the T2V sibling.</summary>
public sealed class MiniMaxH3I2VWorkflow : EditWorkflowBase
{
    public override string Name => "minimax-h3-i2v";
    public override WorkflowMedia Media => WorkflowMedia.Video;
    public override bool SupportsEndFrame => true;
    /// <summary>H3 generates a native stereo audio track alongside the video (saved as an mp4 with sound).</summary>
    public override bool HasAudio => true;
    /// <summary>H3 VAE: valid clip length = 17n+5 (mirrors the node's length step=17, min=5).</summary>
    public override FrameRule? FrameRule => new(5, 17);
    public override IReadOnlyList<ParamSpec> Schema => base.Schema.Concat(H3.ExtraSchema).ToArray();

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
        => H3.Build(p, req, inputs, i2v: true);
}
