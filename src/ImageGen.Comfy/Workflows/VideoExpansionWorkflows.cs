using ImageGen.Application.Rendering;
using ImageGen.Domain;

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
    /// <summary>The MoE helper's node ids, named by role. The VALUE is the graph-local node key (preserved exactly, so
    /// the emitted graph stays byte-identical); the NAME replaces the bare numeric literals at the use sites.</summary>
    private static class Nodes
    {
        public const string HighExpert = "4";
        public const string HighSampling = "5";
        public const string LowExpert = "41";
        public const string LowSampling = "51";
        public const string HighSampler = "3";
        public const string LowSampler = "31";
    }

    /// <summary>Two-stage MoE sampling: high-noise expert for [0,boundary), low-noise for [boundary,end). Returns the
    /// final latent. The reference (Wan2.2 repo) guides the two experts at DIFFERENT scales for t2v (high 4.0, low
    /// 3.0), so each stage takes its own cfg: <c>cfg_high</c>/<c>cfg_low</c>. The reference switches experts on a sigma
    /// threshold (i2v 0.900, t2v 0.875); <c>boundary</c> is that threshold pre-mapped to a step index for the config's
    /// fixed steps/shift/scheduler.</summary>
    public static object MoESample(Dictionary<string, object> wf, ParamValues p, object modelHigh, object modelLow,
        object positive, object negative, object latent, long seed)
    {
        int steps = p.IntReq(WorkflowParamKeys.Steps);
        int boundary = p.IntReq(WorkflowParamKeys.Boundary);
        double cfgHigh = p.DblReq(WorkflowParamKeys.CfgHigh);
        double cfgLow = p.DblReq(WorkflowParamKeys.CfgLow);
        string sampler = ComfyGraph.MapSampler(p.StrReq(WorkflowParamKeys.Sampler));
        string scheduler = ComfyGraph.MapScheduler(p.StrReq(WorkflowParamKeys.Scheduler));
        wf[Nodes.HighSampler] = ComfyGraph.Node(ComfyNodeTypes.KSamplerAdvanced, new
        {
            add_noise = "enable",
            noise_seed = seed,
            steps,
            cfg = cfgHigh,
            sampler_name = sampler,
            scheduler,
            start_at_step = 0,
            end_at_step = boundary,
            return_with_leftover_noise = "enable",
            model = modelHigh,
            positive,
            negative,
            latent_image = latent,
        });
        // refiner_steps: run the low-noise stage on its OWN schedule with exactly this many steps, leaving the
        // high-noise structure phase untouched — a draft (small N) then commits (large N) with byte-identical
        // motion, because both runs share the same stage-1 schedule/seed and hand off the same latent. The handoff
        // sits at t* = 1 - boundary/steps of the shared schedule; total2 = round(N/t*) puts the refiner's start
        // index on that same sigma (exact whenever N/t* is whole — every multiple of 5 at the 15/40 reference).
        // 0 = decode the handoff latent as-is (structure phase only); absent/negative = the legacy shared-schedule
        // tail (identical to refiner_steps = steps - boundary).
        int refinerSteps = p.Has(WorkflowParamKeys.RefinerSteps) ? p.IntReq(WorkflowParamKeys.RefinerSteps) : -1;
        if (refinerSteps == 0) return ComfyGraph.Ref(Nodes.HighSampler, 0);
        int steps2 = steps, start2 = boundary;
        if (refinerSteps > 0)
        {
            double tStar = 1.0 - (double)boundary / steps;
            steps2 = Math.Max(refinerSteps + 1, (int)Math.Round(refinerSteps / tStar));
            start2 = steps2 - refinerSteps;
        }
        wf[Nodes.LowSampler] = ComfyGraph.Node(ComfyNodeTypes.KSamplerAdvanced, new
        {
            add_noise = "disable",
            noise_seed = seed,
            steps = steps2,
            cfg = cfgLow,
            sampler_name = sampler,
            scheduler,
            start_at_step = start2,
            end_at_step = 10000,
            return_with_leftover_noise = "disable",
            model = modelLow,
            positive,
            negative,
            latent_image = ComfyGraph.Ref(Nodes.HighSampler, 0),
        });
        return ComfyGraph.Ref(Nodes.LowSampler, 0);
    }

    /// <summary>Load a high+low expert pair, each through ModelSamplingSD3(shift). High file = req.Checkpoint, low = unet_low
    /// param. Both experts load through <see cref="ComfyGraph.DiffusionLoader"/>, which picks its node from the
    /// bound file.</summary>
    public static (object high, object low) LoadExperts(Dictionary<string, object> wf, ParamValues p, ResolvedRequirements req)
    {
        double shift = p.DblReq(WorkflowParamKeys.Shift);
        object Expert(string unetName) => ComfyGraph.DiffusionLoader(unetName);
        wf[Nodes.HighExpert] = Expert(req.RequiredCheckpoint());
        wf[Nodes.HighSampling] = ComfyGraph.Node(ComfyNodeTypes.ModelSamplingSD3, new { model = ComfyGraph.Ref(Nodes.HighExpert, 0), shift });
        wf[Nodes.LowExpert] = Expert(p.Model(WorkflowParamKeys.UnetLow));
        wf[Nodes.LowSampling] = ComfyGraph.Node(ComfyNodeTypes.ModelSamplingSD3, new { model = ComfyGraph.Ref(Nodes.LowExpert, 0), shift });
        return (ComfyGraph.Ref(Nodes.HighSampling, 0), ComfyGraph.Ref(Nodes.LowSampling, 0));
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
        new() { Key = WorkflowParamKeys.PadLeftPct,   Type = ParamType.Int, Min = 0, Max = 2000, Step = 1, Label = "Pad left %",   Help = "Whitespace on the left, % of source width" },
        new() { Key = WorkflowParamKeys.PadRightPct,  Type = ParamType.Int, Min = 0, Max = 2000, Step = 1, Label = "Pad right %",  Help = "Whitespace on the right, % of source width" },
        new() { Key = WorkflowParamKeys.PadTopPct,    Type = ParamType.Int, Min = 0, Max = 2000, Step = 1, Label = "Pad top %",    Help = "Whitespace on top, % of source height" },
        new() { Key = WorkflowParamKeys.PadBottomPct, Type = ParamType.Int, Min = 0, Max = 2000, Step = 1, Label = "Pad bottom %", Help = "Whitespace on the bottom, % of source height" },
        // Draft/commit knob (see Vid.MoESample): motion is fixed by the structure phase; this only buys sharpness.
        new() { Key = WorkflowParamKeys.RefinerSteps, Type = ParamType.Int, Min = 0, Max = 40, Step = 1, Label = "Refiner steps", Help = "Low = fast draft (same motion), high = sharp final; re-run the same seed to commit" },
        // The second MoE expert. A slot id, resolved to this machine's bound file — a literal filename would
        // put a model reference outside the binding system where nobody could change it.
        new() { Key = WorkflowParamKeys.UnetLow, Type = ParamType.String, IsModelRef = true, Label = "Low-noise expert" },
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

    /// <summary>This workflow's own node ids, named by role; the MoE experts + samplers ("4"/"5"/"41"/"51"/"3"/"31") are
    /// written by Vid, and Nodes.Source ("10") is the inherited edit-head source-image role reused here.</summary>
    private const string Clip = "20";
    private const string Vae = "21";
    private const string Positive = "6";
    private const string Negative = "7";
    private const string ScaledSource = "11";
    private const string SourceSize = "15";
    private const string EndFrame = "12";
    private const string Cond = "14";
    private const string Decode = "8";
    private const string Save = "9";
    private const string PadCanvas = "71";
    private const string PadMask = "72";
    private const string PadComposite = "73";
    private const string EndScale = "76";
    private const string EndPadCanvas = "77";
    private const string EndPadComposite = "78";

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        Dictionary<string, object> wf = new Dictionary<string, object>();
        int Pct(string k) => p.Has(k) ? p.IntReq(k) : 0;   // per-side pad %, absent = 0 (no pad on that side)
        (object? mh, object? ml) = Vid.LoadExperts(wf, p, req);
        wf[Clip] = ComfyGraph.Node(ComfyNodeTypes.CLIPLoader, new { clip_name = req.TextEncoder(0), type = "wan", device = "default" });
        object clip = ComfyGraph.Ref(Clip, 0);
        wf[Vae] = ComfyGraph.Node(ComfyNodeTypes.VAELoader, new { vae_name = req.RequiredVae() });
        object vae = ComfyGraph.Ref(Vae, 0);
        wf[Nodes.Source] = ComfyGraph.Node(ComfyNodeTypes.LoadImage, new { image = inputs.SourceImageName ?? throw new RenderValidationException("Wan image→video needs a source image, but none was provided.") });

        int len = p.IntReq(WorkflowParamKeys.Length);
        double fps = p.DblReq(WorkflowParamKeys.Fps);
        double budgetMp = (p.IntReq(WorkflowParamKeys.Width) * (double)p.IntReq(WorkflowParamKeys.Height)) / 1_000_000.0;

        // Optional padding: expand the source canvas with whitespace before the budget scale, so the character has room
        // to move outside its original bounding box (each side a % of the source dim; see PadGeom). Composite the source
        // (alpha-respecting, onto white — same nodes FlattenOnWhite uses) at the offset for the whitespace. Source dims
        // come from the uploaded frame (inputs.SourceWidth/Height). When every pad_*_pct is 0 PadGeom returns null and
        // the graph is the original.
        // i2v: the source is a still, so its dimensions are ALWAYS measured. A zero here is a broken source, not the
        // valid "dims unknown" of the video-source path (a different workflow) — refuse it rather than skip the pad.
        Ensure.GreaterThanZero(inputs.SourceWidth);
        Ensure.GreaterThanZero(inputs.SourceHeight);
        object scaleSource = ComfyGraph.Ref(Nodes.Source, 0);
        if (PadGeom(Pct(WorkflowParamKeys.PadLeftPct), Pct(WorkflowParamKeys.PadRightPct), Pct(WorkflowParamKeys.PadTopPct), Pct(WorkflowParamKeys.PadBottomPct),
                    inputs.SourceWidth, inputs.SourceHeight) is (int cw, int ch, int px, int py))
        {
            wf[PadCanvas] = ComfyGraph.Node(ComfyNodeTypes.EmptyImage, new { width = cw, height = ch, batch_size = 1, color = 0xFFFFFF });
            wf[PadMask] = ComfyGraph.Node(ComfyNodeTypes.InvertMask, new { mask = ComfyGraph.Ref(Nodes.Source, 1) });
            wf[PadComposite] = ComfyGraph.Node(ComfyNodeTypes.ImageCompositeMasked, new { destination = ComfyGraph.Ref(PadCanvas, 0), source = ComfyGraph.Ref(Nodes.Source, 0), x = px, y = py, resize_source = false, mask = ComfyGraph.Ref(PadMask, 0) });
            scaleSource = ComfyGraph.Ref(PadComposite, 0);
        }
        wf[ScaledSource] = ComfyGraph.Node(ComfyNodeTypes.ImageScaleToTotalPixels, new { image = scaleSource, upscale_method = "lanczos", megapixels = budgetMp, resolution_steps = 16 });
        wf[SourceSize] = ComfyGraph.Node(ComfyNodeTypes.GetImageSize, new { image = ComfyGraph.Ref(ScaledSource, 0) });
        wf[Positive] = ComfyGraph.Node(ComfyNodeTypes.CLIPTextEncode, new { text = inputs.Positive, clip });
        wf[Negative] = ComfyGraph.Node(ComfyNodeTypes.CLIPTextEncode, new { text = ComfyGraph.ComposeNegative(p.Str(WorkflowParamKeys.Negative), inputs.Negative), clip });
        // First/last-frame conditioning when the caller supplied an END frame (the source is the first frame): swap the
        // plain WanImageToVideo for WanFirstLastFrameToVideo, pinning both ends. The node resizes end_image to
        // width/height itself, so the raw LoadImage is fine. Without an end frame the plain WanImageToVideo path runs.
        // Both nodes re-emit the same 3 outputs (positive, negative, latent), so the downstream sampler wiring is shared.
        if (!string.IsNullOrEmpty(inputs.EndImageName))
        {
            wf[EndFrame] = ComfyGraph.Node(ComfyNodeTypes.LoadImage, new { image = inputs.EndImageName });
            object endImage = ComfyGraph.Ref(EndFrame, 0);
            // Pad the END frame the SAME way as the first frame when asked (end_pad_*_pct), so both share one padded
            // canvas. Otherwise WanFirstLastFrameToVideo just stretches the raw end frame to the (padded) start size and
            // the pose lands in the wrong place — the clip never reaches it. Scale the end image to the source frame
            // size, then composite it into the same white canvas (PadGeom) at the offset. The save gate in the caller
            // guarantees the two frames share an aspect, so the scale here is proportional (no distortion).
            if (PadGeom(Pct(WorkflowParamKeys.EndPadLeftPct), Pct(WorkflowParamKeys.EndPadRightPct), Pct(WorkflowParamKeys.EndPadTopPct), Pct(WorkflowParamKeys.EndPadBottomPct),
                        inputs.SourceWidth, inputs.SourceHeight) is (int ecw, int ech, int epx, int epy))
            {
                int sw = inputs.SourceWidth, sh = inputs.SourceHeight;
                wf[EndScale] = ComfyGraph.Node(ComfyNodeTypes.ImageScale, new { image = ComfyGraph.Ref(EndFrame, 0), upscale_method = "lanczos", width = sw, height = sh, crop = "disabled" });
                wf[EndPadCanvas] = ComfyGraph.Node(ComfyNodeTypes.EmptyImage, new { width = ecw, height = ech, batch_size = 1, color = 0xFFFFFF });
                wf[EndPadComposite] = ComfyGraph.Node(ComfyNodeTypes.ImageCompositeMasked, new { destination = ComfyGraph.Ref(EndPadCanvas, 0), source = ComfyGraph.Ref(EndScale, 0), x = epx, y = epy, resize_source = false });
                endImage = ComfyGraph.Ref(EndPadComposite, 0);
            }
            wf[Cond] = ComfyGraph.Node(ComfyNodeTypes.WanFirstLastFrameToVideo, new
            {
                positive = ComfyGraph.Ref(Positive, 0),
                negative = ComfyGraph.Ref(Negative, 0),
                vae,
                width = ComfyGraph.Ref(SourceSize, 0),
                height = ComfyGraph.Ref(SourceSize, 1),
                length = len,
                batch_size = 1,
                start_image = ComfyGraph.Ref(ScaledSource, 0),
                end_image = endImage,
            });
        }
        else
        {
            // WanImageToVideo re-emits conditioning + the start latent (3 outputs: positive, negative, latent).
            wf[Cond] = ComfyGraph.Node(ComfyNodeTypes.WanImageToVideo, new
            {
                positive = ComfyGraph.Ref(Positive, 0),
                negative = ComfyGraph.Ref(Negative, 0),
                vae,
                width = ComfyGraph.Ref(SourceSize, 0),
                height = ComfyGraph.Ref(SourceSize, 1),
                length = len,
                batch_size = 1,
                start_image = ComfyGraph.Ref(ScaledSource, 0),
            });
        }
        object pos = ComfyGraph.Ref(Cond, 0), neg = ComfyGraph.Ref(Cond, 1), lat = ComfyGraph.Ref(Cond, 2);
        object outLat = Vid.MoESample(wf, p, mh, ml, pos, neg, lat, ComfyGraph.Seed(p));
        wf[Decode] = ComfyGraph.Node(ComfyNodeTypes.VAEDecode, new { samples = outLat, vae });
        wf[Save] = ComfyGraph.Node(ComfyNodeTypes.SaveAnimatedWEBP, new { images = ComfyGraph.Ref(Decode, 0), filename_prefix = "forgemcp_edit", fps, lossless = false, quality = 80, method = "default" });
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
        // The second MoE expert. A slot id, resolved to this machine's bound file — a literal filename would
        // put a model reference outside the binding system where nobody could change it.
        new() { Key = WorkflowParamKeys.UnetLow, Type = ParamType.String, IsModelRef = true, Label = "Low-noise expert" },
    }).ToArray();

    public override string Name => "wan22-t2v-a14b";
    public override WorkflowMedia Media => WorkflowMedia.Video;

    /// <summary>The MoE experts + samplers ("4"/"5"/"41"/"51"/"3"/"31") are written by Vid; Clip/Vae/Positive/Negative/
    /// Decode/Save reuse the inherited txt2img roles; only the empty video latent is an own node.</summary>
    private const string VideoLatent = "14";

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        Dictionary<string, object> wf = new Dictionary<string, object>();
        (object? mh, object? ml) = Vid.LoadExperts(wf, p, req);
        wf[Nodes.Clip] = ComfyGraph.Node(ComfyNodeTypes.CLIPLoader, new { clip_name = req.TextEncoder(0), type = "wan", device = "default" });
        object clip = ComfyGraph.Ref(Nodes.Clip, 0);
        wf[Nodes.Vae] = ComfyGraph.Node(ComfyNodeTypes.VAELoader, new { vae_name = req.RequiredVae() });
        object vae = ComfyGraph.Ref(Nodes.Vae, 0);

        (int w, int h) = p.DimsReq(WorkflowParamKeys.Aspect, ComfyGraph.NormalizeAspect(inputs.Aspect));
        int len = p.IntReq(WorkflowParamKeys.Length);
        double fps = p.DblReq(WorkflowParamKeys.Fps);
        wf[Nodes.Positive] = ComfyGraph.Node(ComfyNodeTypes.CLIPTextEncode, new { text = inputs.Positive, clip });
        wf[Nodes.Negative] = ComfyGraph.Node(ComfyNodeTypes.CLIPTextEncode, new { text = inputs.Negative ?? "", clip });
        wf[VideoLatent] = ComfyGraph.Node(ComfyNodeTypes.EmptyHunyuanLatentVideo, new { width = w, height = h, length = len, batch_size = 1 });
        object outLat = Vid.MoESample(wf, p, mh, ml, ComfyGraph.Ref(Nodes.Positive, 0), ComfyGraph.Ref(Nodes.Negative, 0), ComfyGraph.Ref(VideoLatent, 0), ComfyGraph.Seed(p));
        wf[Nodes.Decode] = ComfyGraph.Node(ComfyNodeTypes.VAEDecode, new { samples = outLat, vae });
        wf[Nodes.Save] = ComfyGraph.Node(ComfyNodeTypes.SaveAnimatedWEBP, new { images = ComfyGraph.Ref(Nodes.Decode, 0), filename_prefix = "forgemcp", fps, lossless = false, quality = 80, method = "default" });
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

    /// <summary>Own nodes beyond the inherited txt2img roles (Model/Clip/Vae/Positive/Negative/Sampler/Decode/Save reused below).</summary>
    private const string ModelSampling = "30";
    private const string VideoLatent = "14";
    private const string Scheduler = "55";
    private const string SamplerSelect = "56";
    private const string Noise = "57";
    private const string Guider = "58";

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        Dictionary<string, object> wf = new Dictionary<string, object>();
        wf[Nodes.Model] = ComfyGraph.DiffusionLoader(req.RequiredCheckpoint());
        wf[ModelSampling] = ComfyGraph.Node(ComfyNodeTypes.ModelSamplingSD3, new { model = ComfyGraph.Ref(Nodes.Model, 0), shift = p.DblReq(WorkflowParamKeys.Shift) });
        object model = ComfyGraph.Ref(ModelSampling, 0);
        wf[Nodes.Clip] = ComfyGraph.Node(ComfyNodeTypes.DualCLIPLoader, new { clip_name1 = req.TextEncoder(0), clip_name2 = req.TextEncoder(1), type = "hunyuan_video_15", device = "default" });
        object clip = ComfyGraph.Ref(Nodes.Clip, 0);
        wf[Nodes.Vae] = ComfyGraph.Node(ComfyNodeTypes.VAELoader, new { vae_name = req.RequiredVae() });
        object vae = ComfyGraph.Ref(Nodes.Vae, 0);

        (int w, int h) = p.DimsReq(WorkflowParamKeys.Aspect, ComfyGraph.NormalizeAspect(inputs.Aspect));
        int len = p.IntReq(WorkflowParamKeys.Length);
        double fps = p.DblReq(WorkflowParamKeys.Fps);
        wf[Nodes.Positive] = ComfyGraph.Node(ComfyNodeTypes.CLIPTextEncode, new { text = inputs.Positive, clip });
        wf[Nodes.Negative] = ComfyGraph.Node(ComfyNodeTypes.CLIPTextEncode, new { text = inputs.Negative ?? "", clip });
        wf[VideoLatent] = ComfyGraph.Node(ComfyNodeTypes.EmptyHunyuanVideo15Latent, new { width = w, height = h, length = len, batch_size = 1 });
        wf[Scheduler] = ComfyGraph.Node(ComfyNodeTypes.BasicScheduler, new { model, scheduler = ComfyGraph.MapScheduler(p.StrReq(WorkflowParamKeys.Scheduler)), steps = p.IntReq(WorkflowParamKeys.Steps), denoise = 1.0 });
        wf[SamplerSelect] = ComfyGraph.Node(ComfyNodeTypes.KSamplerSelect, new { sampler_name = ComfyGraph.MapSampler(p.StrReq(WorkflowParamKeys.Sampler)) });
        wf[Noise] = ComfyGraph.Node(ComfyNodeTypes.RandomNoise, new { noise_seed = ComfyGraph.Seed(p) });
        wf[Guider] = ComfyGraph.Node(ComfyNodeTypes.CFGGuider, new { model, positive = ComfyGraph.Ref(Nodes.Positive, 0), negative = ComfyGraph.Ref(Nodes.Negative, 0), cfg = p.DblReq(WorkflowParamKeys.Cfg) });
        wf[Nodes.Sampler] = ComfyGraph.Node(ComfyNodeTypes.SamplerCustomAdvanced, new { noise = ComfyGraph.Ref(Noise, 0), guider = ComfyGraph.Ref(Guider, 0), sampler = ComfyGraph.Ref(SamplerSelect, 0), sigmas = ComfyGraph.Ref(Scheduler, 0), latent_image = ComfyGraph.Ref(VideoLatent, 0) });
        // Optional super-resolution second pass (1080p). t2v has no source image, so no start_image/CLIP-vision cues.
        object outLatent = HunyuanSr.Refine(wf, p, ComfyGraph.Ref(Nodes.Sampler, 0), ComfyGraph.Ref(Nodes.Positive, 0), ComfyGraph.Ref(Nodes.Negative, 0), vae, null, null, ComfyGraph.Seed(p));
        wf[Nodes.Decode] = HunyuanSr.Enabled(p)
            ? ComfyGraph.Node(ComfyNodeTypes.VAEDecodeTiled, new { samples = outLatent, vae, tile_size = 256, overlap = 64, temporal_size = 64, temporal_overlap = 8 })
            : ComfyGraph.Node(ComfyNodeTypes.VAEDecode, new { samples = outLatent, vae });
        wf[Nodes.Save] = ComfyGraph.Node(ComfyNodeTypes.SaveAnimatedWEBP, new { images = ComfyGraph.Ref(Nodes.Decode, 0), filename_prefix = "forgemcp", fps, lossless = false, quality = 80, method = "default" });
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

    /// <summary>Own nodes beyond the inherited txt2img roles (Model/Clip/Vae/Positive/Guidance/Sampler/Decode/Save reused below).</summary>
    private const string ModelSampling = "30";
    private const string VideoLatent = "14";
    private const string Scheduler = "55";
    private const string SamplerSelect = "56";
    private const string Noise = "57";
    private const string Guider = "58";

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        Dictionary<string, object> wf = new Dictionary<string, object>();
        wf[Nodes.Model] = ComfyGraph.DiffusionLoader(req.RequiredCheckpoint());
        wf[ModelSampling] = ComfyGraph.Node(ComfyNodeTypes.ModelSamplingSD3, new { model = ComfyGraph.Ref(Nodes.Model, 0), shift = p.DblReq(WorkflowParamKeys.Shift) });
        object model = ComfyGraph.Ref(ModelSampling, 0);
        wf[Nodes.Clip] = ComfyGraph.Node(ComfyNodeTypes.DualCLIPLoader, new { clip_name1 = req.TextEncoder(0), clip_name2 = req.TextEncoder(1), type = "hunyuan_video", device = "default" });
        object clip = ComfyGraph.Ref(Nodes.Clip, 0);
        wf[Nodes.Vae] = ComfyGraph.Node(ComfyNodeTypes.VAELoader, new { vae_name = req.RequiredVae() });
        object vae = ComfyGraph.Ref(Nodes.Vae, 0);

        (int w, int h) = p.DimsReq(WorkflowParamKeys.Aspect, ComfyGraph.NormalizeAspect(inputs.Aspect));
        int len = p.IntReq(WorkflowParamKeys.Length);
        double fps = p.DblReq(WorkflowParamKeys.Fps);
        wf[Nodes.Positive] = ComfyGraph.Node(ComfyNodeTypes.CLIPTextEncode, new { text = inputs.Positive, clip });
        wf[Nodes.Guidance] = ComfyGraph.Node(ComfyNodeTypes.FluxGuidance, new { conditioning = ComfyGraph.Ref(Nodes.Positive, 0), guidance = p.DblReq(WorkflowParamKeys.Guidance) });
        wf[VideoLatent] = ComfyGraph.Node(ComfyNodeTypes.EmptyHunyuanLatentVideo, new { width = w, height = h, length = len, batch_size = 1 });
        wf[Scheduler] = ComfyGraph.Node(ComfyNodeTypes.BasicScheduler, new { model, scheduler = ComfyGraph.MapScheduler(p.StrReq(WorkflowParamKeys.Scheduler)), steps = p.IntReq(WorkflowParamKeys.Steps), denoise = 1.0 });
        wf[SamplerSelect] = ComfyGraph.Node(ComfyNodeTypes.KSamplerSelect, new { sampler_name = ComfyGraph.MapSampler(p.StrReq(WorkflowParamKeys.Sampler)) });
        wf[Noise] = ComfyGraph.Node(ComfyNodeTypes.RandomNoise, new { noise_seed = ComfyGraph.Seed(p) });
        wf[Guider] = ComfyGraph.Node(ComfyNodeTypes.BasicGuider, new { model, conditioning = ComfyGraph.Ref(Nodes.Guidance, 0) });
        wf[Nodes.Sampler] = ComfyGraph.Node(ComfyNodeTypes.SamplerCustomAdvanced, new { noise = ComfyGraph.Ref(Noise, 0), guider = ComfyGraph.Ref(Guider, 0), sampler = ComfyGraph.Ref(SamplerSelect, 0), sigmas = ComfyGraph.Ref(Scheduler, 0), latent_image = ComfyGraph.Ref(VideoLatent, 0) });
        wf[Nodes.Decode] = ComfyGraph.Node(ComfyNodeTypes.VAEDecodeTiled, new { samples = ComfyGraph.Ref(Nodes.Sampler, 0), vae, tile_size = 256, overlap = 64, temporal_size = 64, temporal_overlap = 8 });
        wf[Nodes.Save] = ComfyGraph.Node(ComfyNodeTypes.SaveAnimatedWEBP, new { images = ComfyGraph.Ref(Nodes.Decode, 0), filename_prefix = "forgemcp", fps, lossless = false, quality = 80, method = "default" });
        return wf;
    }
}
