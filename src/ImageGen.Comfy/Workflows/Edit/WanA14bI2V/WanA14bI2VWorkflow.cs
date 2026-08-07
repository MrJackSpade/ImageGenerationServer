using ImageGen.Application.Rendering;
using ImageGen.Domain;

namespace ImageGen.Comfy.Edit.WanA14bI2V;

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
    public override IReadOnlyList<ParamSpec> Schema =>
    [
        .. base.Schema,
        new() { Key = WorkflowParamKeys.PadLeftPct,   Type = ParamType.Int, Min = 0, Max = 2000, Step = 1, Label = "Pad left %",   Help = "Whitespace on the left, % of source width" },
        new() { Key = WorkflowParamKeys.PadRightPct,  Type = ParamType.Int, Min = 0, Max = 2000, Step = 1, Label = "Pad right %",  Help = "Whitespace on the right, % of source width" },
        new() { Key = WorkflowParamKeys.PadTopPct,    Type = ParamType.Int, Min = 0, Max = 2000, Step = 1, Label = "Pad top %",    Help = "Whitespace on top, % of source height" },
        new() { Key = WorkflowParamKeys.PadBottomPct, Type = ParamType.Int, Min = 0, Max = 2000, Step = 1, Label = "Pad bottom %", Help = "Whitespace on the bottom, % of source height" },
        new() { Key = WorkflowParamKeys.RefinerSteps, Type = ParamType.Int, Min = 0, Max = 40, Step = 1, Label = "Refiner steps", Help = "Low = fast draft (same motion), high = sharp final; re-run the same seed to commit" },
        new() { Key = WorkflowParamKeys.UnetLow, Type = ParamType.String, IsModelRef = true, Label = "Low-noise expert" },
    ];

    /// <summary>
    /// The padded-canvas geometry for the four side-percentages: the white canvas size and the (X,Y) offset the source
    /// is composited at. Each side adds <c>dim·pct/100</c> pixels; the canvas is the source plus those additions, and
    /// the source sits at the top-left additions (so the L/T whitespace pushes it toward the bottom-right). Null when
    /// every side is zero (no padding). Must stay in lockstep with SpritePipeline's <c>PaddingPreset.PaddedAspect</c>.
    /// </summary>
    private static (int W, int H, int X, int Y)? PadGeom(int pctL, int pctR, int pctT, int pctB, int sw, int sh)
    {
        // A negative side-percentage is meaningless geometry (you cannot add negative whitespace) — REFUSED, not
        // floored to zero, which would silently drop the offending side.
        _ = Ensure.NotNegative(pctL);
        _ = Ensure.NotNegative(pctR);
        _ = Ensure.NotNegative(pctT);
        _ = Ensure.NotNegative(pctB);
        if (pctL == 0 && pctR == 0 && pctT == 0 && pctB == 0)
        {
            return null;   // no padding
        }

        int addL = sw * pctL / 100, addR = sw * pctR / 100, addT = sh * pctT / 100, addB = sh * pctB / 100;
        return (sw + addL + addR, sh + addT + addB, addL, addT);
    }

    /// <summary>Wan I2V-A14B's 16-px snap grid for the budget scale — single source for both scale nodes and the ETA
    /// render-size.</summary>
    private const int BudgetSteps = 16;

    /// <summary>The render budget is the config's <c>width×height</c> target (MP), not the source dims: the source —
    /// padded or not — is scaled to that total-pixel budget, so the ETA keys on the target area. Snapping the raw
    /// source aspect to this budget lands on ~the same pixel count as the padded frame (only the aspect differs, which
    /// the ETA ignores).</summary>
    protected override (double Megapixels, int ResolutionSteps)? EtaBudget(WanA14bI2VParams p)
        => (p.Width * (double)p.Height / 1_000_000.0, BudgetSteps);

    protected override ComfyWorkflowGraph Build(WanA14bI2VParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new();
        string sampler = ComfyGraph.MapSampler(p.Sampler);
        string scheduler = ComfyGraph.MapScheduler(p.Scheduler);
        (Output<Slot.Model> mh, Output<Slot.Model> ml) = Vid.LoadExperts(g, req.RequiredCheckpoint(), p.UnetLow, p.Shift);
        g[WanA14bI2VWorkflowNodes.Clip] = new CLIPLoader { ClipName = req.TextEncoder(0), Type = ComfyWidgets.ClipType.Wan, Device = ComfyWidgets.Device.Default };
        Output<Slot.Clip> clip = CLIPLoader.ClipOut(WanA14bI2VWorkflowNodes.Clip);
        g[WanA14bI2VWorkflowNodes.Vae] = new VAELoader { VaeName = req.RequiredVae() };
        Output<Slot.Vae> vae = VAELoader.VaeOut(WanA14bI2VWorkflowNodes.Vae);
        g[EditNodes.Source] = new LoadImage { Image = inputs.SourceImageName ?? throw new RenderValidationException("Wan image→video needs a source image, but none was provided.") };

        int len = p.Length;
        double fps = p.Fps;
        double budgetMp = p.Width * (double)p.Height / 1_000_000.0;

        // Optional padding: expand the source canvas with whitespace before the budget scale, so the character has room
        // to move outside its original bounding box (each side a % of the source dim; see PadGeom). Composite the source
        // (alpha-respecting, onto white — same nodes FlattenOnWhite uses) at the offset for the whitespace. Source dims
        // come from the uploaded frame (inputs.SourceWidth/Height). When every pad_*_pct is 0 PadGeom returns null and
        // the graph is the original.
        // i2v: the source is a still, so its dimensions are ALWAYS measured. A zero here is a broken source, not the
        // valid "dims unknown" of the video-source path (a different workflow) — refuse it rather than skip the pad.
        _ = Ensure.GreaterThanZero(inputs.SourceWidth);
        _ = Ensure.GreaterThanZero(inputs.SourceHeight);
        Output<Slot.Image> scaleSource = LoadImage.ImageOut(EditNodes.Source);
        if (PadGeom(p.PadLeftPct ?? 0, p.PadRightPct ?? 0, p.PadTopPct ?? 0, p.PadBottomPct ?? 0,
                    inputs.SourceWidth, inputs.SourceHeight) is (int cw, int ch, int px, int py))
        {
            g[WanA14bI2VWorkflowNodes.PadCanvas] = new EmptyImageLiteralSize { Width = cw, Height = ch, BatchSize = 1, Color = 0xFFFFFF };
            g[WanA14bI2VWorkflowNodes.PadMask] = new InvertMask { Mask = LoadImage.MaskOut(EditNodes.Source) };
            g[WanA14bI2VWorkflowNodes.PadComposite] = new ImageCompositeMasked { Destination = EmptyImageLiteralSize.Out(WanA14bI2VWorkflowNodes.PadCanvas), Source = LoadImage.ImageOut(EditNodes.Source), X = px, Y = py, ResizeSource = false, Mask = InvertMask.Out(WanA14bI2VWorkflowNodes.PadMask) };
            scaleSource = ImageCompositeMasked.Out(WanA14bI2VWorkflowNodes.PadComposite);
        }

        g[WanA14bI2VWorkflowNodes.ScaledSource] = new ImageScaleToTotalPixels { Image = scaleSource, UpscaleMethod = ComfyWidgets.Upscale.Lanczos, Megapixels = budgetMp, ResolutionSteps = BudgetSteps };
        g[WanA14bI2VWorkflowNodes.SourceSize] = new GetImageSize { Image = ImageScaleToTotalPixels.Out(WanA14bI2VWorkflowNodes.ScaledSource) };
        g[WanA14bI2VWorkflowNodes.Positive] = new CLIPTextEncode { Text = inputs.Positive, Clip = clip };
        g[WanA14bI2VWorkflowNodes.Negative] = new CLIPTextEncode { Text = ComfyGraph.ComposeNegative(p.Negative, inputs.Negative), Clip = clip };
        Output<Slot.Conditioning> pos;
        Output<Slot.Conditioning> neg;
        Output<Slot.Latent> lat;
        // First/last-frame conditioning when the caller supplied an END frame (the source is the first frame): swap the
        // plain WanImageToVideo for WanFirstLastFrameToVideo, pinning both ends. The node cover/center-crops BOTH frames
        // to width/height internally; the start frame arrives already at those exact dims (via ScaledSource), so a raw
        // end frame gets a lone crop and lands at a different framing (a same-image loop then stretches instead of
        // holding still). Scale the end frame through the SAME node as the start frame so both reach the node at
        // identical dims. Without an end frame the plain WanImageToVideo path runs. Both nodes re-emit the same 3
        // outputs (positive, negative, latent), so the downstream sampler wiring is shared.
        if (!string.IsNullOrEmpty(inputs.EndImageName))
        {
            g[WanA14bI2VWorkflowNodes.EndFrame] = new LoadImage { Image = inputs.EndImageName };
            Output<Slot.Image> endImage;
            // Pad the END frame the SAME way as the first frame when asked (end_pad_*_pct), so both share one padded
            // canvas. Scale the end image to the source frame size, then composite it into the same white canvas
            // (PadGeom) at the offset. The save gate in the caller guarantees the two frames share an aspect, so the
            // scale here is proportional (no distortion).
            if (PadGeom(p.EndPadLeftPct ?? 0, p.EndPadRightPct ?? 0, p.EndPadTopPct ?? 0, p.EndPadBottomPct ?? 0,
                        inputs.SourceWidth, inputs.SourceHeight) is (int ecw, int ech, int epx, int epy))
            {
                int sw = inputs.SourceWidth, sh = inputs.SourceHeight;
                g[WanA14bI2VWorkflowNodes.EndScale] = new ImageScale { Image = LoadImage.ImageOut(WanA14bI2VWorkflowNodes.EndFrame), UpscaleMethod = ComfyWidgets.Upscale.Lanczos, Width = sw, Height = sh, Crop = ComfyWidgets.Crop.Disabled };
                g[WanA14bI2VWorkflowNodes.EndPadCanvas] = new EmptyImageLiteralSize { Width = ecw, Height = ech, BatchSize = 1, Color = 0xFFFFFF };
                g[WanA14bI2VWorkflowNodes.EndPadComposite] = new ImageCompositeMaskedNoMask { Destination = EmptyImageLiteralSize.Out(WanA14bI2VWorkflowNodes.EndPadCanvas), Source = ImageScale.Out(WanA14bI2VWorkflowNodes.EndScale), X = epx, Y = epy, ResizeSource = false };
                endImage = ImageCompositeMaskedNoMask.Out(WanA14bI2VWorkflowNodes.EndPadComposite);
            }
            else
            {
                // No padding: scale the end frame through the same ImageScaleToTotalPixels as the start frame (:205),
                // to the same pixel budget/rounding, so both frames reach WanFirstLastFrameToVideo at identical dims —
                // a loop (end == start) then produces a clean static loop instead of the node cropping a raw end frame.
                g[WanA14bI2VWorkflowNodes.EndScale] = new ImageScaleToTotalPixels { Image = LoadImage.ImageOut(WanA14bI2VWorkflowNodes.EndFrame), UpscaleMethod = ComfyWidgets.Upscale.Lanczos, Megapixels = budgetMp, ResolutionSteps = BudgetSteps };
                endImage = ImageScaleToTotalPixels.Out(WanA14bI2VWorkflowNodes.EndScale);
            }

            g[WanA14bI2VWorkflowNodes.Cond] = new WanFirstLastFrameToVideo
            {
                Positive = CLIPTextEncode.Out(WanA14bI2VWorkflowNodes.Positive),
                Negative = CLIPTextEncode.Out(WanA14bI2VWorkflowNodes.Negative),
                Vae = vae,
                Width = GetImageSize.WidthOut(WanA14bI2VWorkflowNodes.SourceSize),
                Height = GetImageSize.HeightOut(WanA14bI2VWorkflowNodes.SourceSize),
                Length = len,
                BatchSize = 1,
                StartImage = ImageScaleToTotalPixels.Out(WanA14bI2VWorkflowNodes.ScaledSource),
                EndImage = endImage,
            };
            pos = WanFirstLastFrameToVideo.PositiveOut(WanA14bI2VWorkflowNodes.Cond);
            neg = WanFirstLastFrameToVideo.NegativeOut(WanA14bI2VWorkflowNodes.Cond);
            lat = WanFirstLastFrameToVideo.LatentOut(WanA14bI2VWorkflowNodes.Cond);
        }
        else
        {
            // WanImageToVideo re-emits conditioning + the start latent (3 outputs: positive, negative, latent).
            g[WanA14bI2VWorkflowNodes.Cond] = new WanImageToVideoNoVision
            {
                Positive = CLIPTextEncode.Out(WanA14bI2VWorkflowNodes.Positive),
                Negative = CLIPTextEncode.Out(WanA14bI2VWorkflowNodes.Negative),
                Vae = vae,
                Width = GetImageSize.WidthOut(WanA14bI2VWorkflowNodes.SourceSize),
                Height = GetImageSize.HeightOut(WanA14bI2VWorkflowNodes.SourceSize),
                Length = len,
                BatchSize = 1,
                StartImage = ImageScaleToTotalPixels.Out(WanA14bI2VWorkflowNodes.ScaledSource),
            };
            pos = WanImageToVideoNoVision.PositiveOut(WanA14bI2VWorkflowNodes.Cond);
            neg = WanImageToVideoNoVision.NegativeOut(WanA14bI2VWorkflowNodes.Cond);
            lat = WanImageToVideoNoVision.LatentOut(WanA14bI2VWorkflowNodes.Cond);
        }

        Output<Slot.Latent> outLat = Vid.MoESample(g, mh, ml, pos, neg, lat, p.Steps, p.Boundary, p.CfgHigh, p.CfgLow, sampler, scheduler, p.RefinerSteps, ComfyGraph.Seed(p.Seed));
        g[WanA14bI2VWorkflowNodes.Decode] = new VAEDecode { Samples = outLat, Vae = vae };
        g[WanA14bI2VWorkflowNodes.Save] = new SaveAnimatedWEBPLiteralFps { Images = VAEDecode.Out(WanA14bI2VWorkflowNodes.Decode), FilenamePrefix = OutputPrefixes.Edit, Fps = fps, Lossless = false, Quality = 80, Method = ComfyWidgets.WebpMethod.Default };
        return g;
    }
}