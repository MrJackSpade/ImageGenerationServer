using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

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

    private static readonly IReadOnlyList<ParamSpec> ControlNetSchema = new ParamSpec[]
    {
        new() { Key = LoaderKinds.ParamKey, Type = ParamType.Enum, Choices = LoaderKinds.Choices },
        new() { Key = WorkflowParamKeys.Steps,      Type = ParamType.Int,    Min = ParamBounds.StepsMin,    Max = ParamBounds.StepsMax, Label = "Steps" },
        new() { Key = WorkflowParamKeys.Cfg,        Type = ParamType.Double, Min = ParamBounds.CfgMin,    Max = ParamBounds.CfgMax,  Label = "CFG scale" },
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

    protected override ComfyWorkflowGraph Build(LineThickenControlNetParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new ComfyWorkflowGraph();
        LoadModel(g, p.Loader, p.WeightDtype, p.ClipType, req, inputs, out var model0, out var clip0, out var vae0);   // CheckpointLoaderSimple (4) + LoadImage (10)
        Output<Slot.Image> src = PixelHarnessGraph.FlattenOnWhite(g);                     // flatten alpha onto white (11-14)

        string prompt = p.StylePrompt is { } sp && !string.IsNullOrWhiteSpace(sp) ? sp : inputs.Positive;
        string neg = p.Negative ?? "";   // the model's own documented negative from the config JSON, or none — no shared baseline
        // 60/61, not 12/13 — FlattenOnWhite already owns 11-14 (EmptyImage 12, InvertMask 13, composite 14).
        g[Positive] = new CLIPTextEncode { Text = prompt, Clip = clip0 };
        g[Negative] = new CLIPTextEncode { Text = neg, Clip = clip0 };

        // lineart control image (white-on-black, as the lineart ControlNet expects), then apply the ControlNet.
        g[Lineart] = new LineArtPreprocessor { Image = src, Coarse = p.Coarse, Resolution = p.Resolution };
        g[ControlNet] = new ControlNetLoader { ControlNetName = req.RequiredControlNet() };
        g[ControlNetApply] = new ControlNetApplyAdvanced
        {
            Positive = CLIPTextEncode.Out(Positive),
            Negative = CLIPTextEncode.Out(Negative),
            ControlNet = ControlNetLoader.Out(ControlNet),
            Image = LineArtPreprocessor.Out(Lineart),
            Strength = p.ControlnetStrength,
            StartPercent = 0.0,
            EndPercent = 1.0,
            Vae = vae0,
        };

        // img2img from the source so the character is preserved; the ControlNet enforces the bold lineart.
        g[Encode] = new VAEEncode { Pixels = src, Vae = vae0 };
        g[Sampler] = new KSampler
        {
            Seed = ComfyGraph.Seed(p.Seed),
            Steps = p.Steps,
            Cfg = p.Cfg,
            SamplerName = ComfyGraph.MapSampler(p.Sampler),
            Scheduler = ComfyGraph.MapScheduler(p.Scheduler),
            Denoise = p.Denoise,
            Model = model0,
            Positive = ControlNetApplyAdvanced.PositiveOut(ControlNetApply),
            Negative = ControlNetApplyAdvanced.NegativeOut(ControlNetApply),
            LatentImage = VAEEncode.Out(Encode),
        };
        g[Decode] = new VAEDecode { Samples = KSampler.Out(Sampler), Vae = vae0 };
        g[Save] = new SaveImage { Images = VAEDecode.Out(Decode), FilenamePrefix = "forgemcp_edit" };
        return g;
    }
}

/// <summary>ControlNet lineart re-render parameters — the shared loader head knobs (<c>loader</c>/<c>weight_dtype</c>/
/// <c>clip_type</c> for the typed <c>LoadModel</c>), the sampler settings, the lineart preprocessor's coarse/resolution
/// and the ControlNet strength. The <c>*Req</c>-read values are <c>required</c>; <c>weight_dtype</c>/<c>clip_type</c>/
/// <c>style_prompt</c>/<c>negative</c> are nullable strings; <c>seed</c> is the app's single-sourced seed (defaulted).</summary>
public sealed record LineThickenControlNetParams
{
    [JsonPropertyName(WorkflowParamKeys.Loader)]             public required string Loader { get; init; }
    [JsonPropertyName(WorkflowParamKeys.WeightDtype)]        public string? WeightDtype { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ClipType)]           public string? ClipType { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Steps)]             public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Cfg)]               public required double Cfg { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Sampler)]           public required string Sampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Scheduler)]         public required string Scheduler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Denoise)]           public required double Denoise { get; init; }
    [JsonPropertyName(WorkflowParamKeys.StylePrompt)]       public string? StylePrompt { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Negative)]          public string? Negative { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Coarse)]            public required string Coarse { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ControlnetStrength)] public required double ControlnetStrength { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Resolution)]        public required int Resolution { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)]             public long Seed { get; init; }
}
