namespace ImageGen.Comfy;

/// <summary>
/// Anima text-to-image UNDER the pixel-manifold pipeline — a GENERATE workflow (not an edit, so no source image).
/// Reuses the shared txt2img topology via <see cref="Txt2ImgWorkflowBase"/> (Anima checkpoint + Qwen3 encoder), but
/// patches the denoise model with the per-step <c>PixelManifoldProjection</c> so every step clamps the x0 estimate
/// onto a fixed grid+palette, and renders the authoritative crisp output with a final <c>PixelQuantize</c> (so VAE
/// noise never reaches it). The projection does the pixel-art structuring live while Anima — an authoring model —
/// draws the scene from the prompt under that constraint. Virtual resolution sets the sprite's pixel count on its
/// longest edge, independent of the render bucket. Same booru-tag prompting + auto quality prefix as plain Anima.
/// The two model/decode inserts (nodes 35/36) reuse the projection + quantize emitters shared with the pixelizers.
/// </summary>
public sealed class PixelAnimaWorkflow : Txt2ImgWorkflowBase
{
    public override string Name => "pixelanima";

    public override IReadOnlyList<ParamSpec> Schema => _schema;
    private static readonly IReadOnlyList<ParamSpec> _schema = SharedSchema.Concat(new ParamSpec[]
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

    /// <summary>Grid + palette + virtual resolution shared by the projection patch and the final quantize.</summary>
    private static (int gw, int gh, string palette, int vres) Pixel(ParamValues p)
    {
        int gw = p.IntReq(WorkflowParamKeys.GridW);
        int gh = p.IntReq(WorkflowParamKeys.GridH);
        return (gw, gh, p.StrReq(WorkflowParamKeys.Palette), p.IntReq(WorkflowParamKeys.VirtualResolution));
    }

    /// <summary>Patch the denoise model with the per-step pixel-manifold projection (the base's reserved patch node).</summary>
    protected override object PatchDenoiseModel(Dictionary<string, object> wf, object model, object vae, ParamValues p)
    {
        (int gw, int gh, string? palette, int vres) = Pixel(p);
        wf[Nodes.DenoisePatch] = PixelizeSchema.Projection(model, vae, gw, gh, palette, vres, p);
        return ComfyGraph.Ref(Nodes.DenoisePatch, 0);
    }

    /// <summary>Render the authoritative crisp output with a final PixelQuantize (the base's reserved post-decode node).</summary>
    protected override object PostDecodeImage(Dictionary<string, object> wf, object image, ParamValues p)
    {
        (int gw, int gh, string? palette, int vres) = Pixel(p);
        wf[Nodes.PostDecode] = PixelizeSchema.FinalQuantize(image, gw, gh, palette, vres, p);
        return ComfyGraph.Ref(Nodes.PostDecode, 0);
    }
}
