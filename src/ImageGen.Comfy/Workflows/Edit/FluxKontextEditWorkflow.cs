namespace ImageGen.Comfy;

/// <summary>Flux.1 Kontext image edit. Single-image native; multi-image uses the verified ImageStitch method
/// (stitch source+refs into one image, encode as the single reference latent; output stays source-sized).</summary>
public sealed class FluxKontextEditWorkflow : EditWorkflowBase
{
    public override string Name => "flux1-kontext";

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>();
        LoadModel(wf, p, req, inputs, out var model0, out var clip0, out var vae0);
        var seed = ComfyGraph.Seed(p);
        var refNames = inputs.ReferenceImageNames;

        wf["13"] = ComfyGraph.Node("CLIPTextEncode", new { text = inputs.Positive, clip = clip0 });
        wf["11"] = ComfyGraph.Node("FluxKontextImageScale", new { image = ComfyGraph.Ref("10", 0) });
        wf["12"] = ComfyGraph.Node("VAEEncode", new { pixels = ComfyGraph.Ref("11", 0), vae = vae0 });
        int fn = p.Has("reference_max") ? Math.Min(refNames.Count, p.IntReq("reference_max")) : 0;   // no reference_max declared → this editor takes no refs
        object refLatent;
        if (fn > 0)
        {
            object stitched = ComfyGraph.Ref("10", 0);
            for (int i = 0; i < fn; i++)
            {
                string load = $"{40 + i}", stitch = $"{50 + i}";
                wf[load] = ComfyGraph.Node("LoadImage", new { image = refNames[i] });
                wf[stitch] = ComfyGraph.Node("ImageStitch", new { image1 = stitched, image2 = ComfyGraph.Ref(load, 0), direction = "right", match_image_size = true, spacing_width = 0, spacing_color = "white" });
                stitched = ComfyGraph.Ref(stitch, 0);
            }
            wf["18"] = ComfyGraph.Node("FluxKontextImageScale", new { image = stitched });
            wf["19"] = ComfyGraph.Node("VAEEncode", new { pixels = ComfyGraph.Ref("18", 0), vae = vae0 });
            refLatent = ComfyGraph.Ref("19", 0);
        }
        else refLatent = ComfyGraph.Ref("12", 0);
        wf["15"] = ComfyGraph.Node("ReferenceLatent", new { conditioning = ComfyGraph.Ref("13", 0), latent = refLatent });
        wf["14"] = ComfyGraph.Node("FluxGuidance", new { conditioning = ComfyGraph.Ref("15", 0), guidance = p.DblReq("guidance") });
        wf["16"] = ComfyGraph.Node("ConditioningZeroOut", new { conditioning = ComfyGraph.Ref("13", 0) });
        wf["3"] = ComfyGraph.Node("KSampler", new
        {
            seed,
            steps = p.IntReq("steps"),
            cfg = p.DblReq("cfg"),
            sampler_name = ComfyGraph.MapSampler(p.StrReq("sampler")),
            scheduler = ComfyGraph.MapScheduler(p.StrReq("scheduler")),
            denoise = 1.0,
            model = model0,
            positive = ComfyGraph.Ref("14", 0),
            negative = ComfyGraph.Ref("16", 0),
            latent_image = ComfyGraph.Ref("12", 0),
        });
        wf["8"] = ComfyGraph.Node("VAEDecode", new { samples = ComfyGraph.Ref("3", 0), vae = vae0 });
        wf["9"] = ComfyGraph.Node("SaveImage", new { images = ComfyGraph.Ref("8", 0), filename_prefix = "forgemcp_edit" });
        return wf;
    }
}
