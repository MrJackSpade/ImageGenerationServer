//TODO: CHECK FOR FALLBACKS
namespace ImageGen.Comfy;

/// <summary>
/// Pixelizer on DreamOmni2. DreamOmni2 runs its whole diffusion inside the self-contained
/// <c>RunningHub DreamOmni2 Editor</c> node (a quanto-int8 FLUX.1-Kontext pipeline + a VLM), so the
/// per-step projection is done INSIDE that node: it was extended with <c>pixel_art</c> options that
/// project the flow-matching x0 estimate onto the grid+palette every step (same math as
/// <c>PixelManifoldProjection</c>, via PixelHarness <c>quant</c>). A final <c>PixelQuantize</c> renders the
/// authoritative output. <see cref="RequiresModel"/> = false (the pipeline loads its own weights).
/// </summary>
public sealed class DreamOmni2PixelizeWorkflow : EditWorkflowBase
{
    public override string Name => "pixelize-dreamomni2";
    public override bool PreservesComposition => true;
    public override bool RequiresModel => false;
    /// <summary>The editor loads its own int8 weights (no linked checkpoint → no resolved resolution), so the render
    /// snap uses the FLUX.1-Kontext-class envelope (256–1440, /16) it's built on.</summary>
    public override ModelResolution? ResolutionEnvelope => new() { MinW = 256, MinH = 256, MaxW = 1440, MaxH = 1440, Step = 16 };
    public override IReadOnlyList<ParamSpec> Schema => PixelizeSchema.DreamOmniLike("Convert to pixel art, flat colors, clean crisp pixels, limited palette");

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>
        {
            ["10"] = ComfyGraph.Node("LoadImage", new { image = inputs.SourceImageName ?? "" }),
        };
        object refImg;
        var refNames = inputs.ReferenceImageNames;
        if (refNames.Count > 0) { wf["11"] = ComfyGraph.Node("LoadImage", new { image = refNames[0] }); refImg = ComfyGraph.Ref("11", 0); }
        else refImg = ComfyGraph.Ref("10", 0);   // Editor requires a reference; the source doubles as its own.

        var instruction = p.Str("style_prompt");
        if (string.IsNullOrWhiteSpace(instruction)) instruction = inputs.Positive;
        int gw = p.Int("grid_w", 0); if (gw <= 0) gw = 384;
        int gh = p.Int("grid_h", 0); if (gh <= 0) gh = 256;
        var palette = p.Str("palette") ?? "chroma-256";
        int vres = p.Int("virtual_resolution", 256);

        // The config links no checkpoint (the editor loads its own int8 weights), so there's no resolved Resolution.
        // DreamOmni2 is a FLUX.1-Kontext-class pipeline, so snap against the Kontext envelope (256-1440, /16). The
        // render size is fed to the editor as render_width/height, overriding its internal aspect-bucket resize.
        var snap = PixelSnap.Target(p, new ModelResolution { MinW = 256, MinH = 256, MaxW = 1440, MaxH = 1440, Step = 16 }, vres, inputs.SourceWidth, inputs.SourceHeight);

        wf["1"] = ComfyGraph.Node("RunningHub DreamOmni2 Edit Pipeline", new { });
        wf["2"] = ComfyGraph.Node("RunningHub DreamOmni2 Editor", new
        {
            pipeline = ComfyGraph.Ref("1", 0),
            src_image = ComfyGraph.Ref("10", 0),
            ref_image = refImg,
            prompt = instruction,
            num_inference_steps = p.Int("steps", 30),
            guidance_scale = p.Dbl("cfg", 3.5),
            seed = ComfyGraph.Seed(p),
            // per-step pixel-art projection inside the pipeline (the node modification)
            pixel_art = true,
            grid_w = gw,
            grid_h = gh,
            palette,
            proj_method = p.Str("proj_method") ?? "median",
            virtual_resolution = vres,
            w_start = p.Dbl("w_start", 0.5),
            w_end = p.Dbl("w_end", 1.0),
            proj_start = p.Dbl("start_percent", 0.0),
            proj_end = p.Dbl("end_percent", 1.0),
            project_every = p.Int("project_every", 1),
            // 0 when snapping is off / no width+height given -> the node keeps its own aspect-bucket size
            render_width = snap?.w ?? 0,
            render_height = snap?.h ?? 0,
            // reference% -> img2img strength inside the pipeline; 1.0 (reference 0, default) == full generation
            strength = PixelSnap.Denoise(p, 0),
        });
        wf["36"] = PixelizeSchema.FinalQuantize(ComfyGraph.Ref("2", 0), gw, gh, palette, vres, p);
        wf["9"] = ComfyGraph.Node("SaveImage", new { images = ComfyGraph.Ref("36", 0), filename_prefix = "forgemcp_edit" });
        return wf;
    }
}
