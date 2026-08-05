namespace ImageGen.Comfy;

/// <summary>Flux.1 Kontext image edit. Single-image native; multi-image uses the verified ImageStitch method
/// (stitch source+refs into one image, encode as the single reference latent; output stays source-sized).</summary>
public sealed class FluxKontextEditWorkflow : EditWorkflowBase
{
    public override string Name => "flux1-kontext";

    /// <summary>Own nodes (the model/clip/vae/source head is the inherited Nodes). Two FluxKontextImageScale and two
    /// VAEEncode are disambiguated by input: the source vs the stitched source+refs.</summary>
    private const string Positive = "13";
    private const string SourceScale = "11";
    private const string SourceEncode = "12";
    private const string StitchScale = "18";
    private const string StitchEncode = "19";
    private const string RefLatent = "15";
    private const string Guidance = "14";
    private const string NegativeZero = "16";
    private const string Sampler = "3";
    private const string Decode = "8";
    private const string Save = "9";

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        Dictionary<string, object> wf = new Dictionary<string, object>();
        LoadModel(wf, p, req, inputs, out object? model0, out object? clip0, out object? vae0);
        long seed = ComfyGraph.Seed(p);
        IReadOnlyList<string> refNames = inputs.ReferenceImageNames;

        wf[Positive] = ComfyGraph.Node(ComfyNodeTypes.CLIPTextEncode, new { text = inputs.Positive, clip = clip0 });
        wf[SourceScale] = ComfyGraph.Node(ComfyNodeTypes.FluxKontextImageScale, new { image = ComfyGraph.Ref(Nodes.Source, 0) });
        wf[SourceEncode] = ComfyGraph.Node(ComfyNodeTypes.VAEEncode, new { pixels = ComfyGraph.Ref(SourceScale, 0), vae = vae0 });
        int fn = p.Has(WorkflowParamKeys.ReferenceMax) ? Math.Min(refNames.Count, p.IntReq(WorkflowParamKeys.ReferenceMax)) : 0;   // no reference_max declared → this editor takes no refs
        object refLatent;
        if (fn > 0)
        {
            object stitched = ComfyGraph.Ref(Nodes.Source, 0);
            for (int i = 0; i < fn; i++)
            {
                string load = $"{40 + i}", stitch = $"{50 + i}";
                wf[load] = ComfyGraph.Node(ComfyNodeTypes.LoadImage, new { image = refNames[i] });
                wf[stitch] = ComfyGraph.Node(ComfyNodeTypes.ImageStitch, new { image1 = stitched, image2 = ComfyGraph.Ref(load, 0), direction = "right", match_image_size = true, spacing_width = 0, spacing_color = "white" });
                stitched = ComfyGraph.Ref(stitch, 0);
            }
            wf[StitchScale] = ComfyGraph.Node(ComfyNodeTypes.FluxKontextImageScale, new { image = stitched });
            wf[StitchEncode] = ComfyGraph.Node(ComfyNodeTypes.VAEEncode, new { pixels = ComfyGraph.Ref(StitchScale, 0), vae = vae0 });
            refLatent = ComfyGraph.Ref(StitchEncode, 0);
        }
        else refLatent = ComfyGraph.Ref(SourceEncode, 0);
        wf[RefLatent] = ComfyGraph.Node(ComfyNodeTypes.ReferenceLatent, new { conditioning = ComfyGraph.Ref(Positive, 0), latent = refLatent });
        wf[Guidance] = ComfyGraph.Node(ComfyNodeTypes.FluxGuidance, new { conditioning = ComfyGraph.Ref(RefLatent, 0), guidance = p.DblReq(WorkflowParamKeys.Guidance) });
        wf[NegativeZero] = ComfyGraph.Node(ComfyNodeTypes.ConditioningZeroOut, new { conditioning = ComfyGraph.Ref(Positive, 0) });
        wf[Sampler] = ComfyGraph.Node(ComfyNodeTypes.KSampler, new
        {
            seed,
            steps = p.IntReq(WorkflowParamKeys.Steps),
            cfg = p.DblReq(WorkflowParamKeys.Cfg),
            sampler_name = ComfyGraph.MapSampler(p.StrReq(WorkflowParamKeys.Sampler)),
            scheduler = ComfyGraph.MapScheduler(p.StrReq(WorkflowParamKeys.Scheduler)),
            denoise = 1.0,
            model = model0,
            positive = ComfyGraph.Ref(Guidance, 0),
            negative = ComfyGraph.Ref(NegativeZero, 0),
            latent_image = ComfyGraph.Ref(SourceEncode, 0),
        });
        wf[Decode] = ComfyGraph.Node(ComfyNodeTypes.VAEDecode, new { samples = ComfyGraph.Ref(Sampler, 0), vae = vae0 });
        wf[Save] = ComfyGraph.Node(ComfyNodeTypes.SaveImage, new { images = ComfyGraph.Ref(Decode, 0), filename_prefix = "forgemcp_edit" });
        return wf;
    }
}
