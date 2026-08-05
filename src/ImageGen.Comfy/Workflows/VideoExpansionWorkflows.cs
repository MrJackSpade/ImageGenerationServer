using System.Text.Json.Serialization;
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

    /// <summary>Two-stage MoE sampling over a typed <see cref="ComfyWorkflowGraph"/>: high-noise expert for
    /// [0,boundary), low-noise for [boundary,end). The two experts guide at DIFFERENT scales for t2v (high 4.0, low 3.0),
    /// so each stage takes its own cfg (<c>cfg_high</c>/<c>cfg_low</c>); <c>boundary</c> is the reference's sigma
    /// switch-threshold pre-mapped to a step index. <paramref name="sampler"/>/<paramref name="scheduler"/> are the
    /// ALREADY-MAPPED ComfyUI names; <paramref name="refinerSteps"/> is the optional draft/commit knob (null = the legacy
    /// shared-schedule tail). Returns the final latent.</summary>
    public static Output<Slot.Latent> MoESample(ComfyWorkflowGraph g, Output<Slot.Model> modelHigh, Output<Slot.Model> modelLow,
        Output<Slot.Conditioning> positive, Output<Slot.Conditioning> negative, Output<Slot.Latent> latent,
        int steps, int boundary, double cfgHigh, double cfgLow, string sampler, string scheduler, int? refinerSteps, long seed)
    {
        g[Nodes.HighSampler] = new KSamplerAdvanced
        {
            AddNoise = "enable",
            NoiseSeed = seed,
            Steps = steps,
            Cfg = cfgHigh,
            SamplerName = sampler,
            Scheduler = scheduler,
            StartAtStep = 0,
            EndAtStep = boundary,
            ReturnWithLeftoverNoise = "enable",
            Model = modelHigh,
            Positive = positive,
            Negative = negative,
            LatentImage = latent,
        };
        // refiner_steps: run the low-noise stage on its OWN schedule with exactly this many steps, leaving the high-noise
        // structure phase untouched — a draft (small N) then commits (large N) with byte-identical motion, because both
        // runs share the same stage-1 schedule/seed and hand off the same latent. The handoff sits at t* = 1 -
        // boundary/steps; total2 = round(N/t*) puts the refiner's start index on that same sigma (exact whenever N/t* is
        // whole). 0 = decode the handoff as-is; absent/negative = the legacy shared-schedule tail.
        int refiner = refinerSteps ?? -1;
        if (refiner == 0) return KSamplerAdvanced.Out(Nodes.HighSampler);
        int steps2 = steps, start2 = boundary;
        if (refiner > 0)
        {
            double tStar = 1.0 - (double)boundary / steps;
            steps2 = Math.Max(refiner + 1, (int)Math.Round(refiner / tStar));
            start2 = steps2 - refiner;
        }
        g[Nodes.LowSampler] = new KSamplerAdvanced
        {
            AddNoise = "disable",
            NoiseSeed = seed,
            Steps = steps2,
            Cfg = cfgLow,
            SamplerName = sampler,
            Scheduler = scheduler,
            StartAtStep = start2,
            EndAtStep = 10000,
            ReturnWithLeftoverNoise = "disable",
            Model = modelLow,
            Positive = positive,
            Negative = negative,
            LatentImage = KSamplerAdvanced.Out(Nodes.HighSampler),
        };
        return KSamplerAdvanced.Out(Nodes.LowSampler);
    }

    /// <summary>Load a high+low expert pair, each through ModelSamplingSD3(shift), over a typed graph. High file = the
    /// resolved checkpoint, low = the resolved <c>unet_low</c>. Both load through
    /// <see cref="ComfyGraph.DiffusionLoaderNode"/>, which picks its node from the bound file.</summary>
    public static (Output<Slot.Model> high, Output<Slot.Model> low) LoadExperts(ComfyWorkflowGraph g, string highFile, string lowFile, double shift)
    {
        g[Nodes.HighExpert] = ComfyGraph.DiffusionLoaderNode(highFile);
        g[Nodes.HighSampling] = new ModelSamplingSD3 { Model = UNETLoader.ModelOut(Nodes.HighExpert), Shift = shift };
        g[Nodes.LowExpert] = ComfyGraph.DiffusionLoaderNode(lowFile);
        g[Nodes.LowSampling] = new ModelSamplingSD3 { Model = UNETLoader.ModelOut(Nodes.LowExpert), Shift = shift };
        return (ModelSamplingSD3.Out(Nodes.HighSampling), ModelSamplingSD3.Out(Nodes.LowSampling));
    }
}

/// <summary>Wan 2.2 I2V-A14B image→video (two-expert MoE). Source image is the first frame; output an animated WEBP.
/// WanImageToVideo emits the (pos,neg,latent) triple consumed by the two KSamplerAdvanced stages.</summary>
public sealed class WanA14bI2VWorkflow : EditWorkflow<WanA14bI2VParams>
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

    protected override ComfyWorkflowGraph Build(WanA14bI2VParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new ComfyWorkflowGraph();
        string sampler = ComfyGraph.MapSampler(p.Sampler);
        string scheduler = ComfyGraph.MapScheduler(p.Scheduler);
        (Output<Slot.Model> mh, Output<Slot.Model> ml) = Vid.LoadExperts(g, req.RequiredCheckpoint(), p.UnetLow, p.Shift);
        g[Clip] = new CLIPLoader { ClipName = req.TextEncoder(0), Type = "wan", Device = "default" };
        Output<Slot.Clip> clip = CLIPLoader.ClipOut(Clip);
        g[Vae] = new VAELoader { VaeName = req.RequiredVae() };
        Output<Slot.Vae> vae = VAELoader.VaeOut(Vae);
        g[Nodes.Source] = new LoadImage { Image = inputs.SourceImageName ?? throw new RenderValidationException("Wan image→video needs a source image, but none was provided.") };

        int len = p.Length;
        double fps = p.Fps;
        double budgetMp = (p.Width * (double)p.Height) / 1_000_000.0;

        // Optional padding: expand the source canvas with whitespace before the budget scale, so the character has room
        // to move outside its original bounding box (each side a % of the source dim; see PadGeom). Composite the source
        // (alpha-respecting, onto white — same nodes FlattenOnWhite uses) at the offset for the whitespace. Source dims
        // come from the uploaded frame (inputs.SourceWidth/Height). When every pad_*_pct is 0 PadGeom returns null and
        // the graph is the original.
        // i2v: the source is a still, so its dimensions are ALWAYS measured. A zero here is a broken source, not the
        // valid "dims unknown" of the video-source path (a different workflow) — refuse it rather than skip the pad.
        Ensure.GreaterThanZero(inputs.SourceWidth);
        Ensure.GreaterThanZero(inputs.SourceHeight);
        Output<Slot.Image> scaleSource = LoadImage.ImageOut(Nodes.Source);
        if (PadGeom(p.PadLeftPct ?? 0, p.PadRightPct ?? 0, p.PadTopPct ?? 0, p.PadBottomPct ?? 0,
                    inputs.SourceWidth, inputs.SourceHeight) is (int cw, int ch, int px, int py))
        {
            g[PadCanvas] = new EmptyImageLiteralSize { Width = cw, Height = ch, BatchSize = 1, Color = 0xFFFFFF };
            g[PadMask] = new InvertMask { Mask = LoadImage.MaskOut(Nodes.Source) };
            g[PadComposite] = new ImageCompositeMasked { Destination = EmptyImageLiteralSize.Out(PadCanvas), Source = LoadImage.ImageOut(Nodes.Source), X = px, Y = py, ResizeSource = false, Mask = InvertMask.Out(PadMask) };
            scaleSource = ImageCompositeMasked.Out(PadComposite);
        }
        g[ScaledSource] = new ImageScaleToTotalPixels { Image = scaleSource, UpscaleMethod = "lanczos", Megapixels = budgetMp, ResolutionSteps = 16 };
        g[SourceSize] = new GetImageSize { Image = ImageScaleToTotalPixels.Out(ScaledSource) };
        g[Positive] = new CLIPTextEncode { Text = inputs.Positive, Clip = clip };
        g[Negative] = new CLIPTextEncode { Text = ComfyGraph.ComposeNegative(p.Negative, inputs.Negative), Clip = clip };
        Output<Slot.Conditioning> pos;
        Output<Slot.Conditioning> neg;
        Output<Slot.Latent> lat;
        // First/last-frame conditioning when the caller supplied an END frame (the source is the first frame): swap the
        // plain WanImageToVideo for WanFirstLastFrameToVideo, pinning both ends. The node resizes end_image to
        // width/height itself, so the raw LoadImage is fine. Without an end frame the plain WanImageToVideo path runs.
        // Both nodes re-emit the same 3 outputs (positive, negative, latent), so the downstream sampler wiring is shared.
        if (!string.IsNullOrEmpty(inputs.EndImageName))
        {
            g[EndFrame] = new LoadImage { Image = inputs.EndImageName };
            Output<Slot.Image> endImage = LoadImage.ImageOut(EndFrame);
            // Pad the END frame the SAME way as the first frame when asked (end_pad_*_pct), so both share one padded
            // canvas. Otherwise WanFirstLastFrameToVideo just stretches the raw end frame to the (padded) start size and
            // the pose lands in the wrong place — the clip never reaches it. Scale the end image to the source frame
            // size, then composite it into the same white canvas (PadGeom) at the offset. The save gate in the caller
            // guarantees the two frames share an aspect, so the scale here is proportional (no distortion).
            if (PadGeom(p.EndPadLeftPct ?? 0, p.EndPadRightPct ?? 0, p.EndPadTopPct ?? 0, p.EndPadBottomPct ?? 0,
                        inputs.SourceWidth, inputs.SourceHeight) is (int ecw, int ech, int epx, int epy))
            {
                int sw = inputs.SourceWidth, sh = inputs.SourceHeight;
                g[EndScale] = new ImageScale { Image = LoadImage.ImageOut(EndFrame), UpscaleMethod = "lanczos", Width = sw, Height = sh, Crop = "disabled" };
                g[EndPadCanvas] = new EmptyImageLiteralSize { Width = ecw, Height = ech, BatchSize = 1, Color = 0xFFFFFF };
                g[EndPadComposite] = new ImageCompositeMaskedNoMask { Destination = EmptyImageLiteralSize.Out(EndPadCanvas), Source = ImageScale.Out(EndScale), X = epx, Y = epy, ResizeSource = false };
                endImage = ImageCompositeMaskedNoMask.Out(EndPadComposite);
            }
            g[Cond] = new WanFirstLastFrameToVideo
            {
                Positive = CLIPTextEncode.Out(Positive),
                Negative = CLIPTextEncode.Out(Negative),
                Vae = vae,
                Width = GetImageSize.WidthOut(SourceSize),
                Height = GetImageSize.HeightOut(SourceSize),
                Length = len,
                BatchSize = 1,
                StartImage = ImageScaleToTotalPixels.Out(ScaledSource),
                EndImage = endImage,
            };
            pos = WanFirstLastFrameToVideo.PositiveOut(Cond);
            neg = WanFirstLastFrameToVideo.NegativeOut(Cond);
            lat = WanFirstLastFrameToVideo.LatentOut(Cond);
        }
        else
        {
            // WanImageToVideo re-emits conditioning + the start latent (3 outputs: positive, negative, latent).
            g[Cond] = new WanImageToVideoNoVision
            {
                Positive = CLIPTextEncode.Out(Positive),
                Negative = CLIPTextEncode.Out(Negative),
                Vae = vae,
                Width = GetImageSize.WidthOut(SourceSize),
                Height = GetImageSize.HeightOut(SourceSize),
                Length = len,
                BatchSize = 1,
                StartImage = ImageScaleToTotalPixels.Out(ScaledSource),
            };
            pos = WanImageToVideoNoVision.PositiveOut(Cond);
            neg = WanImageToVideoNoVision.NegativeOut(Cond);
            lat = WanImageToVideoNoVision.LatentOut(Cond);
        }
        Output<Slot.Latent> outLat = Vid.MoESample(g, mh, ml, pos, neg, lat, p.Steps, p.Boundary, p.CfgHigh, p.CfgLow, sampler, scheduler, p.RefinerSteps, ComfyGraph.Seed(p.Seed));
        g[Decode] = new VAEDecode { Samples = outLat, Vae = vae };
        g[Save] = new SaveAnimatedWEBPLiteralFps { Images = VAEDecode.Out(Decode), FilenamePrefix = "forgemcp_edit", Fps = fps, Lossless = false, Quality = 80, Method = "default" };
        return g;
    }
}

/// <summary>Wan 2.2 I2V-A14B image→video parameters (its own record — the two MoE experts drive their own loaders, so
/// none of the shared loader-head knobs apply). The <c>unet_low</c> resolved model ref + the sampler/MoE knobs
/// (<c>shift</c>/<c>steps</c>/<c>boundary</c>/<c>cfg_high</c>/<c>cfg_low</c>) are <c>required</c>; the render budget
/// (<c>width</c>/<c>height</c>), clip <c>length</c> and <c>fps</c> are required; <c>refiner_steps</c>, the four
/// <c>pad_*_pct</c> + four <c>end_pad_*_pct</c> percentages, the model's own <c>negative</c> and the <c>seed</c> are
/// optional (an absent pad % is 0 — no pad on that side).</summary>
public sealed record WanA14bI2VParams
{
    [JsonPropertyName(WorkflowParamKeys.UnetLow)]         public required string UnetLow { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Shift)]           public required double Shift { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Steps)]           public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Boundary)]        public required int Boundary { get; init; }
    [JsonPropertyName(WorkflowParamKeys.CfgHigh)]         public required double CfgHigh { get; init; }
    [JsonPropertyName(WorkflowParamKeys.CfgLow)]          public required double CfgLow { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Sampler)]         public required string Sampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Scheduler)]       public required string Scheduler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Width)]           public required int Width { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Height)]          public required int Height { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Length)]          public required int Length { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Fps)]             public required double Fps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.RefinerSteps)]    public int? RefinerSteps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.PadLeftPct)]      public int? PadLeftPct { get; init; }
    [JsonPropertyName(WorkflowParamKeys.PadRightPct)]     public int? PadRightPct { get; init; }
    [JsonPropertyName(WorkflowParamKeys.PadTopPct)]       public int? PadTopPct { get; init; }
    [JsonPropertyName(WorkflowParamKeys.PadBottomPct)]    public int? PadBottomPct { get; init; }
    [JsonPropertyName(WorkflowParamKeys.EndPadLeftPct)]   public int? EndPadLeftPct { get; init; }
    [JsonPropertyName(WorkflowParamKeys.EndPadRightPct)]  public int? EndPadRightPct { get; init; }
    [JsonPropertyName(WorkflowParamKeys.EndPadTopPct)]    public int? EndPadTopPct { get; init; }
    [JsonPropertyName(WorkflowParamKeys.EndPadBottomPct)] public int? EndPadBottomPct { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Negative)]        public string? Negative { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)]            public long Seed { get; init; }
}

/// <summary>Wan 2.2 T2V-A14B text→video (two-expert MoE). No source image — an EmptyHunyuanLatentVideo seeds the
/// clip and the conditioning feeds the two KSamplerAdvanced stages directly.</summary>
public sealed class WanA14bT2VWorkflow : Txt2ImgWorkflow<WanA14bT2VParams>
{
    /// <inheritdoc/>
    public override IReadOnlyList<ParamSpec> Schema => Txt2ImgWorkflowBase.SharedSchema.Concat(new ParamSpec[]
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

    protected override ComfyWorkflowGraph Build(WanA14bT2VParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new ComfyWorkflowGraph();
        string sampler = ComfyGraph.MapSampler(p.Sampler);
        string scheduler = ComfyGraph.MapScheduler(p.Scheduler);
        (Output<Slot.Model> mh, Output<Slot.Model> ml) = Vid.LoadExperts(g, req.RequiredCheckpoint(), p.UnetLow, p.Shift);
        g[Nodes.Clip] = new CLIPLoader { ClipName = req.TextEncoder(0), Type = "wan", Device = "default" };
        Output<Slot.Clip> clip = CLIPLoader.ClipOut(Nodes.Clip);
        g[Nodes.Vae] = new VAELoader { VaeName = req.RequiredVae() };
        Output<Slot.Vae> vae = VAELoader.VaeOut(Nodes.Vae);

        (int w, int h) = p.Dims(ComfyGraph.NormalizeAspect(inputs.Aspect));
        int len = p.Length;
        double fps = p.Fps;
        g[Nodes.Positive] = new CLIPTextEncode { Text = inputs.Positive, Clip = clip };
        g[Nodes.Negative] = new CLIPTextEncode { Text = inputs.Negative ?? "", Clip = clip };
        g[VideoLatent] = new EmptyHunyuanLatentVideo { Width = w, Height = h, Length = len, BatchSize = 1 };
        Output<Slot.Latent> outLat = Vid.MoESample(g, mh, ml, CLIPTextEncode.Out(Nodes.Positive), CLIPTextEncode.Out(Nodes.Negative), EmptyHunyuanLatentVideo.Out(VideoLatent), p.Steps, p.Boundary, p.CfgHigh, p.CfgLow, sampler, scheduler, p.RefinerSteps, ComfyGraph.Seed(p.Seed));
        g[Nodes.Decode] = new VAEDecode { Samples = outLat, Vae = vae };
        g[Nodes.Save] = new SaveAnimatedWEBPLiteralFps { Images = VAEDecode.Out(Nodes.Decode), FilenamePrefix = "forgemcp", Fps = fps, Lossless = false, Quality = 80, Method = "default" };
        return g;
    }
}

/// <summary>Wan 2.2 T2V-A14B text→video parameters. A custom-Build MoE model, so its own guidance is the dual
/// <c>cfg_high</c>/<c>cfg_low</c> (the base <c>cfg</c> is left unset), and its two experts drive their own loaders via
/// <c>unet_low</c> + <c>shift</c>. The MoE step window (<c>steps</c> from the base, <c>boundary</c>) and clip
/// <c>length</c>/<c>fps</c> are required; <c>refiner_steps</c> is optional (absent = the legacy shared-schedule tail).</summary>
public sealed record WanA14bT2VParams : Txt2ImgParams
{
    [JsonPropertyName(WorkflowParamKeys.UnetLow)]      public required string UnetLow { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Shift)]        public required double Shift { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Boundary)]     public required int Boundary { get; init; }
    [JsonPropertyName(WorkflowParamKeys.CfgHigh)]      public required double CfgHigh { get; init; }
    [JsonPropertyName(WorkflowParamKeys.CfgLow)]       public required double CfgLow { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Length)]       public required int Length { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Fps)]          public required double Fps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.RefinerSteps)] public int? RefinerSteps { get; init; }
}

/// <summary>HunyuanVideo 1.5 text→video (720p). UNETLoader + the Qwen2.5-VL/ByT5 DualCLIPLoader (type
/// "hunyuan_video_15") + ModelSamplingSD3 + EmptyHunyuanVideo15Latent + a CFGGuider/SamplerCustomAdvanced chain
/// (real CFG, negatives work). The text→video sibling of the 480p i2v editor already in the catalog.</summary>
public sealed class HunyuanVideo15T2VWorkflow : Txt2ImgWorkflow<HunyuanVideo15T2VParams>
{
    public override string Name => "hunyuanvideo15-t2v";
    public override WorkflowMedia Media => WorkflowMedia.Video;
    public override IReadOnlyList<ParamSpec> Schema => Txt2ImgWorkflowBase.SharedSchema.Concat(HunyuanSr.Schema).ToArray();

    /// <summary>Own nodes beyond the inherited txt2img roles (Model/Clip/Vae/Positive/Negative/Sampler/Decode/Save reused below).</summary>
    private const string ModelSampling = "30";
    private const string VideoLatent = "14";
    private const string Scheduler = "55";
    private const string SamplerSelect = "56";
    private const string Noise = "57";
    private const string Guider = "58";

    protected override ComfyWorkflowGraph Build(HunyuanVideo15T2VParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new ComfyWorkflowGraph();
        string sampler = ComfyGraph.MapSampler(p.Sampler);
        string scheduler = ComfyGraph.MapScheduler(p.Scheduler);
        long seed = ComfyGraph.Seed(p.Seed);
        g[Nodes.Model] = ComfyGraph.DiffusionLoaderNode(req.RequiredCheckpoint());
        g[ModelSampling] = new ModelSamplingSD3 { Model = UNETLoader.ModelOut(Nodes.Model), Shift = p.Shift };
        Output<Slot.Model> model = ModelSamplingSD3.Out(ModelSampling);
        g[Nodes.Clip] = new DualCLIPLoader { ClipName1 = req.TextEncoder(0), ClipName2 = req.TextEncoder(1), Type = "hunyuan_video_15", Device = "default" };
        Output<Slot.Clip> clip = DualCLIPLoader.ClipOut(Nodes.Clip);
        g[Nodes.Vae] = new VAELoader { VaeName = req.RequiredVae() };
        Output<Slot.Vae> vae = VAELoader.VaeOut(Nodes.Vae);

        (int w, int h) = p.Dims(ComfyGraph.NormalizeAspect(inputs.Aspect));
        int len = p.Length;
        double fps = p.Fps;
        g[Nodes.Positive] = new CLIPTextEncode { Text = inputs.Positive, Clip = clip };
        g[Nodes.Negative] = new CLIPTextEncode { Text = inputs.Negative ?? "", Clip = clip };
        g[VideoLatent] = new EmptyHunyuanVideo15Latent { Width = w, Height = h, Length = len, BatchSize = 1 };
        g[Scheduler] = new BasicScheduler { Model = model, Scheduler = scheduler, Steps = p.Steps, Denoise = 1.0 };
        g[SamplerSelect] = new KSamplerSelect { SamplerName = sampler };
        g[Noise] = new RandomNoise { NoiseSeed = seed };
        g[Guider] = new CFGGuider { Model = model, Positive = CLIPTextEncode.Out(Nodes.Positive), Negative = CLIPTextEncode.Out(Nodes.Negative), Cfg = p.RequiredCfg() };
        g[Nodes.Sampler] = new SamplerCustomAdvanced { Noise = RandomNoise.Out(Noise), Guider = CFGGuider.Out(Guider), Sampler = KSamplerSelect.Out(SamplerSelect), Sigmas = BasicScheduler.Out(Scheduler), LatentImage = EmptyHunyuanVideo15Latent.Out(VideoLatent) };
        // Optional super-resolution second pass (1080p). t2v has no source image, so no start_image/CLIP-vision cues.
        Output<Slot.Latent> outLatent = HunyuanSr.Refine(g, p, SamplerCustomAdvanced.Out(Nodes.Sampler), CLIPTextEncode.Out(Nodes.Positive), CLIPTextEncode.Out(Nodes.Negative), vae, null, null, sampler, scheduler, seed);
        g[Nodes.Decode] = HunyuanSr.Enabled(p)
            ? new VAEDecodeTiled { Samples = outLatent, Vae = vae, TileSize = 256, Overlap = 64, TemporalSize = 64, TemporalOverlap = 8 }
            : new VAEDecode { Samples = outLatent, Vae = vae };
        g[Nodes.Save] = new SaveAnimatedWEBPLiteralFps { Images = new Output<Slot.Image>(Nodes.Decode, 0), FilenamePrefix = "forgemcp", Fps = fps, Lossless = false, Quality = 80, Method = "default" };
        return g;
    }
}

/// <summary>HunyuanVideo 1.5 text→video parameters. The flow <c>shift</c>, clip <c>length</c> and playback <c>fps</c> ride
/// on top of the shared txt2img knobs; the optional super-resolution second pass (<c>sr</c> + the <c>sr_*</c> settings)
/// is exposed through <see cref="IHunyuanSrParams"/> — all nullable, since a non-SR config supplies none of them.</summary>
public sealed record HunyuanVideo15T2VParams : Txt2ImgParams, IHunyuanSrParams
{
    [JsonPropertyName(WorkflowParamKeys.Shift)]       public required double Shift { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Length)]      public required int Length { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Fps)]         public required double Fps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Sr)]          public bool Sr { get; init; }
    [JsonPropertyName(WorkflowParamKeys.SrModel)]     public string? SrModel { get; init; }
    [JsonPropertyName(WorkflowParamKeys.SrUpsampler)] public string? SrUpsampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.SrWidth)]     public int? SrWidth { get; init; }
    [JsonPropertyName(WorkflowParamKeys.SrHeight)]    public int? SrHeight { get; init; }
    [JsonPropertyName(WorkflowParamKeys.SrSteps)]     public int? SrSteps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.SrDenoise)]   public double? SrDenoise { get; init; }
    [JsonPropertyName(WorkflowParamKeys.SrNoiseAug)]  public double? SrNoiseAug { get; init; }
    [JsonPropertyName(WorkflowParamKeys.SrCfg)]       public double? SrCfg { get; init; }
    [JsonPropertyName(WorkflowParamKeys.SrShift)]     public double? SrShift { get; init; }
}

/// <summary>Original HunyuanVideo 13B text→video. The diffusion loader follows the bound file; the LLaVA-Llama3/CLIP-L DualCLIPLoader
/// (type "hunyuan_video") + ModelSamplingSD3 + embedded FluxGuidance + EmptyHunyuanLatentVideo + a
/// BasicGuider/SamplerCustomAdvanced chain (guidance-distilled: cfg 1, no negative). The t2v sibling of the i2v
/// GGUF editor already in the catalog.</summary>
public sealed class HunyuanVideoT2VWorkflow : Txt2ImgWorkflow<HunyuanVideoT2VParams>
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

    protected override ComfyWorkflowGraph Build(HunyuanVideoT2VParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new ComfyWorkflowGraph();
        g[Nodes.Model] = ComfyGraph.DiffusionLoaderNode(req.RequiredCheckpoint());
        g[ModelSampling] = new ModelSamplingSD3 { Model = UNETLoader.ModelOut(Nodes.Model), Shift = p.Shift };
        Output<Slot.Model> model = ModelSamplingSD3.Out(ModelSampling);
        g[Nodes.Clip] = new DualCLIPLoader { ClipName1 = req.TextEncoder(0), ClipName2 = req.TextEncoder(1), Type = "hunyuan_video", Device = "default" };
        Output<Slot.Clip> clip = DualCLIPLoader.ClipOut(Nodes.Clip);
        g[Nodes.Vae] = new VAELoader { VaeName = req.RequiredVae() };
        Output<Slot.Vae> vae = VAELoader.VaeOut(Nodes.Vae);

        (int w, int h) = p.Dims(ComfyGraph.NormalizeAspect(inputs.Aspect));
        int len = p.Length;
        double fps = p.Fps;
        g[Nodes.Positive] = new CLIPTextEncode { Text = inputs.Positive, Clip = clip };
        g[Nodes.Guidance] = new FluxGuidance { Conditioning = CLIPTextEncode.Out(Nodes.Positive), Guidance = p.RequiredGuidance() };
        g[VideoLatent] = new EmptyHunyuanLatentVideo { Width = w, Height = h, Length = len, BatchSize = 1 };
        g[Scheduler] = new BasicScheduler { Model = model, Scheduler = ComfyGraph.MapScheduler(p.Scheduler), Steps = p.Steps, Denoise = 1.0 };
        g[SamplerSelect] = new KSamplerSelect { SamplerName = ComfyGraph.MapSampler(p.Sampler) };
        g[Noise] = new RandomNoise { NoiseSeed = ComfyGraph.Seed(p.Seed) };
        g[Guider] = new BasicGuider { Model = model, Conditioning = FluxGuidance.Out(Nodes.Guidance) };
        g[Nodes.Sampler] = new SamplerCustomAdvanced { Noise = RandomNoise.Out(Noise), Guider = BasicGuider.Out(Guider), Sampler = KSamplerSelect.Out(SamplerSelect), Sigmas = BasicScheduler.Out(Scheduler), LatentImage = EmptyHunyuanLatentVideo.Out(VideoLatent) };
        g[Nodes.Decode] = new VAEDecodeTiled { Samples = SamplerCustomAdvanced.Out(Nodes.Sampler), Vae = vae, TileSize = 256, Overlap = 64, TemporalSize = 64, TemporalOverlap = 8 };
        g[Nodes.Save] = new SaveAnimatedWEBPLiteralFps { Images = VAEDecodeTiled.Out(Nodes.Decode), FilenamePrefix = "forgemcp", Fps = fps, Lossless = false, Quality = 80, Method = "default" };
        return g;
    }
}

/// <summary>Original HunyuanVideo 13B text→video parameters. Adds the flow <c>shift</c>, clip <c>length</c> and playback
/// <c>fps</c> to the shared txt2img knobs; the embedded FluxGuidance value is the base <c>guidance</c> (required by this
/// guidance-distilled graph, read through <see cref="RequiredGuidance"/>).</summary>
public sealed record HunyuanVideoT2VParams : Txt2ImgParams
{
    [JsonPropertyName(WorkflowParamKeys.Shift)]  public required double Shift { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Length)] public required int Length { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Fps)]    public required double Fps { get; init; }

    /// <summary>The embedded-guidance value this graph cannot build without — the base's nullable <c>guidance</c>, or a
    /// refusal naming it (the typed form of <c>DblReq(guidance)</c>).</summary>
    public double RequiredGuidance() => Guidance ?? throw new RenderValidationException(
        $"This configuration needs a value for '{WorkflowParamKeys.Guidance}' and none is set. It must supply one — there is no default.");
}
