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
        new() { Key = "audio_vae", Type = ParamType.String, IsModelRef = true, Label = "Audio VAE" },
    };

    /// <summary>The shared T2V/I2V graph. <paramref name="i2v"/>: the source image is the first frame and the clip
    /// size derives from it (scaled to H3's ~1 MP budget); otherwise the size comes from the aspect map. The graph is
    /// otherwise identical — one H3 node, one distilled sampler chain, dual (video+audio) decode, one mp4-with-audio.</summary>
    public static Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs, bool i2v)
    {
        var wf = new Dictionary<string, object>();

        // Loaders. Diffusion via DiffusionLoader → plain UNETLoader (int8 ConvRot loads natively, weight_dtype default
        // keeps its INT8). Qwen3-VL text encoder through CLIPLoader type "minimax". TWO VAEs: video (frames) and audio
        // (the native stereo track); the audio VAE is the audio_vae model-ref slot.
        wf["4"] = ComfyGraph.DiffusionLoader(req.RequiredCheckpoint());   // H3 sets no weight_dtype → AutoWeightDtype (native INT8 ConvRot)
        object model = ComfyGraph.Ref("4", 0);
        wf["20"] = ComfyGraph.Node("CLIPLoader", new { clip_name = req.TextEncoder(0), type = "minimax", device = "default" });
        object clip = ComfyGraph.Ref("20", 0);
        wf["21"] = ComfyGraph.Node("VAELoader", new { vae_name = req.RequiredVae() });
        object videoVae = ComfyGraph.Ref("21", 0);
        wf["22"] = ComfyGraph.Node("VAELoader", new { vae_name = p.Model("audio_vae") });
        object audioVae = ComfyGraph.Ref("22", 0);

        int len = p.IntReq("length");   // frames; the default (124 = 17*7+5 ≈ 5s @ 24fps) lives in the config JSON, not here
        double fps = p.DblReq("fps");

        // The single H3 conditioning+latent node. It encodes the prompt itself and emits (positive CONDITIONING, LATENT).
        var h3 = new Dictionary<string, object>
        {
            ["clip"] = clip,
            ["vae"] = videoVae,
            ["prompt"] = inputs.Positive,
            ["length"] = len,
        };
        if (i2v)
        {
            // Source = first frame. Scale to H3's ~1 MP budget (multiple of 32) and use those dims as the clip size, so
            // the clip keeps the source's aspect inside H3's canvas. An optional END frame pins the last frame.
            wf["10"] = ComfyGraph.Node("LoadImage", new { image = inputs.SourceImageName ?? throw new RenderValidationException("MiniMax-H3 image→video needs a source image (the first frame), but none was provided.") });
            wf["11"] = ComfyGraph.Node("ImageScaleToTotalPixels", new { image = ComfyGraph.Ref("10", 0), upscale_method = "lanczos", megapixels = 1.0, resolution_steps = 32 });
            wf["15"] = ComfyGraph.Node("GetImageSize", new { image = ComfyGraph.Ref("11", 0) });
            h3["width"] = ComfyGraph.Ref("15", 0);
            h3["height"] = ComfyGraph.Ref("15", 1);
            h3["first_frame"] = ComfyGraph.Ref("11", 0);
            if (!string.IsNullOrEmpty(inputs.EndImageName))
            {
                wf["12"] = ComfyGraph.Node("LoadImage", new { image = inputs.EndImageName });
                h3["last_frame"] = ComfyGraph.Ref("12", 0);
            }
        }
        else
        {
            var (w, h) = p.DimsReq("aspect", ComfyGraph.NormalizeAspect(inputs.Aspect));
            h3["width"] = w;
            h3["height"] = h;
        }
        wf["14"] = ComfyGraph.Node("MiniMaxH3ImageToVideo", h3);
        object positive = ComfyGraph.Ref("14", 0), latent = ComfyGraph.Ref("14", 1);

        // Distilled sampling: BasicGuider (no CFG, no negative) + a res_multistep SamplerCustomAdvanced chain.
        wf["55"] = ComfyGraph.Node("BasicScheduler", new { model, scheduler = ComfyGraph.MapScheduler(p.StrReq("scheduler")), steps = p.IntReq("steps"), denoise = 1.0 });
        wf["56"] = ComfyGraph.Node("KSamplerSelect", new { sampler_name = ComfyGraph.MapSampler(p.StrReq("sampler")) });
        wf["57"] = ComfyGraph.Node("RandomNoise", new { noise_seed = ComfyGraph.Seed(p) });
        wf["58"] = ComfyGraph.Node("BasicGuider", new { model, conditioning = positive });
        wf["3"] = ComfyGraph.Node("SamplerCustomAdvanced", new { noise = ComfyGraph.Ref("57", 0), guider = ComfyGraph.Ref("58", 0), sampler = ComfyGraph.Ref("56", 0), sigmas = ComfyGraph.Ref("55", 0), latent_image = latent });

        // Dual decode → one mp4 with audio. The SAME latent decodes to frames (video VAE) and to the native stereo
        // track (audio VAE); CreateVideo muxes them; SaveVideo writes a real mp4 (format/codec auto = h264/aac).
        wf["8"] = ComfyGraph.Node("VAEDecode", new { samples = ComfyGraph.Ref("3", 0), vae = videoVae });
        wf["40"] = ComfyGraph.Node("VAEDecodeAudio", new { samples = ComfyGraph.Ref("3", 0), vae = audioVae });
        wf["41"] = ComfyGraph.Node("CreateVideo", new { images = ComfyGraph.Ref("8", 0), fps, audio = ComfyGraph.Ref("40", 0) });
        wf["9"] = ComfyGraph.Node("SaveVideo", new { video = ComfyGraph.Ref("41", 0), filename_prefix = i2v ? "forgemcp_edit" : "forgemcp", format = "auto", codec = "auto" });
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
