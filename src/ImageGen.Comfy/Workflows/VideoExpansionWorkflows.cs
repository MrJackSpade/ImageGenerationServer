//TODO: CHECK FOR FALLBACKS
namespace ImageGen.Comfy;

/// <summary>
/// 24GB-tier VIDEO models the catalog lacked: the Wan 2.2 A14B MoE (two-expert high+low noise) for image→video and
/// text→video, and native text→video for HunyuanVideo 1.5 and the original HunyuanVideo. Each is its own graph
/// (none fit the existing single-model i2v/txt2img topologies). Wired from the official ComfyUI templates
/// (video_wan2_2_14B_{i2v,t2v}.json, video_hunyuan_video_1.5_720p_t2v.json, hunyuan_video_text_to_video.json).
/// The MoE classes load a SECOND expert from the <c>unet_low</c> param (literal filename); its requirement is linked
/// in the config's <c>extra</c> for presence-gating. All gate to 24GB via the config's min_vram_mb. Smoke-test live.
/// </summary>
file static class Vid
{
    /// <summary>The ComfyUI native Wan quality/motion negative (shared by the Wan templates).</summary>
    public const string WanNegative =
        "色调艳丽，过曝，静态，细节模糊不清，字幕，风格，作品，画作，画面，静止，整体发灰，最差质量，低质量，JPEG压缩残留，丑陋的，残缺的，多余的手指，画得不好的手部，画得不好的脸部，畸形的，毁容的，形态畸形的肢体，手指融合，静止不动的画面，杂乱的背景，三条腿，背景人很多，倒着走";

    /// <summary>Two-stage MoE sampling: high-noise expert for [0,boundary), low-noise for [boundary,end). Returns the
    /// final latent. The reference (Wan2.2 repo) guides the two experts at DIFFERENT scales for t2v (high 4.0, low
    /// 3.0), so each stage takes its own cfg: <c>cfg_high</c>/<c>cfg_low</c>, falling back to the shared <c>cfg</c>.
    /// The reference switches experts on a sigma threshold (i2v 0.900, t2v 0.875); <c>boundary</c> is that threshold
    /// pre-mapped to a step index for the config's fixed steps/shift/scheduler.</summary>
    public static object MoESample(Dictionary<string, object> wf, ParamValues p, object modelHigh, object modelLow,
        object positive, object negative, object latent, long seed)
    {
        int steps = p.Int("steps", 20);
        int boundary = p.Int("boundary") > 0 ? p.Int("boundary") : steps / 2;
        double cfg = p.Dbl("cfg", 3.5);
        double cfgHigh = p.Dbl("cfg_high") > 0 ? p.Dbl("cfg_high") : cfg;
        double cfgLow = p.Dbl("cfg_low") > 0 ? p.Dbl("cfg_low") : cfg;
        var sampler = ComfyGraph.MapSampler(p.Str("sampler"));
        var scheduler = ComfyGraph.MapScheduler(p.Str("scheduler"));
        wf["3"] = ComfyGraph.Node("KSamplerAdvanced", new
        {
            add_noise = "enable", noise_seed = seed, steps, cfg = cfgHigh, sampler_name = sampler, scheduler,
            start_at_step = 0, end_at_step = boundary, return_with_leftover_noise = "enable",
            model = modelHigh, positive, negative, latent_image = latent,
        });
        // refiner_steps: run the low-noise stage on its OWN schedule with exactly this many steps, leaving the
        // high-noise structure phase untouched — a draft (small N) then commits (large N) with byte-identical
        // motion, because both runs share the same stage-1 schedule/seed and hand off the same latent. The handoff
        // sits at t* = 1 - boundary/steps of the shared schedule; total2 = round(N/t*) puts the refiner's start
        // index on that same sigma (exact whenever N/t* is whole — every multiple of 5 at the 15/40 reference).
        // 0 = decode the handoff latent as-is (structure phase only); absent/negative = the legacy shared-schedule
        // tail (identical to refiner_steps = steps - boundary).
        int refinerSteps = p.Int("refiner_steps", -1);
        if (refinerSteps == 0) return ComfyGraph.Ref("3", 0);
        int steps2 = steps, start2 = boundary;
        if (refinerSteps > 0)
        {
            double tStar = 1.0 - (double)boundary / steps;
            steps2 = Math.Max(refinerSteps + 1, (int)Math.Round(refinerSteps / tStar));
            start2 = steps2 - refinerSteps;
        }
        wf["31"] = ComfyGraph.Node("KSamplerAdvanced", new
        {
            add_noise = "disable", noise_seed = seed, steps = steps2, cfg = cfgLow, sampler_name = sampler, scheduler,
            start_at_step = start2, end_at_step = 10000, return_with_leftover_noise = "disable",
            model = modelLow, positive, negative, latent_image = ComfyGraph.Ref("3", 0),
        });
        return ComfyGraph.Ref("31", 0);
    }

    /// <summary>Load a high+low expert pair, each through ModelSamplingSD3(shift). High file = req.Checkpoint, low = unet_low
    /// param. Both experts load through <see cref="ComfyGraph.DiffusionLoader"/>, which picks its node from the
    /// bound file.</summary>
    public static (object high, object low) LoadExperts(Dictionary<string, object> wf, ParamValues p, ResolvedRequirements req)
    {
        double shift = p.Dbl("shift", 5.0);
        object Expert(string unetName) => ComfyGraph.DiffusionLoader(unetName);
        wf["4"] = Expert(req.Checkpoint);
        wf["5"] = ComfyGraph.Node("ModelSamplingSD3", new { model = ComfyGraph.Ref("4", 0), shift });
        wf["41"] = Expert(p.Str("unet_low") ?? "");
        wf["51"] = ComfyGraph.Node("ModelSamplingSD3", new { model = ComfyGraph.Ref("41", 0), shift });
        return (ComfyGraph.Ref("5", 0), ComfyGraph.Ref("51", 0));
    }
}

/// <summary>Wan 2.2 I2V-A14B image→video (two-expert MoE). Source image is the first frame; output an animated WEBP.
/// WanImageToVideo emits the (pos,neg,latent) triple consumed by the two KSamplerAdvanced stages.</summary>
public sealed class WanA14bI2VWorkflow : EditWorkflowBase
{
    public override string Name => "wan22-i2v-a14b";
    public override WorkflowMedia Media => WorkflowMedia.Video;
    /// <summary>Supports an optional last frame (WanFirstLastFrameToVideo) — the source is the first frame.</summary>
    public override bool SupportsEndFrame => true;
    /// <summary>Wan VAE: 4× temporal compression → valid clip lengths are 4n+1 (mirrors the node's length step=4).</summary>
    public override FrameRule? FrameRule => new(1, 4);

    /// <summary>
    /// Adds four <c>pad_*_pct</c> params on top of the shared edit schema: how much whitespace to add on each side
    /// before animating, as a PERCENTAGE of the source dimension (L/R of the width, T/B of the height). When any is
    /// positive the source frame is composited onto a larger white canvas so the character can animate INTO whitespace
    /// beyond its original bounds (e.g. <c>pad_right_pct=200</c> → 3× width, character flush left, room to dash right;
    /// <c>pad_top_pct=100</c> → 2× height, character flush bottom, room to jump). The caller (SpritePipeline) drives
    /// these from its configurable padding presets. The padded frame is still scaled to the same total-pixel budget
    /// below, so the clip's resolution does NOT grow — the character just occupies less of the frame. See
    /// <see cref="PadGeom"/>. The <c>end_pad_*_pct</c> free overrides pad an END frame the same way.
    /// </summary>
    public override IReadOnlyList<ParamSpec> Schema => base.Schema.Concat(new ParamSpec[]
    {
        new() { Key = "pad_left_pct",   Type = ParamType.Int, Min = 0, Max = 2000, Step = 1, Default = 0, Label = "Pad left %",   Help = "Whitespace on the left, % of source width" },
        new() { Key = "pad_right_pct",  Type = ParamType.Int, Min = 0, Max = 2000, Step = 1, Default = 0, Label = "Pad right %",  Help = "Whitespace on the right, % of source width" },
        new() { Key = "pad_top_pct",    Type = ParamType.Int, Min = 0, Max = 2000, Step = 1, Default = 0, Label = "Pad top %",    Help = "Whitespace on top, % of source height" },
        new() { Key = "pad_bottom_pct", Type = ParamType.Int, Min = 0, Max = 2000, Step = 1, Default = 0, Label = "Pad bottom %", Help = "Whitespace on the bottom, % of source height" },
        // Draft/commit knob (see Vid.MoESample): motion is fixed by the structure phase; this only buys sharpness.
        new() { Key = "refiner_steps", Type = ParamType.Int, Min = 0, Max = 40, Step = 1, Label = "Refiner steps", Help = "Low = fast draft (same motion), high = sharp final; re-run the same seed to commit" },
        // The second MoE expert. A slot id, resolved to this machine's bound file — it was a literal
        // filename, which put a model reference outside the binding system where nobody could change it.
        new() { Key = "unet_low", Type = ParamType.String, IsModelRef = true, Label = "Low-noise expert" },
    }).ToArray();

    /// <summary>
    /// The padded-canvas geometry for the four side-percentages: the white canvas size and the (X,Y) offset the source
    /// is composited at. Each side adds <c>dim·pct/100</c> pixels; the canvas is the source plus those additions, and
    /// the source sits at the top-left additions (so the L/T whitespace pushes it toward the bottom-right). Null when
    /// every side is zero (no padding). Must stay in lockstep with SpritePipeline's <c>PaddingPreset.PaddedAspect</c>.
    /// </summary>
    private static (int W, int H, int X, int Y)? PadGeom(int pctL, int pctR, int pctT, int pctB, int sw, int sh)
    {
        pctL = Math.Max(0, pctL); pctR = Math.Max(0, pctR); pctT = Math.Max(0, pctT); pctB = Math.Max(0, pctB);
        if (pctL == 0 && pctR == 0 && pctT == 0 && pctB == 0) return null;   // no padding
        int addL = sw * pctL / 100, addR = sw * pctR / 100, addT = sh * pctT / 100, addB = sh * pctB / 100;
        return (sw + addL + addR, sh + addT + addB, addL, addT);
    }

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>();
        var (mh, ml) = Vid.LoadExperts(wf, p, req);
        wf["20"] = ComfyGraph.Node("CLIPLoader", new { clip_name = req.TextEncoders.ElementAtOrDefault(0) ?? "", type = p.Str("clip_type") ?? "wan", device = "default" });
        object clip = ComfyGraph.Ref("20", 0);
        wf["21"] = ComfyGraph.Node("VAELoader", new { vae_name = req.Vae ?? "" });
        object vae = ComfyGraph.Ref("21", 0);
        wf["10"] = ComfyGraph.Node("LoadImage", new { image = inputs.SourceImageName ?? "" });

        int len = p.Int("length") > 0 ? p.Int("length") : 81;
        double fps = p.Dbl("fps") > 0 ? p.Dbl("fps") : 16;
        double budgetMp = (p.Int("width") > 0 && p.Int("height") > 0) ? (p.Int("width") * (double)p.Int("height")) / 1_000_000.0 : 0.6;

        // Optional padding: expand the source canvas with whitespace before the budget scale, so the character has room
        // to move outside its original bounding box (each side a % of the source dim; see PadGeom). Composite the source
        // (alpha-respecting, onto white — same nodes FlattenOnWhite uses) at the offset for the whitespace. Source dims
        // come from the uploaded frame (inputs.SourceWidth/Height). When every pad_*_pct is 0 PadGeom returns null and
        // the graph is the original.
        object scaleSource = ComfyGraph.Ref("10", 0);
        if (inputs.SourceWidth > 0 && inputs.SourceHeight > 0
            && PadGeom(p.Int("pad_left_pct"), p.Int("pad_right_pct"), p.Int("pad_top_pct"), p.Int("pad_bottom_pct"),
                       inputs.SourceWidth, inputs.SourceHeight) is (int cw, int ch, int px, int py))
        {
            wf["71"] = ComfyGraph.Node("EmptyImage", new { width = cw, height = ch, batch_size = 1, color = 0xFFFFFF });
            wf["72"] = ComfyGraph.Node("InvertMask", new { mask = ComfyGraph.Ref("10", 1) });
            wf["73"] = ComfyGraph.Node("ImageCompositeMasked", new { destination = ComfyGraph.Ref("71", 0), source = ComfyGraph.Ref("10", 0), x = px, y = py, resize_source = false, mask = ComfyGraph.Ref("72", 0) });
            scaleSource = ComfyGraph.Ref("73", 0);
        }
        wf["11"] = ComfyGraph.Node("ImageScaleToTotalPixels", new { image = scaleSource, upscale_method = "lanczos", megapixels = budgetMp, resolution_steps = 16 });
        wf["15"] = ComfyGraph.Node("GetImageSize", new { image = ComfyGraph.Ref("11", 0) });
        wf["6"] = ComfyGraph.Node("CLIPTextEncode", new { text = inputs.Positive, clip });
        wf["7"] = ComfyGraph.Node("CLIPTextEncode", new { text = string.IsNullOrWhiteSpace(inputs.Negative) ? Vid.WanNegative : inputs.Negative, clip });
        // First/last-frame conditioning when the caller supplied an END frame (the source is the first frame): swap the
        // plain WanImageToVideo for WanFirstLastFrameToVideo, pinning both ends. The node resizes end_image to
        // width/height itself, so the raw LoadImage is fine. Without an end frame the graph is byte-identical to before.
        // Both nodes re-emit the same 3 outputs (positive, negative, latent), so the downstream sampler wiring is shared.
        if (!string.IsNullOrEmpty(inputs.EndImageName))
        {
            wf["12"] = ComfyGraph.Node("LoadImage", new { image = inputs.EndImageName });
            object endImage = ComfyGraph.Ref("12", 0);
            // Pad the END frame the SAME way as the first frame when asked (end_pad_*_pct), so both share one padded
            // canvas. Otherwise WanFirstLastFrameToVideo just stretches the raw end frame to the (padded) start size and
            // the pose lands in the wrong place — the clip never reaches it. Scale the end image to the source frame
            // size, then composite it into the same white canvas (PadGeom) at the offset. The save gate in the caller
            // guarantees the two frames share an aspect, so the scale here is proportional (no distortion).
            if (inputs.SourceWidth > 0 && inputs.SourceHeight > 0
                && PadGeom(p.Int("end_pad_left_pct"), p.Int("end_pad_right_pct"), p.Int("end_pad_top_pct"), p.Int("end_pad_bottom_pct"),
                           inputs.SourceWidth, inputs.SourceHeight) is (int ecw, int ech, int epx, int epy))
            {
                int sw = inputs.SourceWidth, sh = inputs.SourceHeight;
                wf["76"] = ComfyGraph.Node("ImageScale", new { image = ComfyGraph.Ref("12", 0), upscale_method = "lanczos", width = sw, height = sh, crop = "disabled" });
                wf["77"] = ComfyGraph.Node("EmptyImage", new { width = ecw, height = ech, batch_size = 1, color = 0xFFFFFF });
                wf["78"] = ComfyGraph.Node("ImageCompositeMasked", new { destination = ComfyGraph.Ref("77", 0), source = ComfyGraph.Ref("76", 0), x = epx, y = epy, resize_source = false });
                endImage = ComfyGraph.Ref("78", 0);
            }
            wf["14"] = ComfyGraph.Node("WanFirstLastFrameToVideo", new
            {
                positive = ComfyGraph.Ref("6", 0), negative = ComfyGraph.Ref("7", 0), vae,
                width = ComfyGraph.Ref("15", 0), height = ComfyGraph.Ref("15", 1), length = len, batch_size = 1,
                start_image = ComfyGraph.Ref("11", 0), end_image = endImage,
            });
        }
        else
        {
            // WanImageToVideo re-emits conditioning + the start latent (3 outputs: positive, negative, latent).
            wf["14"] = ComfyGraph.Node("WanImageToVideo", new
            {
                positive = ComfyGraph.Ref("6", 0), negative = ComfyGraph.Ref("7", 0), vae,
                width = ComfyGraph.Ref("15", 0), height = ComfyGraph.Ref("15", 1), length = len, batch_size = 1,
                start_image = ComfyGraph.Ref("11", 0),
            });
        }
        object pos = ComfyGraph.Ref("14", 0), neg = ComfyGraph.Ref("14", 1), lat = ComfyGraph.Ref("14", 2);
        object outLat = Vid.MoESample(wf, p, mh, ml, pos, neg, lat, ComfyGraph.Seed(p));
        wf["8"] = ComfyGraph.Node("VAEDecode", new { samples = outLat, vae });
        wf["9"] = ComfyGraph.Node("SaveAnimatedWEBP", new { images = ComfyGraph.Ref("8", 0), filename_prefix = "forgemcp_edit", fps, lossless = false, quality = 80, method = "default" });
        return wf;
    }
}

/// <summary>Wan 2.2 T2V-A14B text→video (two-expert MoE). No source image — an EmptyHunyuanLatentVideo seeds the
/// clip and the conditioning feeds the two KSamplerAdvanced stages directly.</summary>
public sealed class WanA14bT2VWorkflow : Txt2ImgWorkflowBase
{
    /// <inheritdoc/>
    public override IReadOnlyList<ParamSpec> Schema => base.Schema.Concat(new ParamSpec[]
    {
        // The second MoE expert. A slot id, resolved to this machine's bound file — it was a literal
        // filename, which put a model reference outside the binding system where nobody could change it.
        new() { Key = "unet_low", Type = ParamType.String, IsModelRef = true, Label = "Low-noise expert" },
    }).ToArray();

    public override string Name => "wan22-t2v-a14b";
    public override WorkflowMedia Media => WorkflowMedia.Video;

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>();
        var (mh, ml) = Vid.LoadExperts(wf, p, req);
        wf["20"] = ComfyGraph.Node("CLIPLoader", new { clip_name = req.TextEncoders.ElementAtOrDefault(0) ?? "", type = p.Str("clip_type") ?? "wan", device = "default" });
        object clip = ComfyGraph.Ref("20", 0);
        wf["21"] = ComfyGraph.Node("VAELoader", new { vae_name = req.Vae ?? "" });
        object vae = ComfyGraph.Ref("21", 0);

        int sw = p.Int("width", 640), sh = p.Int("height", 640);
        var (w, h) = p.Dims("aspect", ComfyGraph.NormalizeAspect(inputs.Aspect), sw, sh);
        int len = p.Int("length") > 0 ? p.Int("length") : 81;
        double fps = p.Dbl("fps") > 0 ? p.Dbl("fps") : 16;
        wf["6"] = ComfyGraph.Node("CLIPTextEncode", new { text = inputs.Positive, clip });
        wf["7"] = ComfyGraph.Node("CLIPTextEncode", new { text = string.IsNullOrWhiteSpace(inputs.Negative) ? Vid.WanNegative : inputs.Negative, clip });
        wf["14"] = ComfyGraph.Node("EmptyHunyuanLatentVideo", new { width = w, height = h, length = len, batch_size = 1 });
        object outLat = Vid.MoESample(wf, p, mh, ml, ComfyGraph.Ref("6", 0), ComfyGraph.Ref("7", 0), ComfyGraph.Ref("14", 0), ComfyGraph.Seed(p));
        wf["8"] = ComfyGraph.Node("VAEDecode", new { samples = outLat, vae });
        wf["9"] = ComfyGraph.Node("SaveAnimatedWEBP", new { images = ComfyGraph.Ref("8", 0), filename_prefix = "forgemcp", fps, lossless = false, quality = 80, method = "default" });
        return wf;
    }
}

/// <summary>HunyuanVideo 1.5 text→video (720p). UNETLoader + the Qwen2.5-VL/ByT5 DualCLIPLoader (type
/// "hunyuan_video_15") + ModelSamplingSD3 + EmptyHunyuanVideo15Latent + a CFGGuider/SamplerCustomAdvanced chain
/// (real CFG, negatives work). The text→video sibling of the 480p i2v editor already in the catalog.</summary>
public sealed class HunyuanVideo15T2VWorkflow : Txt2ImgWorkflowBase
{
    public override string Name => "hunyuanvideo15-t2v";
    public override WorkflowMedia Media => WorkflowMedia.Video;
    public override IReadOnlyList<ParamSpec> Schema => base.Schema.Concat(HunyuanSr.Schema).ToArray();

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>();
        var enc = req.TextEncoders;
        wf["4"] = ComfyGraph.DiffusionLoader(req.Checkpoint);
        wf["30"] = ComfyGraph.Node("ModelSamplingSD3", new { model = ComfyGraph.Ref("4", 0), shift = p.Dbl("shift", 7.0) });
        object model = ComfyGraph.Ref("30", 0);
        wf["20"] = ComfyGraph.Node("DualCLIPLoader", new { clip_name1 = enc.ElementAtOrDefault(0) ?? "", clip_name2 = enc.ElementAtOrDefault(1) ?? "", type = p.Str("clip_type") ?? "hunyuan_video_15", device = "default" });
        object clip = ComfyGraph.Ref("20", 0);
        wf["21"] = ComfyGraph.Node("VAELoader", new { vae_name = req.Vae ?? "" });
        object vae = ComfyGraph.Ref("21", 0);

        int sw = p.Int("width", 1280), sh = p.Int("height", 720);
        var (w, h) = p.Dims("aspect", ComfyGraph.NormalizeAspect(inputs.Aspect), sw, sh);
        int len = p.Int("length") > 0 ? p.Int("length") : 121;
        double fps = p.Dbl("fps") > 0 ? p.Dbl("fps") : 24;
        wf["6"] = ComfyGraph.Node("CLIPTextEncode", new { text = inputs.Positive, clip });
        wf["7"] = ComfyGraph.Node("CLIPTextEncode", new { text = inputs.Negative ?? "", clip });
        wf["14"] = ComfyGraph.Node("EmptyHunyuanVideo15Latent", new { width = w, height = h, length = len, batch_size = 1 });
        wf["55"] = ComfyGraph.Node("BasicScheduler", new { model, scheduler = ComfyGraph.MapScheduler(p.Str("scheduler")), steps = p.Int("steps", 20), denoise = 1.0 });
        wf["56"] = ComfyGraph.Node("KSamplerSelect", new { sampler_name = ComfyGraph.MapSampler(p.Str("sampler")) });
        wf["57"] = ComfyGraph.Node("RandomNoise", new { noise_seed = ComfyGraph.Seed(p) });
        wf["58"] = ComfyGraph.Node("CFGGuider", new { model, positive = ComfyGraph.Ref("6", 0), negative = ComfyGraph.Ref("7", 0), cfg = p.Dbl("cfg", 6.0) });
        wf["3"] = ComfyGraph.Node("SamplerCustomAdvanced", new { noise = ComfyGraph.Ref("57", 0), guider = ComfyGraph.Ref("58", 0), sampler = ComfyGraph.Ref("56", 0), sigmas = ComfyGraph.Ref("55", 0), latent_image = ComfyGraph.Ref("14", 0) });
        // Optional super-resolution second pass (1080p). t2v has no source image, so no start_image/CLIP-vision cues.
        object outLatent = HunyuanSr.Refine(wf, p, ComfyGraph.Ref("3", 0), ComfyGraph.Ref("6", 0), ComfyGraph.Ref("7", 0), vae, null, null, ComfyGraph.Seed(p));
        wf["8"] = HunyuanSr.Enabled(p)
            ? ComfyGraph.Node("VAEDecodeTiled", new { samples = outLatent, vae, tile_size = 256, overlap = 64, temporal_size = 64, temporal_overlap = 8 })
            : ComfyGraph.Node("VAEDecode", new { samples = outLatent, vae });
        wf["9"] = ComfyGraph.Node("SaveAnimatedWEBP", new { images = ComfyGraph.Ref("8", 0), filename_prefix = "forgemcp", fps, lossless = false, quality = 80, method = "default" });
        return wf;
    }
}

/// <summary>Original HunyuanVideo 13B text→video. The diffusion loader follows the bound file; the LLaVA-Llama3/CLIP-L DualCLIPLoader
/// (type "hunyuan_video") + ModelSamplingSD3 + embedded FluxGuidance + EmptyHunyuanLatentVideo + a
/// BasicGuider/SamplerCustomAdvanced chain (guidance-distilled: cfg 1, no negative). The t2v sibling of the i2v
/// GGUF editor already in the catalog.</summary>
public sealed class HunyuanVideoT2VWorkflow : Txt2ImgWorkflowBase
{
    public override string Name => "hunyuanvideo-t2v";
    public override WorkflowMedia Media => WorkflowMedia.Video;
    public override bool PromptDirectsMotion => true;

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>();
        var enc = req.TextEncoders;
        wf["4"] = ComfyGraph.DiffusionLoader(req.Checkpoint);
        wf["30"] = ComfyGraph.Node("ModelSamplingSD3", new { model = ComfyGraph.Ref("4", 0), shift = p.Dbl("shift", 7.0) });
        object model = ComfyGraph.Ref("30", 0);
        wf["20"] = ComfyGraph.Node("DualCLIPLoader", new { clip_name1 = enc.ElementAtOrDefault(0) ?? "", clip_name2 = enc.ElementAtOrDefault(1) ?? "", type = p.Str("clip_type") ?? "hunyuan_video", device = "default" });
        object clip = ComfyGraph.Ref("20", 0);
        wf["21"] = ComfyGraph.Node("VAELoader", new { vae_name = req.Vae ?? "" });
        object vae = ComfyGraph.Ref("21", 0);

        int sw = p.Int("width", 848), sh = p.Int("height", 480);
        var (w, h) = p.Dims("aspect", ComfyGraph.NormalizeAspect(inputs.Aspect), sw, sh);
        int len = p.Int("length") > 0 ? p.Int("length") : 73;
        double fps = p.Dbl("fps") > 0 ? p.Dbl("fps") : 24;
        wf["6"] = ComfyGraph.Node("CLIPTextEncode", new { text = inputs.Positive, clip });
        wf["12"] = ComfyGraph.Node("FluxGuidance", new { conditioning = ComfyGraph.Ref("6", 0), guidance = p.Dbl("guidance", 6.0) });
        wf["14"] = ComfyGraph.Node("EmptyHunyuanLatentVideo", new { width = w, height = h, length = len, batch_size = 1 });
        wf["55"] = ComfyGraph.Node("BasicScheduler", new { model, scheduler = ComfyGraph.MapScheduler(p.Str("scheduler")), steps = p.Int("steps", 20), denoise = 1.0 });
        wf["56"] = ComfyGraph.Node("KSamplerSelect", new { sampler_name = ComfyGraph.MapSampler(p.Str("sampler")) });
        wf["57"] = ComfyGraph.Node("RandomNoise", new { noise_seed = ComfyGraph.Seed(p) });
        wf["58"] = ComfyGraph.Node("BasicGuider", new { model, conditioning = ComfyGraph.Ref("12", 0) });
        wf["3"] = ComfyGraph.Node("SamplerCustomAdvanced", new { noise = ComfyGraph.Ref("57", 0), guider = ComfyGraph.Ref("58", 0), sampler = ComfyGraph.Ref("56", 0), sigmas = ComfyGraph.Ref("55", 0), latent_image = ComfyGraph.Ref("14", 0) });
        wf["8"] = ComfyGraph.Node("VAEDecodeTiled", new { samples = ComfyGraph.Ref("3", 0), vae, tile_size = 256, overlap = 64, temporal_size = 64, temporal_overlap = 8 });
        wf["9"] = ComfyGraph.Node("SaveAnimatedWEBP", new { images = ComfyGraph.Ref("8", 0), filename_prefix = "forgemcp", fps, lossless = false, quality = 80, method = "default" });
        return wf;
    }
}
