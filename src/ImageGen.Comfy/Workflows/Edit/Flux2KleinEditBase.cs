namespace ImageGen.Comfy;

/// <summary>Flux.2 Klein custom-sampler edit pipeline. Multi-image uses the ComfyUI reference_latent method (chain
/// one ReferenceLatent per image, source first). Two models run this (4B and 9B) → two workflow classes over this
/// base.</summary>
public abstract class Flux2KleinEditBase : EditWorkflowBase
{
    /// <summary>This base's own node ids.</summary>
    private const string Positive = "13";
    private const string ScaledSource = "11";
    private const string Encode = "12";
    private const string SourceSize = "17";
    private const string Guidance = "14";
    private const string ReferenceLatent = "15";
    private const string Guider = "22";
    private const string EmptyLatent = "28";
    private const string Scheduler = "29";
    private const string Noise = "20";
    private const string SamplerSelect = "21";
    private const string Sampler = "23";
    private const string Decode = "8";
    private const string Save = "9";

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>();
        LoadModel(wf, p, req, inputs, out var model0, out var clip0, out var vae0);
        var seed = ComfyGraph.Seed(p);
        var refNames = inputs.ReferenceImageNames;

        wf[Positive] = ComfyGraph.Node(ComfyNodeTypes.CLIPTextEncode, new { text = inputs.Positive, clip = clip0 });
        wf[ScaledSource] = ComfyGraph.Node(ComfyNodeTypes.ImageScaleToTotalPixels, new { image = ComfyGraph.Ref(Nodes.Source, 0), upscale_method = "lanczos", megapixels = 1.0, resolution_steps = 64 });
        wf[Encode] = ComfyGraph.Node(ComfyNodeTypes.VAEEncode, new { pixels = ComfyGraph.Ref(ScaledSource, 0), vae = vae0 });
        wf[SourceSize] = ComfyGraph.Node(ComfyNodeTypes.GetImageSize, new { image = ComfyGraph.Ref(ScaledSource, 0) });
        wf[Guidance] = ComfyGraph.Node(ComfyNodeTypes.FluxGuidance, new { conditioning = ComfyGraph.Ref(Positive, 0), guidance = p.DblReq(WorkflowParamKeys.Guidance) });
        wf[ReferenceLatent] = ComfyGraph.Node(ComfyNodeTypes.ReferenceLatent, new { conditioning = ComfyGraph.Ref(Guidance, 0), latent = ComfyGraph.Ref(Encode, 0) });
        object cond = ComfyGraph.Ref(ReferenceLatent, 0);
        int fn = p.Has(WorkflowParamKeys.ReferenceMax) ? Math.Min(refNames.Count, p.IntReq(WorkflowParamKeys.ReferenceMax)) : 0;
        for (int i = 0; i < fn; i++)
        {
            string load = $"{40 + i}", scale = $"{50 + i}", enc = $"{60 + i}", rl = $"{70 + i}";
            wf[load] = ComfyGraph.Node(ComfyNodeTypes.LoadImage, new { image = refNames[i] });
            wf[scale] = ComfyGraph.Node(ComfyNodeTypes.ImageScaleToTotalPixels, new { image = ComfyGraph.Ref(load, 0), upscale_method = "lanczos", megapixels = 1.0, resolution_steps = 64 });
            wf[enc] = ComfyGraph.Node(ComfyNodeTypes.VAEEncode, new { pixels = ComfyGraph.Ref(scale, 0), vae = vae0 });
            wf[rl] = ComfyGraph.Node(ComfyNodeTypes.ReferenceLatent, new { conditioning = cond, latent = ComfyGraph.Ref(enc, 0) });
            cond = ComfyGraph.Ref(rl, 0);
        }
        wf[Guider] = ComfyGraph.Node(ComfyNodeTypes.BasicGuider, new { model = model0, conditioning = cond });
        wf[EmptyLatent] = ComfyGraph.Node(ComfyNodeTypes.EmptyFlux2LatentImage, new { width = ComfyGraph.Ref(SourceSize, 0), height = ComfyGraph.Ref(SourceSize, 1), batch_size = 1 });
        wf[Scheduler] = ComfyGraph.Node(ComfyNodeTypes.Flux2Scheduler, new { steps = p.IntReq(WorkflowParamKeys.Steps), width = ComfyGraph.Ref(SourceSize, 0), height = ComfyGraph.Ref(SourceSize, 1) });
        wf[Noise] = ComfyGraph.Node(ComfyNodeTypes.RandomNoise, new { noise_seed = seed });
        wf[SamplerSelect] = ComfyGraph.Node(ComfyNodeTypes.KSamplerSelect, new { sampler_name = ComfyGraph.MapSampler(p.StrReq(WorkflowParamKeys.Sampler)) });
        wf[Sampler] = ComfyGraph.Node(ComfyNodeTypes.SamplerCustomAdvanced, new { noise = ComfyGraph.Ref(Noise, 0), guider = ComfyGraph.Ref(Guider, 0), sampler = ComfyGraph.Ref(SamplerSelect, 0), sigmas = ComfyGraph.Ref(Scheduler, 0), latent_image = ComfyGraph.Ref(EmptyLatent, 0) });
        wf[Decode] = ComfyGraph.Node(ComfyNodeTypes.VAEDecode, new { samples = ComfyGraph.Ref(Sampler, 0), vae = vae0 });
        wf[Save] = ComfyGraph.Node(ComfyNodeTypes.SaveImage, new { images = ComfyGraph.Ref(Decode, 0), filename_prefix = "forgemcp_edit" });
        return wf;
    }
}
