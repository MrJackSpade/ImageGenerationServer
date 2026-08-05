using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>Pixel-Anima parameters: the shared txt2img knobs plus the pixel-manifold grid/palette/virtual-resolution
/// and per-step projection ramp read by both the denoise-model patch and the final quantize. Virtual resolution sets
/// the sprite's pixel count on its longest edge (0 = use the explicit grid); grid_w/grid_h are the explicit fallback.</summary>
public sealed record PixelAnimaParams : Txt2ImgParams
{
    [JsonPropertyName(WorkflowParamKeys.VirtualResolution)]
    [Range(0, 4096)]                                        public required int VirtualResolution { get; init; }
    [JsonPropertyName(WorkflowParamKeys.GridW)]
    [Range(0, 4096)]                                        public required int GridW { get; init; }
    [JsonPropertyName(WorkflowParamKeys.GridH)]
    [Range(0, 4096)]                                        public required int GridH { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Palette)]           public required string Palette { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ProjMethod)]        public required string ProjMethod { get; init; }
    [JsonPropertyName(WorkflowParamKeys.FinalMethod)]       public required string FinalMethod { get; init; }
    [JsonPropertyName(WorkflowParamKeys.WStart)]
    [Range(0.0, 1.0)]                                       public required double WStart { get; init; }
    [JsonPropertyName(WorkflowParamKeys.WEnd)]
    [Range(0.0, 1.0)]                                       public required double WEnd { get; init; }
    [JsonPropertyName(WorkflowParamKeys.StartPercent)]
    [Range(0.0, 1.0)]                                       public required double StartPercent { get; init; }
    [JsonPropertyName(WorkflowParamKeys.EndPercent)]
    [Range(0.0, 1.0)]                                       public required double EndPercent { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ProjectEvery)]
    [Range(1, 8)]                                           public required int ProjectEvery { get; init; }
}

/// <summary>
/// Anima text-to-image UNDER the pixel-manifold pipeline — a GENERATE workflow (not an edit, so no source image).
/// Reuses the shared txt2img topology via <see cref="Txt2ImgWorkflow{TParams}"/> (Anima checkpoint + Qwen3 encoder), but
/// patches the denoise model with the per-step <c>PixelManifoldProjection</c> so every step clamps the x0 estimate
/// onto a fixed grid+palette, and renders the authoritative crisp output with a final <c>PixelQuantize</c> (so VAE
/// noise never reaches it). The projection does the pixel-art structuring live while Anima — an authoring model —
/// draws the scene from the prompt under that constraint. Virtual resolution sets the sprite's pixel count on its
/// longest edge, independent of the render bucket. Same booru-tag prompting + auto quality prefix as plain Anima.
/// The two model/decode inserts (nodes 35/36) reuse the projection + quantize emitters shared with the pixelizers.
/// </summary>
public sealed class PixelAnimaWorkflow : Txt2ImgWorkflow<PixelAnimaParams>
{
    public override string Name => "pixelanima";

    public override IReadOnlyList<ParamSpec> Schema => _schema;
    private static readonly IReadOnlyList<ParamSpec> _schema = Txt2ImgWorkflowBase.SharedSchema.Concat(new ParamSpec[]
    {
        // Virtual resolution = the sprite's pixel count on its longest edge; the grid follows the render aspect. This is
        // the knob the UI exposes. 0 = use explicit grid_w/grid_h instead.
        new() { Key = WorkflowParamKeys.VirtualResolution, Type = ParamType.Int, Min = 0, Max = 4096, Label = "Virtual res", Help = "Sprite pixel count on its longest edge" },
        new() { Key = WorkflowParamKeys.GridW, Type = ParamType.Int, Min = 0, Max = 4096 },
        new() { Key = WorkflowParamKeys.GridH, Type = ParamType.Int, Min = 0, Max = 4096 },
        new() { Key = WorkflowParamKeys.Palette, Type = ParamType.Enum, Choices = PixelPalettes.Choices, Label = "Palette" },
        new() { Key = WorkflowParamKeys.ProjMethod,  Type = ParamType.Enum, Choices = new[] { "median", "mode", "box", "nearest_present", "mean_srgb", "mean_linear", "mean_oklab", "lanczos", "var_hybrid", "supersample_mode" }, Label = "Projection", Help = "Per-step projection method (median = crisp + straight edges)" },
        new() { Key = WorkflowParamKeys.FinalMethod, Type = ParamType.Enum, Choices = new[] { "median", "mode", "box", "nearest_present", "mean_srgb", "mean_linear", "mean_oklab", "lanczos", "var_hybrid", "supersample_mode" }, Label = "Cell method", Help = "Final-render cell method (median = crisp + straight; box = smoother)" },
        new() { Key = WorkflowParamKeys.WStart,       Type = ParamType.Double, Min = 0.0, Max = 1.0 },
        new() { Key = WorkflowParamKeys.WEnd,         Type = ParamType.Double, Min = 0.0, Max = 1.0 },
        new() { Key = WorkflowParamKeys.StartPercent, Type = ParamType.Double, Min = 0.0, Max = 1.0 },
        new() { Key = WorkflowParamKeys.EndPercent,   Type = ParamType.Double, Min = 0.0, Max = 1.0 },
        new() { Key = WorkflowParamKeys.ProjectEvery, Type = ParamType.Int,    Min = 1, Max = 8 },
    }).ToArray();

    /// <summary>Patch the denoise model with the per-step pixel-manifold projection (the base's reserved patch node).</summary>
    protected override Output<Slot.Model> PatchDenoiseModel(ComfyWorkflowGraph g, Output<Slot.Model> model, Output<Slot.Vae> vae, PixelAnimaParams p)
    {
        g[Nodes.DenoisePatch] = PixelizeSchema.Projection(model, vae, p.GridW, p.GridH, p.Palette, p.VirtualResolution,
            p.ProjMethod, p.WStart, p.WEnd, p.StartPercent, p.EndPercent, p.ProjectEvery);
        return PixelManifoldProjection.Out(Nodes.DenoisePatch);
    }

    /// <summary>Render the authoritative crisp output with a final PixelQuantize (the base's reserved post-decode node).</summary>
    protected override Output<Slot.Image> PostDecodeImage(ComfyWorkflowGraph g, Output<Slot.Image> image, PixelAnimaParams p)
    {
        g[Nodes.PostDecode] = PixelizeSchema.FinalQuantize(image, p.GridW, p.GridH, p.Palette, p.VirtualResolution, p.FinalMethod);
        return PixelQuantize.Out(Nodes.PostDecode);
    }
}
