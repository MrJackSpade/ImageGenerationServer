namespace ImageGen.Comfy.Edit.LineThickenControlNet;

/// <summary>
/// ControlNet line-art re-render. A lineart preprocessor (<c>LineArtPreprocessor</c>,
/// comfyui_controlnet_aux; <c>coarse=enable</c> yields bolder lines) drives an SD1.5 lineart
/// ControlNet over an anime checkpoint, re-rendering the source as img2img at partial denoise so the
/// character is preserved while the outlines are redrawn clean and bold. Checkpoint + ControlNet are
/// resolved from the configuration's requirements (the files must be present on disk).
/// </summary>
public sealed class LineThickenControlNetWorkflow : EditWorkflow<LineThickenControlNetParams>
{
    public override string Name => "line-thicken-controlnet";
    public override bool PreservesComposition => true;
    public override IReadOnlyList<ParamSpec> Schema => ControlNetSchema;

    private static readonly IReadOnlyList<ParamSpec> ControlNetSchema =
    [
        new() { Key = LoaderKinds.ParamKey, Type = ParamType.Enum, Choices = LoaderKindWire.Choices },
        new() { Key = WorkflowParamKeys.Steps,      Type = ParamType.Int,    Min = ParamBounds.StepsMin,    Max = ParamBounds.StepsMax, Label = "Steps" },
        new() { Key = WorkflowParamKeys.Cfg,        Type = ParamType.Double, Min = ParamBounds.CfgMin,    Max = ParamBounds.CfgMax,  Label = "CFG scale" },
        new() { Key = WorkflowParamKeys.Sampler,    Type = ParamType.String },
        new() { Key = WorkflowParamKeys.Scheduler,  Type = ParamType.String },
        .. SeedParam.Schema,
        new() { Key = WorkflowParamKeys.Denoise,    Type = ParamType.Double, Min = ParamBounds.DenoiseMin, Max = ParamBounds.DenoiseMax, Step = 0.01, Label = "Redraw amount" },
        new() { Key = WorkflowParamKeys.StylePrompt, Type = ParamType.String, Label = "Style prompt" },
        new() { Key = WorkflowParamKeys.Negative,   Type = ParamType.String },
        new() { Key = WorkflowParamKeys.Coarse,     Type = ParamType.Enum,   Choices = [ComfyWidgets.Toggle.Enable, ComfyWidgets.Toggle.Disable], Label = "Coarse (bolder) lineart" },
        new() { Key = WorkflowParamKeys.ControlnetStrength, Type = ParamType.Double, Min = 0.0, Max = 2.0, Step = 0.01, Label = "ControlNet strength" },
        new() { Key = WorkflowParamKeys.Resolution, Type = ParamType.Int,    Min = 256,  Max = 2048, Label = "Lineart resolution" },
    ];

    protected override ComfyWorkflowGraph Build(LineThickenControlNetParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new();
        LoadModel(g, p.Loader, p.WeightDtype, p.ClipType, req, inputs, out Output<Slot.Model> model0, out Output<Slot.Clip> clip0, out Output<Slot.Vae> vae0);   // CheckpointLoaderSimple (4) + LoadImage (10)
        Output<Slot.Image> src = PixelHarnessGraph.FlattenOnWhite(g);                     // flatten alpha onto white (11-14)

        string prompt = p.StylePrompt is { } sp && !string.IsNullOrWhiteSpace(sp) ? sp : inputs.Positive;
        string neg = p.Negative ?? "";   // the model's own documented negative from the config JSON, or none — no shared baseline
        // 60/61, not 12/13 — FlattenOnWhite already owns 11-14 (EmptyImage 12, InvertMask 13, composite 14).
        g[Nodes.Positive] = new CLIPTextEncode { Text = prompt, Clip = clip0 };
        g[Nodes.Negative] = new CLIPTextEncode { Text = neg, Clip = clip0 };

        // lineart control image (white-on-black, as the lineart ControlNet expects), then apply the ControlNet.
        g[Nodes.Lineart] = new LineArtPreprocessor { Image = src, Coarse = p.Coarse, Resolution = p.Resolution };
        g[Nodes.ControlNet] = new ControlNetLoader { ControlNetName = req.RequiredControlNet() };
        g[Nodes.ControlNetApply] = new ControlNetApplyAdvanced
        {
            Positive = CLIPTextEncode.Out(Nodes.Positive),
            Negative = CLIPTextEncode.Out(Nodes.Negative),
            ControlNet = ControlNetLoader.Out(Nodes.ControlNet),
            Image = LineArtPreprocessor.Out(Nodes.Lineart),
            Strength = p.ControlnetStrength,
            StartPercent = 0.0,
            EndPercent = 1.0,
            Vae = vae0,
        };

        // img2img from the source so the character is preserved; the ControlNet enforces the bold lineart.
        g[Nodes.Encode] = new VAEEncode { Pixels = src, Vae = vae0 };
        g[Nodes.Sampler] = new KSampler
        {
            Seed = ComfyGraph.Seed(p.Seed),
            Steps = p.Steps,
            Cfg = p.Cfg,
            SamplerName = ComfyGraph.MapSampler(p.Sampler),
            Scheduler = ComfyGraph.MapScheduler(p.Scheduler),
            Denoise = p.Denoise,
            Model = model0,
            Positive = ControlNetApplyAdvanced.PositiveOut(Nodes.ControlNetApply),
            Negative = ControlNetApplyAdvanced.NegativeOut(Nodes.ControlNetApply),
            LatentImage = VAEEncode.Out(Nodes.Encode),
        };
        g[Nodes.Decode] = new VAEDecode { Samples = KSampler.Out(Nodes.Sampler), Vae = vae0 };
        g[Nodes.Save] = new SaveImage { Images = VAEDecode.Out(Nodes.Decode), FilenamePrefix = OutputPrefixes.Edit };
        return g;
    }
}
