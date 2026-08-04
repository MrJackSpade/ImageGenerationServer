//TODO: CHECK FOR FALLBACKS
namespace ImageGen.Comfy;

/// <summary>
/// Pixelizer on FLUX.1-Kontext. Mirrors the Kontext edit graph (CLIP encode → ReferenceLatent on the
/// source's encoded latent → FluxGuidance), but patches the model with the per-step
/// <c>PixelManifoldProjection</c> so every denoise step clamps the x0 estimate onto a fixed grid+palette,
/// and renders the authoritative output with a final <c>PixelQuantize</c>. Virtual resolution sets the
/// sprite's pixel count independent of Kontext's render bucket.
/// </summary>
public sealed class FluxKontextPixelizeWorkflow : EditWorkflowBase
{
    public override string Name => "pixelize-kontext";
    public override bool PreservesComposition => true;
    public override IReadOnlyList<ParamSpec> Schema => PixelizeSchema.KontextLike("Convert to pixel art, flat colors, clean crisp pixels, limited palette");

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>();
        LoadModel(wf, p, req, inputs, out var model0, out var clip0, out var vae0);   // 4/5/6 + LoadImage 10
        var src = PixelHarnessGraph.FlattenOnWhite(wf);                               // flatten alpha onto white (11-14)

        var instruction = p.Str("style_prompt");
        if (string.IsNullOrWhiteSpace(instruction)) instruction = inputs.Positive;
        int gw = p.IntReq("grid_w");
        int gh = p.IntReq("grid_h");
        var palette = p.StrReq("palette");
        int vres = p.IntReq("virtual_resolution");

        wf["60"] = ComfyGraph.Node("CLIPTextEncode", new { text = instruction, clip = clip0 });
        var snap = PixelSnap.Target(p, req, vres, inputs.SourceWidth, inputs.SourceHeight);   // override the Kontext bucket with the clean k×VRES size when on
        wf["62"] = snap is { } s
            ? PixelHarnessGraph.FixedScale(src, s.w, s.h)
            : ComfyGraph.Node("FluxKontextImageScale", new { image = src });
        wf["63"] = ComfyGraph.Node("VAEEncode", new { pixels = ComfyGraph.Ref("62", 0), vae = vae0 });
        wf["64"] = ComfyGraph.Node("ReferenceLatent", new { conditioning = ComfyGraph.Ref("60", 0), latent = ComfyGraph.Ref("63", 0) });
        wf["65"] = ComfyGraph.Node("FluxGuidance", new { conditioning = ComfyGraph.Ref("64", 0), guidance = p.DblReq("guidance") });
        wf["66"] = ComfyGraph.Node("ConditioningZeroOut", new { conditioning = ComfyGraph.Ref("60", 0) });

        wf["35"] = PixelizeSchema.Projection(model0, vae0, gw, gh, palette, vres, p);
        wf["3"] = ComfyGraph.Node("KSampler", new
        {
            seed = ComfyGraph.Seed(p),
            steps = p.IntReq("steps"),
            cfg = p.DblReq("cfg"),
            sampler_name = ComfyGraph.MapSampler(p.StrReq("sampler")),
            scheduler = ComfyGraph.MapScheduler(p.StrReq("scheduler")),
            denoise = PixelSnap.Denoise(p, 0),   // reference% -> denoise; 0 (default) == 1.0 == regenerate from the source ref
            model = ComfyGraph.Ref("35", 0),
            positive = ComfyGraph.Ref("65", 0),
            negative = ComfyGraph.Ref("66", 0),
            latent_image = ComfyGraph.Ref("63", 0),
        });
        wf["8"] = ComfyGraph.Node("VAEDecode", new { samples = ComfyGraph.Ref("3", 0), vae = vae0 });
        wf["36"] = PixelizeSchema.FinalQuantize(ComfyGraph.Ref("8", 0), gw, gh, palette, vres, p);
        wf["9"] = ComfyGraph.Node("SaveImage", new { images = ComfyGraph.Ref("36", 0), filename_prefix = "forgemcp_edit" });
        return wf;
    }
}
