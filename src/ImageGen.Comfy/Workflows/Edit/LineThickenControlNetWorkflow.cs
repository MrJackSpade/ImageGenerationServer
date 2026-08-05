namespace ImageGen.Comfy;

/// <summary>
/// ControlNet line-art re-render. A lineart preprocessor (<c>LineArtPreprocessor</c>,
/// comfyui_controlnet_aux; <c>coarse=enable</c> yields bolder lines) drives an SD1.5 lineart
/// ControlNet over an anime checkpoint, re-rendering the source as img2img at partial denoise so the
/// character is preserved while the outlines are redrawn clean and bold. Checkpoint + ControlNet are
/// resolved from the configuration's requirements (the files must be present on disk).
/// </summary>
public sealed class LineThickenControlNetWorkflow : EditWorkflowBase
{
    public override string Name => "line-thicken-controlnet";
    public override bool PreservesComposition => true;
    public override IReadOnlyList<ParamSpec> Schema => ControlNetSchema;

    private static readonly IReadOnlyList<ParamSpec> ControlNetSchema = new ParamSpec[]
    {
        new() { Key = LoaderKinds.ParamKey, Type = ParamType.Enum, Choices = LoaderKinds.Choices },
        new() { Key = WorkflowParamKeys.Steps,      Type = ParamType.Int,    Min = 1,    Max = 100, Label = "Steps" },
        new() { Key = WorkflowParamKeys.Cfg,        Type = ParamType.Double, Min = 1,    Max = 30,  Label = "CFG scale" },
        new() { Key = WorkflowParamKeys.Sampler,    Type = ParamType.String },
        new() { Key = WorkflowParamKeys.Scheduler,  Type = ParamType.String },
        new() { Key = WorkflowParamKeys.Denoise,    Type = ParamType.Double, Min = 0.1,  Max = 1.0, Label = "Redraw amount" },
        new() { Key = WorkflowParamKeys.StylePrompt, Type = ParamType.String, Label = "Style prompt" },
        new() { Key = WorkflowParamKeys.Negative,   Type = ParamType.String },
        new() { Key = WorkflowParamKeys.Coarse,     Type = ParamType.Enum,   Choices = new[] { "enable", "disable" }, Label = "Coarse (bolder) lineart" },
        new() { Key = WorkflowParamKeys.ControlnetStrength, Type = ParamType.Double, Min = 0.0, Max = 2.0, Label = "ControlNet strength" },
        new() { Key = WorkflowParamKeys.Resolution, Type = ParamType.Int,    Min = 256,  Max = 2048, Label = "Lineart resolution" },
    };

    /// <summary>This workflow's own node ids.</summary>
    private const string Lineart = "50";
    private const string ControlNet = "51";
    private const string ControlNetApply = "52";
    private const string Positive = "60";
    private const string Negative = "61";
    private const string Encode = "31";
    private const string Sampler = "3";
    private const string Decode = "8";
    private const string Save = "9";

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        Dictionary<string, object> wf = new Dictionary<string, object>();
        LoadModel(wf, p, req, inputs, out object? model0, out object? clip0, out object? vae0);   // CheckpointLoaderSimple (4) + LoadImage (10)
        object src = PixelHarnessGraph.FlattenOnWhite(wf);                               // flatten alpha onto white (11-14)

        string? prompt = p.Str(WorkflowParamKeys.StylePrompt);
        if (string.IsNullOrWhiteSpace(prompt)) prompt = inputs.Positive;
        string neg = p.Str(WorkflowParamKeys.Negative) ?? "";   // the model's own documented negative from the config JSON, or none — no shared baseline
        // 60/61, not 12/13 — FlattenOnWhite already owns 11-14 (EmptyImage 12, InvertMask 13, composite 14).
        wf[Positive] = ComfyGraph.Node(ComfyNodeTypes.CLIPTextEncode, new { text = prompt, clip = clip0 });
        wf[Negative] = ComfyGraph.Node(ComfyNodeTypes.CLIPTextEncode, new { text = neg, clip = clip0 });

        // lineart control image (white-on-black, as the lineart ControlNet expects), then apply the ControlNet.
        wf[Lineart] = ComfyGraph.Node(ComfyNodeTypes.LineArtPreprocessor, new { image = src, coarse = p.StrReq(WorkflowParamKeys.Coarse), resolution = p.IntReq(WorkflowParamKeys.Resolution) });
        wf[ControlNet] = ComfyGraph.Node(ComfyNodeTypes.ControlNetLoader, new { control_net_name = req.RequiredControlNet() });
        wf[ControlNetApply] = ComfyGraph.Node(ComfyNodeTypes.ControlNetApplyAdvanced, new
        {
            positive = ComfyGraph.Ref(Positive, 0),
            negative = ComfyGraph.Ref(Negative, 0),
            control_net = ComfyGraph.Ref(ControlNet, 0),
            image = ComfyGraph.Ref(Lineart, 0),
            strength = p.DblReq(WorkflowParamKeys.ControlnetStrength),
            start_percent = 0.0,
            end_percent = 1.0,
            vae = vae0,
        });

        // img2img from the source so the character is preserved; the ControlNet enforces the bold lineart.
        wf[Encode] = ComfyGraph.Node(ComfyNodeTypes.VAEEncode, new { pixels = src, vae = vae0 });
        wf[Sampler] = ComfyGraph.Node(ComfyNodeTypes.KSampler, new
        {
            seed = ComfyGraph.Seed(p),
            steps = p.IntReq(WorkflowParamKeys.Steps),
            cfg = p.DblReq(WorkflowParamKeys.Cfg),
            sampler_name = ComfyGraph.MapSampler(p.StrReq(WorkflowParamKeys.Sampler)),
            scheduler = ComfyGraph.MapScheduler(p.StrReq(WorkflowParamKeys.Scheduler)),
            denoise = p.DblReq(WorkflowParamKeys.Denoise),
            model = model0,
            positive = ComfyGraph.Ref(ControlNetApply, 0),
            negative = ComfyGraph.Ref(ControlNetApply, 1),
            latent_image = ComfyGraph.Ref(Encode, 0),
        });
        wf[Decode] = ComfyGraph.Node(ComfyNodeTypes.VAEDecode, new { samples = ComfyGraph.Ref(Sampler, 0), vae = vae0 });
        wf[Save] = ComfyGraph.Node(ComfyNodeTypes.SaveImage, new { images = ComfyGraph.Ref(Decode, 0), filename_prefix = "forgemcp_edit" });
        return wf;
    }
}
