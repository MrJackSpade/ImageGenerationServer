namespace ImageGen.Comfy;

/// <summary>
/// Pixelizer on FLUX.2-Klein 4B. Mirrors the Klein custom-sampler edit graph (ReferenceLatent on the
/// source, BasicGuider + SamplerCustomAdvanced over a fresh Flux.2 latent), with the model patched by the
/// per-step <c>PixelManifoldProjection</c> before the guider and a final <c>PixelQuantize</c> render.
/// </summary>
public sealed class Flux2Klein4bPixelizeWorkflow : EditWorkflowBase
{
    public override string Name => "pixelize-klein4b";
    public override bool PreservesComposition => true;
    public override IReadOnlyList<ParamSpec> Schema => PixelizeSchema.KleinLike("Convert to pixel art, flat colors, clean crisp pixels, limited palette");

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
        var snap = PixelSnap.Target(p, req, vres, inputs.SourceWidth, inputs.SourceHeight);   // override the megapixels bucket with the clean k×VRES size when on
        wf["62"] = snap is { } s
            ? PixelHarnessGraph.FixedScale(src, s.w, s.h)
            : ComfyGraph.Node("ImageScaleToTotalPixels", new { image = src, upscale_method = "lanczos", megapixels = p.DblReq("megapixels"), resolution_steps = 64 });
        wf["63"] = ComfyGraph.Node("VAEEncode", new { pixels = ComfyGraph.Ref("62", 0), vae = vae0 });
        wf["64"] = ComfyGraph.Node("GetImageSize", new { image = ComfyGraph.Ref("62", 0) });
        wf["65"] = ComfyGraph.Node("FluxGuidance", new { conditioning = ComfyGraph.Ref("60", 0), guidance = p.DblReq("guidance") });
        wf["66"] = ComfyGraph.Node("ReferenceLatent", new { conditioning = ComfyGraph.Ref("65", 0), latent = ComfyGraph.Ref("63", 0) });

        wf["35"] = PixelizeSchema.Projection(model0, vae0, gw, gh, palette, vres, p);
        wf["22"] = ComfyGraph.Node("BasicGuider", new { model = ComfyGraph.Ref("35", 0), conditioning = ComfyGraph.Ref("66", 0) });
        wf["28"] = ComfyGraph.Node("EmptyFlux2LatentImage", new { width = ComfyGraph.Ref("64", 0), height = ComfyGraph.Ref("64", 1), batch_size = 1 });
        wf["29"] = ComfyGraph.Node("Flux2Scheduler", new { steps = p.IntReq("steps"), width = ComfyGraph.Ref("64", 0), height = ComfyGraph.Ref("64", 1) });
        wf["20"] = ComfyGraph.Node("RandomNoise", new { noise_seed = ComfyGraph.Seed(p) });
        wf["21"] = ComfyGraph.Node("KSamplerSelect", new { sampler_name = ComfyGraph.MapSampler(p.StrReq("sampler")) });
        // reference% -> img2img: 0 generates from the empty latent over the full schedule; >0 inits from the source
        // latent and runs only the denoise tail (SplitSigmasDenoise low_sigmas = denoise fraction of the steps).
        object sigmas, initLatent;
        if (p.IntReq("reference") > 0)
        {
            wf["27"] = ComfyGraph.Node("SplitSigmasDenoise", new { sigmas = ComfyGraph.Ref("29", 0), denoise = PixelSnap.Denoise(p, 0) });
            sigmas = ComfyGraph.Ref("27", 1);        // low_sigmas — the img2img tail
            initLatent = ComfyGraph.Ref("63", 0);    // source latent
        }
        else { sigmas = ComfyGraph.Ref("29", 0); initLatent = ComfyGraph.Ref("28", 0); }
        wf["23"] = ComfyGraph.Node("SamplerCustomAdvanced", new { noise = ComfyGraph.Ref("20", 0), guider = ComfyGraph.Ref("22", 0), sampler = ComfyGraph.Ref("21", 0), sigmas, latent_image = initLatent });
        wf["8"] = ComfyGraph.Node("VAEDecode", new { samples = ComfyGraph.Ref("23", 0), vae = vae0 });
        wf["36"] = PixelizeSchema.FinalQuantize(ComfyGraph.Ref("8", 0), gw, gh, palette, vres, p);
        wf["9"] = ComfyGraph.Node("SaveImage", new { images = ComfyGraph.Ref("36", 0), filename_prefix = "forgemcp_edit" });
        return wf;
    }
}
