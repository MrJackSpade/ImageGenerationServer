using ImageGen.Application.Rendering;

namespace ImageGen.Comfy;

/// <summary>
/// Diffusion pixelizer — the quality half. Img2img at a partial denoise where the model (Flux-dev by default; the
/// neutral high-fidelity denoiser, NOT an authoring model) is patched with the per-step PixelManifoldProjection so
/// every denoise step clamps the x0 estimate onto a fixed grid+palette. The projection does the pixel-art
/// structuring; the model only supplies local coherence — hence low strength + a strong w-ramp. A final PixelQuantize
/// renders the authoritative crisp output so VAE noise never reaches it.
///
/// One class, many configs: any latent img2img model (Flux/SDXL-style) binds here via requirements+params. Qwen
/// Image Edit, which needs its own edit conditioning, will be a sibling class that inserts the same projection patch.
/// The sampler runs at grid*block so the decoded image and the projection target share a resolution. API-only.
/// </summary>
public sealed class PixelizeWorkflow : EditWorkflowBase
{
    public override string Name => "pixelize";
    /// <summary>Restyle to grid+palette — exempt from the no-change gate.</summary>
    public override bool PreservesComposition => true;
    public override IReadOnlyList<ParamSpec> Schema => PixelizeSchema;

    private static readonly IReadOnlyList<ParamSpec> PixelizeSchema = new ParamSpec[]
    {
        // model loading (consumed by EditWorkflowBase.LoadModel)
        new() { Key = LoaderKinds.ParamKey, Type = ParamType.Enum, Choices = LoaderKinds.Choices },
        // No default. A GENERIC workflow cannot know which CLIP family a configuration is for; a "flux"
        // default would be silently wrong for any configuration that omits it -- pixelize-hidream would
        // inherit it and hand CLIPLoader a type it does not accept. An omission must surface, not be guessed.
        new() { Key = "clip_type", Type = ParamType.String },
        new() { Key = "dual",      Type = ParamType.Bool },
        // sampling
        new() { Key = "steps",     Type = ParamType.Int,    Min = 1, Max = 100, Label = "Steps" },
        new() { Key = "cfg",       Type = ParamType.Double, Min = 1, Max = 30, Label = "CFG scale" },
        new() { Key = "guidance",  Type = ParamType.Double },   // Flux distilled guidance (omit the node for non-flux)
        new() { Key = "sampler",   Type = ParamType.String },
        new() { Key = "scheduler", Type = ParamType.String },
        // The harness uses a FIXED style prompt for the correction pass (not the edit instruction). Blank it to
        // fall back to the caller's instruction.
        new() { Key = "style_prompt", Type = ParamType.String, Label = "Style prompt" },
        // Low strength: the projection leads, the model only cleans up. The harness Flux default is 0.3.
        new() { Key = "reference",  Type = ParamType.Int, Min = 0, Max = 100, Label = "Reference %", Help = "0 = generate fresh · 100 = copy the source" },
        // Virtual resolution = the sprite's pixel count on its longest edge; the grid is derived from the INPUT
        // aspect so output aspect == input aspect. 0 = use explicit grid_w/grid_h. The sampler runs at `megapixels`
        // (aspect-preserving), NOT a fixed grid*block, so a portrait input no longer gets squashed to 3:2.
        new() { Key = "virtual_resolution", Type = ParamType.Int, Min = 0, Max = 4096, Label = "Virtual res", Help = "Sprite pixel count on its longest edge" },
        new() { Key = "grid_w",      Type = ParamType.Int,    Min = 0, Max = 4096 },
        new() { Key = "grid_h",      Type = ParamType.Int,    Min = 0, Max = 4096 },
        new() { Key = "megapixels",  Type = ParamType.Double, Min = 0.1, Max = 4.0, Label = "Megapixels", Help = "Working resolution area, aspect preserved (ignored when Snap res is on)" },
        // When snap_resolution is on AND width+height are given, the render res is snapped to a clean integer
        // multiple of VRES (exact k×k cells) within the model's range, overriding the megapixels sizing above.
        new() { Key = "width",           Type = ParamType.Int,  Min = 0, Max = 4096, Label = "Render width", Help = "Explicit render width; 0 = model default" },
        new() { Key = "height",          Type = ParamType.Int,  Min = 0, Max = 4096, Label = "Render height", Help = "Explicit render height; 0 = model default" },
        new() { Key = "snap_resolution", Type = ParamType.Bool, Label = "Snap res", Help = "Override the render size to a clean integer multiple of VRES" },
        new() { Key = "out_scale",   Type = ParamType.Int,    Min = 1, Max = 16, Label = "Output upscale" },
        new() { Key = "palette",     Type = ParamType.Enum, Choices = PixelPalettes.Choices, Label = "Palette" },
        new() { Key = "proj_method", Type = ParamType.Enum,   Choices = new[] { "median", "mode", "box", "nearest_present", "mean_srgb", "mean_linear", "mean_oklab", "lanczos", "var_hybrid", "supersample_mode" }, Label = "Projection", Help = "Per-step projection method (median = crisp + straight edges)" },
        new() { Key = "final_method",Type = ParamType.Enum,   Choices = new[] { "median", "mode", "box", "nearest_present", "mean_srgb", "mean_linear", "mean_oklab", "lanczos", "var_hybrid", "supersample_mode" }, Label = "Cell method", Help = "Final-render cell method (median = crisp + straight; box = smoother)" },
        // projection weight ramp (over log-sigma progress) + the window it applies in
        new() { Key = "w_start",       Type = ParamType.Double, Min = 0.0, Max = 1.0 },
        new() { Key = "w_end",         Type = ParamType.Double, Min = 0.0, Max = 1.0 },
        new() { Key = "start_percent", Type = ParamType.Double, Min = 0.0, Max = 1.0 },
        new() { Key = "end_percent",   Type = ParamType.Double, Min = 0.0, Max = 1.0 },
        new() { Key = "project_every", Type = ParamType.Int,    Min = 1, Max = 8 },
    };

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>();
        LoadModel(wf, p, req, inputs, out var model0, out var clip0, out var vae0);   // nodes 4/5/6 + LoadImage "10"
        var src = PixelHarnessGraph.FlattenOnWhite(wf);                               // flatten alpha onto white (nodes 11-14)

        int gw = p.IntReq("grid_w");
        int gh = p.IntReq("grid_h");
        int vres = p.IntReq("virtual_resolution");
        var palette = p.StrReq("palette");

        // source image -> working resolution -> init latent. Default: preserve input aspect at a megapixel area
        // (snapped /16). When snapping is on, override with the clean k×VRES render size instead.
        var snap = PixelSnap.Target(p, req, vres, inputs.SourceWidth, inputs.SourceHeight);
        wf["30"] = snap is { } s
            ? PixelHarnessGraph.FixedScale(src, s.w, s.h)
            : ComfyGraph.Node("ImageScaleToTotalPixels", new { image = src, upscale_method = "lanczos", megapixels = p.DblReq("megapixels"), resolution_steps = 16 });
        wf["31"] = ComfyGraph.Node("VAEEncode", new { pixels = ComfyGraph.Ref("30", 0), vae = vae0 });

        // conditioning: the harness's fixed style prompt (or the caller's instruction if it's blanked),
        // optional Flux guidance, empty negative (cfg 1 ignores it)
        var prompt = p.Str("style_prompt");
        if (string.IsNullOrWhiteSpace(prompt)) prompt = inputs.Positive;
        wf["32"] = ComfyGraph.Node("CLIPTextEncode", new { text = prompt, clip = clip0 });
        object posSrc = ComfyGraph.Ref("32", 0);
        if (p.DblOrNull("guidance") is double g)
        {
            wf["33"] = ComfyGraph.Node("FluxGuidance", new { conditioning = ComfyGraph.Ref("32", 0), guidance = g });
            posSrc = ComfyGraph.Ref("33", 0);
        }
        wf["37"] = ComfyGraph.Node("CLIPTextEncode", new { text = "", clip = clip0 });

        // patch the model with the per-step pixel-manifold projection (the diffusion pixelizer)
        wf["35"] = ComfyGraph.Node("PixelManifoldProjection", new
        {
            model = model0,
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
            denoise = PixelSnap.Denoise(p, 70),   // reference% -> denoise (default 70 → denoise 0.3)
            model = ComfyGraph.Ref("35", 0),
            positive = posSrc,
            negative = ComfyGraph.Ref("37", 0),
            latent_image = ComfyGraph.Ref("31", 0),
        });
        wf["8"] = ComfyGraph.Node("VAEDecode", new { samples = ComfyGraph.Ref("3", 0), vae = vae0 });
        // authoritative final render — quantize the decode so VAE noise never reaches the output
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
