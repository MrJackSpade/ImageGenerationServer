namespace ImageGen.Comfy;

/// <summary>Shared schema fragments + projection/quantize node emitters for the per-model pixelizers, so the
/// grid/palette/virtual-resolution + projection-ramp knobs stay identical across them.</summary>
internal static class PixelizeSchema
{
    private static readonly ParamSpec[] Common =
    [
        .. SeedParam.Schema,
        new() { Key = WorkflowParamKeys.VirtualResolution, Type = ParamType.Int, Min = 0, Max = 4096, Label = "Virtual res", Help = "Sprite pixel count on its longest edge" },
        new() { Key = WorkflowParamKeys.GridW,       Type = ParamType.Int,    Min = 0, Max = 4096 },
        new() { Key = WorkflowParamKeys.GridH,       Type = ParamType.Int,    Min = 0, Max = 4096 },
        // Snap the render res to a clean integer multiple of VRES (exact k×k cells) within the model's range,
        // overriding the model's own image-scale bucket. Needs width+height (the requested fixed aspect).
        new() { Key = WorkflowParamKeys.Width,           Type = ParamType.Int,  Min = 0, Max = 4096, Label = "Render width", Help = "Explicit render width; 0 = model default" },
        new() { Key = WorkflowParamKeys.Height,          Type = ParamType.Int,  Min = 0, Max = 4096, Label = "Render height", Help = "Explicit render height; 0 = model default" },
        new() { Key = WorkflowParamKeys.SnapResolution, Type = ParamType.Bool, Label = "Snap res", Help = "Override the render size to a clean integer multiple of VRES" },
        new() { Key = WorkflowParamKeys.OutScale,    Type = ParamType.Int,    Min = 1, Max = 16, Label = "Output upscale" },
        new() { Key = WorkflowParamKeys.Palette,      Type = ParamType.Enum, Choices = PixelPalettes.Choices, Label = "Palette" },
        new() { Key = WorkflowParamKeys.ProjMethod,  Type = ParamType.Enum,   Choices = ComfyWidgetChoices.PixelizeMethods, Label = "Projection", Help = "Per-step projection method (median = crisp + straight edges)" },
        new() { Key = WorkflowParamKeys.FinalMethod, Type = ParamType.Enum,   Choices = ComfyWidgetChoices.PixelizeMethods, Label = "Cell method", Help = "Final-render cell method (median = crisp + straight; box = smoother)" },
        new() { Key = WorkflowParamKeys.WStart,       Type = ParamType.Double, Min = 0.0, Max = 1.0 },
        new() { Key = WorkflowParamKeys.WEnd,         Type = ParamType.Double, Min = 0.0, Max = 1.0 },
        new() { Key = WorkflowParamKeys.StartPercent, Type = ParamType.Double, Min = 0.0, Max = 1.0 },
        new() { Key = WorkflowParamKeys.EndPercent,   Type = ParamType.Double, Min = 0.0, Max = 1.0 },
        new() { Key = WorkflowParamKeys.ProjectEvery, Type = ParamType.Int,    Min = 1, Max = 8 },
    ];

    public static IReadOnlyList<ParamSpec> KontextLike() =>
    [
        new() { Key = LoaderKinds.ParamKey, Type = ParamType.Enum, Choices = LoaderKindWire.Choices },
        // No default. A GENERIC workflow cannot know which CLIP family a configuration is for; a "flux"
        // default would be silently wrong for any configuration that omits it -- pixelize-hidream would
        // inherit it and hand CLIPLoader a type it does not accept. An omission must surface, not be guessed.
        new() { Key = WorkflowParamKeys.ClipType, Type = ParamType.String },
        new() { Key = WorkflowParamKeys.Dual,      Type = ParamType.Bool },
        new() { Key = WorkflowParamKeys.Steps,     Type = ParamType.Int,    Min = ParamBounds.StepsMin, Max = ParamBounds.StepsMax, Label = "Steps" },
        new() { Key = WorkflowParamKeys.Cfg,       Type = ParamType.Double, Min = ParamBounds.CfgMin, Max = ParamBounds.CfgMax, Label = "CFG scale" },
        new() { Key = WorkflowParamKeys.Guidance,  Type = ParamType.Double },
        new() { Key = WorkflowParamKeys.Sampler,   Type = ParamType.String },
        new() { Key = WorkflowParamKeys.Scheduler, Type = ParamType.String },
        new() { Key = WorkflowParamKeys.StylePrompt, Type = ParamType.String, Label = "Instruction" },
        new() { Key = WorkflowParamKeys.Reference, Type = ParamType.Int, Min = 0, Max = 100, Label = "Reference %", Help = "0 = generate fresh · 100 = copy the source" },
        .. Common,
    ];

    public static IReadOnlyList<ParamSpec> KleinLike() =>
    [
        new() { Key = LoaderKinds.ParamKey, Type = ParamType.Enum, Choices = LoaderKindWire.Choices },
        new() { Key = WorkflowParamKeys.ClipType, Type = ParamType.String },
        new() { Key = WorkflowParamKeys.Dual,      Type = ParamType.Bool },
        new() { Key = WorkflowParamKeys.Steps,     Type = ParamType.Int,    Min = ParamBounds.StepsMin, Max = ParamBounds.StepsMax, Label = "Steps" },
        new() { Key = WorkflowParamKeys.Cfg,       Type = ParamType.Double, Min = ParamBounds.CfgMin, Max = ParamBounds.CfgMax, Label = "CFG scale" },
        new() { Key = WorkflowParamKeys.Guidance,  Type = ParamType.Double },
        new() { Key = WorkflowParamKeys.Sampler,   Type = ParamType.String },
        new() { Key = WorkflowParamKeys.Scheduler, Type = ParamType.String },
        new() { Key = WorkflowParamKeys.Megapixels, Type = ParamType.Double, Min = 0.1, Max = 4.0 },
        new() { Key = WorkflowParamKeys.StylePrompt, Type = ParamType.String, Label = "Instruction" },
        new() { Key = WorkflowParamKeys.Reference, Type = ParamType.Int, Min = 0, Max = 100, Label = "Reference %", Help = "0 = generate fresh · 100 = copy the source" },
        .. Common,
    ];

    public static IReadOnlyList<ParamSpec> DreamOmniLike() =>
    [
        new() { Key = WorkflowParamKeys.BaseModel, Type = ParamType.String, IsModelRef = true },
        new() { Key = WorkflowParamKeys.Steps, Type = ParamType.Int,    Min = ParamBounds.StepsMin, Max = ParamBounds.StepsMax, Label = "Steps" },
        new() { Key = WorkflowParamKeys.Cfg,   Type = ParamType.Double, Min = ParamBounds.CfgMin, Max = ParamBounds.CfgMax, Label = "Guidance scale" },
        new() { Key = WorkflowParamKeys.ReferenceMax,    Type = ParamType.Int },
        new() { Key = WorkflowParamKeys.ReferenceInputs, Type = ParamType.String },
        new() { Key = WorkflowParamKeys.StylePrompt, Type = ParamType.String, Label = "Instruction" },
        new() { Key = WorkflowParamKeys.Reference, Type = ParamType.Int, Min = 0, Max = 100, Label = "Reference %", Help = "0 = generate fresh · 100 = copy the source" },
        .. Common,
    ];

    /// <summary>The per-step PixelManifoldProjection model patch (node "35"), identical across pixelizers: the projection
    /// model-patch node from the ramp values (the caller reads them off its typed params DTO).</summary>
    public static PixelManifoldProjection Projection(Output<Slot.Model> model, Output<Slot.Vae> vae, int gw, int gh,
        string palette, int vres, string method, double wStart, double wEnd, double startPercent, double endPercent, int projectEvery) =>
        new()
        {
            Model = model,
            Vae = vae,
            GridW = gw,
            GridH = gh,
            Palette = palette,
            Method = method,
            WStart = wStart,
            WEnd = wEnd,
            StartPercent = startPercent,
            EndPercent = endPercent,
            ProjectEvery = projectEvery,
            VirtualResolution = vres,
        };

    /// <summary>The authoritative final PixelQuantize render (node "36").</summary>
    public static PixelQuantize FinalQuantize(Output<Slot.Image> image, int gw, int gh, string palette, int vres, string finalMethod) =>
        new()
        { Image = image, GridW = gw, GridH = gh, Palette = palette, Method = finalMethod, VirtualResolution = vres };
}
