namespace ImageGen.Comfy;

/// <summary>Flux.2 Klein custom-sampler edit pipeline. Multi-image uses the ComfyUI reference_latent method (chain
/// one ReferenceLatent per image, source first). Two models run this (4B and 9B) → two workflow classes over this
/// base.</summary>
public abstract class Flux2KleinEditBase : EditWorkflowBase
{
    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>();
        LoadModel(wf, p, req, inputs, out var model0, out var clip0, out var vae0);
        var seed = ComfyGraph.Seed(p);
        var refNames = inputs.ReferenceImageNames;

        wf["13"] = ComfyGraph.Node("CLIPTextEncode", new { text = inputs.Positive, clip = clip0 });
        wf["11"] = ComfyGraph.Node("ImageScaleToTotalPixels", new { image = ComfyGraph.Ref("10", 0), upscale_method = "lanczos", megapixels = 1.0, resolution_steps = 64 });
        wf["12"] = ComfyGraph.Node("VAEEncode", new { pixels = ComfyGraph.Ref("11", 0), vae = vae0 });
        wf["17"] = ComfyGraph.Node("GetImageSize", new { image = ComfyGraph.Ref("11", 0) });
        wf["14"] = ComfyGraph.Node("FluxGuidance", new { conditioning = ComfyGraph.Ref("13", 0), guidance = p.DblReq("guidance") });
        wf["15"] = ComfyGraph.Node("ReferenceLatent", new { conditioning = ComfyGraph.Ref("14", 0), latent = ComfyGraph.Ref("12", 0) });
        object cond = ComfyGraph.Ref("15", 0);
        int fn = p.Has("reference_max") ? Math.Min(refNames.Count, p.IntReq("reference_max")) : 0;
        for (int i = 0; i < fn; i++)
        {
            string load = $"{40 + i}", scale = $"{50 + i}", enc = $"{60 + i}", rl = $"{70 + i}";
            wf[load] = ComfyGraph.Node("LoadImage", new { image = refNames[i] });
            wf[scale] = ComfyGraph.Node("ImageScaleToTotalPixels", new { image = ComfyGraph.Ref(load, 0), upscale_method = "lanczos", megapixels = 1.0, resolution_steps = 64 });
            wf[enc] = ComfyGraph.Node("VAEEncode", new { pixels = ComfyGraph.Ref(scale, 0), vae = vae0 });
            wf[rl] = ComfyGraph.Node("ReferenceLatent", new { conditioning = cond, latent = ComfyGraph.Ref(enc, 0) });
            cond = ComfyGraph.Ref(rl, 0);
        }
        wf["22"] = ComfyGraph.Node("BasicGuider", new { model = model0, conditioning = cond });
        wf["28"] = ComfyGraph.Node("EmptyFlux2LatentImage", new { width = ComfyGraph.Ref("17", 0), height = ComfyGraph.Ref("17", 1), batch_size = 1 });
        wf["29"] = ComfyGraph.Node("Flux2Scheduler", new { steps = p.IntReq("steps"), width = ComfyGraph.Ref("17", 0), height = ComfyGraph.Ref("17", 1) });
        wf["20"] = ComfyGraph.Node("RandomNoise", new { noise_seed = seed });
        wf["21"] = ComfyGraph.Node("KSamplerSelect", new { sampler_name = ComfyGraph.MapSampler(p.StrReq("sampler")) });
        wf["23"] = ComfyGraph.Node("SamplerCustomAdvanced", new { noise = ComfyGraph.Ref("20", 0), guider = ComfyGraph.Ref("22", 0), sampler = ComfyGraph.Ref("21", 0), sigmas = ComfyGraph.Ref("29", 0), latent_image = ComfyGraph.Ref("28", 0) });
        wf["8"] = ComfyGraph.Node("VAEDecode", new { samples = ComfyGraph.Ref("23", 0), vae = vae0 });
        wf["9"] = ComfyGraph.Node("SaveImage", new { images = ComfyGraph.Ref("8", 0), filename_prefix = "forgemcp_edit" });
        return wf;
    }
}
