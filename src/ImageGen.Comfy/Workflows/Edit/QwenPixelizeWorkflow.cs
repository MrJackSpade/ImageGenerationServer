//TODO: CHECK FOR FALLBACKS
using ImageGen.Application.Rendering;

namespace ImageGen.Comfy;

/// <summary>
/// Diffusion pixelizer on Qwen-Image-Edit — generate pixel art DIRECTLY from a reference image. QIE's instruction
/// edit (TextEncodeQwenImageEditPlus with the reference as image1 + ReferenceLatent) redraws the image per the
/// instruction while the per-step PixelManifoldProjection clamps the x0 estimate onto the pixel grid+palette every
/// step — so the model and the projection co-operate: QIE produces manifold-friendly structure instead of fighting
/// an already-finished image. Generates at full denoise (QIE-style), not a partial img2img.
///
/// Uses VIRTUAL RESOLUTION (longest-edge virtual-pixel count) by default, so the sprite's pixel count is set
/// independently of whatever bucket QIE renders at. Final PixelQuantize renders the authoritative output. API-only.
/// </summary>
public sealed class QwenPixelizeWorkflow : EditWorkflowBase
{
    public override string Name => "pixelize-qwen";
    public override bool PreservesComposition => true;
    public override IReadOnlyList<ParamSpec> Schema => QwenPixelizeSchema;

    private static readonly IReadOnlyList<ParamSpec> QwenPixelizeSchema = new ParamSpec[]
    {
        new() { Key = "loader",    Type = ParamType.Enum,   Choices = new[] { "checkpoint", "unet", "unet_gguf" }, Default = "unet" },
        new() { Key = "clip_type", Type = ParamType.String, Default = "qwen_image" },
        new() { Key = "dual",      Type = ParamType.Bool,   Default = false },
        new() { Key = "steps",     Type = ParamType.Int,    Default = 20, Min = 1, Max = 100, Label = "Steps" },
        new() { Key = "cfg",       Type = ParamType.Double, Default = 4.0, Min = 1, Max = 30, Label = "CFG scale" },
        new() { Key = "sampler",   Type = ParamType.String, Default = "euler" },
        new() { Key = "scheduler", Type = ParamType.String, Default = "simple" },
        new() { Key = "shift",     Type = ParamType.Double, Default = 3.1 },   // ModelSamplingAuraFlow shift (2511)
        new() { Key = "style_prompt", Type = ParamType.String, Default = "Convert to pixel art, flat colors, clean crisp pixels, limited palette", Label = "Instruction" },
        // false (default) = GENERATE a new on-character design from the reference (semantic/vision guidance only,
        // empty init) — varies by seed. true = faithful edit-in-place (inject source latent, init from it) = pixelize
        // the same image every time.
        new() { Key = "reference", Type = ParamType.Int, Default = 0, Min = 0, Max = 100, Label = "Reference %", Help = "0 = generate fresh · 100 = copy the source" },
        // Virtual resolution = the sprite's pixel count on its longest edge (aspect preserved), independent of the
        // model's render bucket. 0 = use explicit grid_w/grid_h instead.
        new() { Key = "virtual_resolution", Type = ParamType.Int, Default = 256, Min = 0, Max = 4096, Label = "Virtual res", Help = "Sprite pixel count on its longest edge" },
        new() { Key = "grid_w",    Type = ParamType.Int, Min = 0, Max = 4096 },
        new() { Key = "grid_h",    Type = ParamType.Int, Min = 0, Max = 4096 },
        // Snap the render res to a clean integer multiple of VRES (exact k×k cells) within the model's range,
        // overriding the FluxKontextImageScale bucket. Needs width+height (the requested fixed aspect).
        new() { Key = "width",           Type = ParamType.Int,  Default = 0, Min = 0, Max = 4096, Label = "Render width", Help = "Explicit render width; 0 = model default" },
        new() { Key = "height",          Type = ParamType.Int,  Default = 0, Min = 0, Max = 4096, Label = "Render height", Help = "Explicit render height; 0 = model default" },
        new() { Key = "snap_resolution", Type = ParamType.Bool, Default = true, Label = "Snap res", Help = "Override the render size to a clean integer multiple of VRES" },
        new() { Key = "out_scale", Type = ParamType.Int, Default = 3, Min = 1, Max = 16, Label = "Output upscale" },
        new() { Key = "palette",   Type = ParamType.Enum, Choices = PixelPalettes.Choices, Default = "adaptive", Label = "Palette" },
        new() { Key = "proj_method",  Type = ParamType.Enum, Choices = new[] { "median", "mode", "box", "nearest_present", "mean_srgb", "mean_linear", "mean_oklab", "lanczos", "var_hybrid", "supersample_mode" }, Default = "median", Label = "Projection", Help = "Per-step projection method (median = crisp + straight edges)" },
        new() { Key = "final_method", Type = ParamType.Enum, Choices = new[] { "median", "mode", "box", "nearest_present", "mean_srgb", "mean_linear", "mean_oklab", "lanczos", "var_hybrid", "supersample_mode" }, Default = "median", Label = "Cell method", Help = "Final-render cell method (median = crisp + straight; box = smoother)" },
        new() { Key = "w_start",       Type = ParamType.Double, Default = 0.5, Min = 0.0, Max = 1.0 },
        new() { Key = "w_end",         Type = ParamType.Double, Default = 1.0, Min = 0.0, Max = 1.0 },
        new() { Key = "start_percent", Type = ParamType.Double, Default = 0.0, Min = 0.0, Max = 1.0 },
        new() { Key = "end_percent",   Type = ParamType.Double, Default = 1.0, Min = 0.0, Max = 1.0 },
        new() { Key = "project_every", Type = ParamType.Int,    Default = 1, Min = 1, Max = 8 },
    };

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>();
        LoadModel(wf, p, req, inputs, out var model0, out var clip0, out var vae0);   // model/clip/vae + LoadImage "10"
        var src = PixelHarnessGraph.FlattenOnWhite(wf);                               // flatten alpha onto white (11-14)

        var instruction = p.Str("style_prompt");
        if (string.IsNullOrWhiteSpace(instruction)) instruction = inputs.Positive;

        int gw = p.IntReq("grid_w");
        int gh = p.IntReq("grid_h");
        var palette = p.StrReq("palette");
        int vres = p.IntReq("virtual_resolution");

        // The source enters as a SEMANTIC guide through Qwen's vision encoder (image1). The `reference` % knob sets
        // how much the output references the source pixels: 0 = no reference (empty init latent, no ReferenceLatent →
        // QIE GENERATES a new design each seed); >0 = inject the source latent + ReferenceLatent and img2img it at
        // denoise = 1 - reference/100 (100 ≈ copy). When snapping is on, the sprite renders at the clean k×VRES size.
        var snap = PixelSnap.Target(p, req, vres, inputs.SourceWidth, inputs.SourceHeight);
        bool useRef = p.IntReq("reference") > 0;
        wf["20"] = ComfyGraph.Node("FluxKontextImageScale", new { image = src });
        wf["22"] = ComfyGraph.Node("TextEncodeQwenImageEditPlus", new { clip = clip0, image1 = ComfyGraph.Ref("20", 0), prompt = instruction });
        object cond, initLatent;
        if (useRef)
        {
            // source-referenced img2img: init from the source latent (snapped to the clean size if enabled). The
            // FixedScale must be its OWN node (25) and referenced — passing the node dict inline as VAEEncode's
            // `pixels` input hands the encoder a dict instead of an image ('dict' has no attribute 'shape').
            object srcPixels;
            if (snap is { } sa) { wf["25"] = PixelHarnessGraph.FixedScale(src, sa.w, sa.h); srcPixels = ComfyGraph.Ref("25", 0); }
            else srcPixels = ComfyGraph.Ref("20", 0);
            wf["21"] = ComfyGraph.Node("VAEEncode", new { pixels = srcPixels, vae = vae0 });
            wf["24"] = ComfyGraph.Node("ReferenceLatent", new { conditioning = ComfyGraph.Ref("22", 0), latent = ComfyGraph.Ref("21", 0) });
            cond = ComfyGraph.Ref("24", 0);
            initLatent = ComfyGraph.Ref("21", 0);
        }
        else
        {
            if (snap is { } sl)
                wf["41"] = ComfyGraph.Node("EmptySD3LatentImage", new { width = sl.w, height = sl.h, batch_size = 1 });
            else
            {
                wf["40"] = ComfyGraph.Node("GetImageSize", new { image = ComfyGraph.Ref("20", 0) });
                wf["41"] = ComfyGraph.Node("EmptySD3LatentImage", new { width = ComfyGraph.Ref("40", 0), height = ComfyGraph.Ref("40", 1), batch_size = 1 });
            }
            cond = ComfyGraph.Ref("22", 0);
            initLatent = ComfyGraph.Ref("41", 0);
        }
        wf["26"] = ComfyGraph.Node("ConditioningZeroOut", new { conditioning = cond });

        // Qwen 2511 sampling fix (ModelSamplingAuraFlow + CFGNorm), then patch with the per-step projection.
        wf["2"] = ComfyGraph.Node("ModelSamplingAuraFlow", new { model = model0, shift = p.DblReq("shift") });
        wf["7"] = ComfyGraph.Node("CFGNorm", new { model = ComfyGraph.Ref("2", 0), strength = 1.0 });
        wf["35"] = ComfyGraph.Node("PixelManifoldProjection", new
        {
            model = ComfyGraph.Ref("7", 0),
            vae = vae0,
            grid_w = gw,
            grid_h = gh,
            palette,
            method = p.StrReq("proj_method"),
            w_start = p.DblReq("w_start"),
            w_end = p.DblReq("w_end"),
            start_percent = p.DblReq("start_percent"),
            end_percent = p.DblReq("end_percent"),
            project_every = p.IntReq("project_every"),
            virtual_resolution = vres,
        });

        wf["3"] = ComfyGraph.Node("KSampler", new
        {
            seed = ComfyGraph.Seed(p),
            steps = p.IntReq("steps"),
            cfg = p.DblReq("cfg"),
            sampler_name = ComfyGraph.MapSampler(p.StrReq("sampler")),
            scheduler = ComfyGraph.MapScheduler(p.StrReq("scheduler")),
            denoise = PixelSnap.Denoise(p, 0),   // reference% -> denoise; 0 (default) == 1.0 == generate fresh
            model = ComfyGraph.Ref("35", 0),
            positive = cond,
            negative = ComfyGraph.Ref("26", 0),
            latent_image = initLatent,
        });
        wf["8"] = ComfyGraph.Node("VAEDecode", new { samples = ComfyGraph.Ref("3", 0), vae = vae0 });
        wf["36"] = ComfyGraph.Node("PixelQuantize", new
        {
            image = ComfyGraph.Ref("8", 0),
            grid_w = gw,
            grid_h = gh,
            palette,
            method = p.StrReq("final_method"),
            virtual_resolution = vres,
        });
        wf["9"] = ComfyGraph.Node("SaveImage", new { images = ComfyGraph.Ref("36", 0), filename_prefix = "forgemcp_edit" });
        return wf;
    }
}
