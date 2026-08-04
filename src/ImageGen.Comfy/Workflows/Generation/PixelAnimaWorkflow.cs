//TODO: CHECK FOR FALLBACKS
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
        new() { Key = "virtual_resolution", Type = ParamType.Int, Default = 384, Min = 0, Max = 4096, Label = "Virtual res", Help = "Sprite pixel count on its longest edge" },
        new() { Key = "grid_w", Type = ParamType.Int, Min = 0, Max = 4096 },
        new() { Key = "grid_h", Type = ParamType.Int, Min = 0, Max = 4096 },
        new() { Key = "palette", Type = ParamType.Enum, Choices = PixelPalettes.Choices, Default = "adaptive", Label = "Palette" },
        new() { Key = "proj_method",  Type = ParamType.Enum, Choices = new[] { "median", "mode", "box", "nearest_present", "mean_srgb", "mean_linear", "mean_oklab", "lanczos", "var_hybrid", "supersample_mode" }, Default = "median", Label = "Projection", Help = "Per-step projection method (median = crisp + straight edges)" },
        new() { Key = "final_method", Type = ParamType.Enum, Choices = new[] { "median", "mode", "box", "nearest_present", "mean_srgb", "mean_linear", "mean_oklab", "lanczos", "var_hybrid", "supersample_mode" }, Default = "median", Label = "Cell method", Help = "Final-render cell method (median = crisp + straight; box = smoother)" },
        new() { Key = "w_start",       Type = ParamType.Double, Default = 0.5, Min = 0.0, Max = 1.0 },
        new() { Key = "w_end",         Type = ParamType.Double, Default = 1.0, Min = 0.0, Max = 1.0 },
        new() { Key = "start_percent", Type = ParamType.Double, Default = 0.0, Min = 0.0, Max = 1.0 },
        new() { Key = "end_percent",   Type = ParamType.Double, Default = 1.0, Min = 0.0, Max = 1.0 },
        new() { Key = "project_every", Type = ParamType.Int,    Default = 1, Min = 1, Max = 8 },
    }).ToArray();

    /// <summary>Grid + palette + virtual resolution shared by the projection patch and the final quantize.</summary>
    private static (int gw, int gh, string palette, int vres) Pixel(ParamValues p)
    {
        int gw = p.IntReq("grid_w");
        int gh = p.IntReq("grid_h");
        return (gw, gh, p.StrReq("palette"), p.IntReq("virtual_resolution"));
    }

    /// <summary>Patch the denoise model with the per-step pixel-manifold projection (node "35").</summary>
    protected override object PatchDenoiseModel(Dictionary<string, object> wf, object model, object vae, ParamValues p)
    {
        var (gw, gh, palette, vres) = Pixel(p);
        wf["35"] = PixelizeSchema.Projection(model, vae, gw, gh, palette, vres, p);
        return ComfyGraph.Ref("35", 0);
    }

    /// <summary>Render the authoritative crisp output with a final PixelQuantize (node "36").</summary>
    protected override object PostDecodeImage(Dictionary<string, object> wf, object image, ParamValues p)
    {
        var (gw, gh, palette, vres) = Pixel(p);
        wf["36"] = PixelizeSchema.FinalQuantize(image, gw, gh, palette, vres, p);
        return ComfyGraph.Ref("36", 0);
    }
}
