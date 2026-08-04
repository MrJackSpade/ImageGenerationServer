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
        new() { Key = "steps",      Type = ParamType.Int,    Min = 1,    Max = 100, Label = "Steps" },
        new() { Key = "cfg",        Type = ParamType.Double, Min = 1,    Max = 30,  Label = "CFG scale" },
        new() { Key = "sampler",    Type = ParamType.String },
        new() { Key = "scheduler",  Type = ParamType.String },
        new() { Key = "denoise",    Type = ParamType.Double, Min = 0.1,  Max = 1.0, Label = "Redraw amount" },
        new() { Key = "style_prompt", Type = ParamType.String, Label = "Style prompt" },
        new() { Key = "negative",   Type = ParamType.String },
        new() { Key = "coarse",     Type = ParamType.Enum,   Choices = new[] { "enable", "disable" }, Label = "Coarse (bolder) lineart" },
        new() { Key = "controlnet_strength", Type = ParamType.Double, Min = 0.0, Max = 2.0, Label = "ControlNet strength" },
        new() { Key = "resolution", Type = ParamType.Int,    Min = 256,  Max = 2048, Label = "Lineart resolution" },
    };

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>();
        LoadModel(wf, p, req, inputs, out var model0, out var clip0, out var vae0);   // CheckpointLoaderSimple (4) + LoadImage (10)
        var src = PixelHarnessGraph.FlattenOnWhite(wf);                               // flatten alpha onto white (11-14)

        var prompt = p.Str("style_prompt");
        if (string.IsNullOrWhiteSpace(prompt)) prompt = inputs.Positive;
        var neg = p.Str("negative") ?? "";   // the model's own documented negative from the config JSON, or none — no shared baseline
        // 60/61, not 12/13 — FlattenOnWhite already owns 11-14 (EmptyImage 12, InvertMask 13, composite 14).
        wf["60"] = ComfyGraph.Node("CLIPTextEncode", new { text = prompt, clip = clip0 });
        wf["61"] = ComfyGraph.Node("CLIPTextEncode", new { text = neg, clip = clip0 });

        // lineart control image (white-on-black, as the lineart ControlNet expects), then apply the ControlNet.
        wf["50"] = ComfyGraph.Node("LineArtPreprocessor", new { image = src, coarse = p.StrReq("coarse"), resolution = p.IntReq("resolution") });
        wf["51"] = ComfyGraph.Node("ControlNetLoader", new { control_net_name = req.RequiredControlNet() });
        wf["52"] = ComfyGraph.Node("ControlNetApplyAdvanced", new
        {
            positive = ComfyGraph.Ref("60", 0),
            negative = ComfyGraph.Ref("61", 0),
            control_net = ComfyGraph.Ref("51", 0),
            image = ComfyGraph.Ref("50", 0),
            strength = p.DblReq("controlnet_strength"),
            start_percent = 0.0,
            end_percent = 1.0,
            vae = vae0,
        });

        // img2img from the source so the character is preserved; the ControlNet enforces the bold lineart.
        wf["31"] = ComfyGraph.Node("VAEEncode", new { pixels = src, vae = vae0 });
        wf["3"] = ComfyGraph.Node("KSampler", new
        {
            seed = ComfyGraph.Seed(p),
            steps = p.IntReq("steps"),
            cfg = p.DblReq("cfg"),
            sampler_name = ComfyGraph.MapSampler(p.StrReq("sampler")),
            scheduler = ComfyGraph.MapScheduler(p.StrReq("scheduler")),
            denoise = p.DblReq("denoise"),
            model = model0,
            positive = ComfyGraph.Ref("52", 0),
            negative = ComfyGraph.Ref("52", 1),
            latent_image = ComfyGraph.Ref("31", 0),
        });
        wf["8"] = ComfyGraph.Node("VAEDecode", new { samples = ComfyGraph.Ref("3", 0), vae = vae0 });
        wf["9"] = ComfyGraph.Node("SaveImage", new { images = ComfyGraph.Ref("8", 0), filename_prefix = "forgemcp_edit" });
        return wf;
    }
}
