namespace ImageGen.Comfy;

/// <summary>Shared schema fragments + projection/quantize node emitters for the per-model pixelizers, so the
/// grid/palette/virtual-resolution + projection-ramp knobs stay identical across them.</summary>
internal static class PixelizeSchema
{
    private static readonly ParamSpec[] Common =
    {
        new() { Key = "virtual_resolution", Type = ParamType.Int, Min = 0, Max = 4096, Label = "Virtual res", Help = "Sprite pixel count on its longest edge" },
        new() { Key = "grid_w",       Type = ParamType.Int,    Min = 0, Max = 4096 },
        new() { Key = "grid_h",       Type = ParamType.Int,    Min = 0, Max = 4096 },
        // Snap the render res to a clean integer multiple of VRES (exact k×k cells) within the model's range,
        // overriding the model's own image-scale bucket. Needs width+height (the requested fixed aspect).
        new() { Key = "width",           Type = ParamType.Int,  Min = 0, Max = 4096, Label = "Render width", Help = "Explicit render width; 0 = model default" },
        new() { Key = "height",          Type = ParamType.Int,  Min = 0, Max = 4096, Label = "Render height", Help = "Explicit render height; 0 = model default" },
        new() { Key = "snap_resolution", Type = ParamType.Bool, Label = "Snap res", Help = "Override the render size to a clean integer multiple of VRES" },
        new() { Key = "out_scale",    Type = ParamType.Int,    Min = 1, Max = 16, Label = "Output upscale" },
        new() { Key = "palette",      Type = ParamType.Enum, Choices = PixelPalettes.Choices, Label = "Palette" },
        new() { Key = "proj_method",  Type = ParamType.Enum,   Choices = new[] { "median", "mode", "box", "nearest_present", "mean_srgb", "mean_linear", "mean_oklab", "lanczos", "var_hybrid", "supersample_mode" }, Label = "Projection", Help = "Per-step projection method (median = crisp + straight edges)" },
        new() { Key = "final_method", Type = ParamType.Enum,   Choices = new[] { "median", "mode", "box", "nearest_present", "mean_srgb", "mean_linear", "mean_oklab", "lanczos", "var_hybrid", "supersample_mode" }, Label = "Cell method", Help = "Final-render cell method (median = crisp + straight; box = smoother)" },
        new() { Key = "w_start",       Type = ParamType.Double, Min = 0.0, Max = 1.0 },
        new() { Key = "w_end",         Type = ParamType.Double, Min = 0.0, Max = 1.0 },
        new() { Key = "start_percent", Type = ParamType.Double, Min = 0.0, Max = 1.0 },
        new() { Key = "end_percent",   Type = ParamType.Double, Min = 0.0, Max = 1.0 },
        new() { Key = "project_every", Type = ParamType.Int,    Min = 1, Max = 8 },
    };

    public static IReadOnlyList<ParamSpec> KontextLike(string defPrompt) => new ParamSpec[]
    {
        new() { Key = "loader",    Type = ParamType.Enum,   Choices = new[] { "checkpoint", "unet", "unet_gguf" } },
        // No default. A GENERIC workflow cannot know which CLIP family a configuration is for, and "flux"
        // silently became the answer for any configuration that omitted it -- pixelize-hidream inherited it
        // and handed CLIPLoader a type it does not accept. An omission must surface, not be guessed.
        new() { Key = "clip_type", Type = ParamType.String },
        new() { Key = "dual",      Type = ParamType.Bool },
        new() { Key = "steps",     Type = ParamType.Int,    Min = 1, Max = 100, Label = "Steps" },
        new() { Key = "cfg",       Type = ParamType.Double, Min = 1, Max = 30, Label = "CFG scale" },
        new() { Key = "guidance",  Type = ParamType.Double },
        new() { Key = "sampler",   Type = ParamType.String },
        new() { Key = "scheduler", Type = ParamType.String },
        new() { Key = "style_prompt", Type = ParamType.String, Label = "Instruction" },
        new() { Key = "reference", Type = ParamType.Int, Min = 0, Max = 100, Label = "Reference %", Help = "0 = generate fresh · 100 = copy the source" },
    }.Concat(Common).ToArray();

    public static IReadOnlyList<ParamSpec> KleinLike(string defPrompt) => new ParamSpec[]
    {
        new() { Key = "loader",    Type = ParamType.Enum,   Choices = new[] { "checkpoint", "unet", "unet_gguf" } },
        new() { Key = "clip_type", Type = ParamType.String },
        new() { Key = "dual",      Type = ParamType.Bool },
        new() { Key = "steps",     Type = ParamType.Int,    Min = 1, Max = 100, Label = "Steps" },
        new() { Key = "cfg",       Type = ParamType.Double, Min = 1, Max = 30, Label = "CFG scale" },
        new() { Key = "guidance",  Type = ParamType.Double },
        new() { Key = "sampler",   Type = ParamType.String },
        new() { Key = "scheduler", Type = ParamType.String },
        new() { Key = "megapixels", Type = ParamType.Double, Min = 0.1, Max = 4.0 },
        new() { Key = "style_prompt", Type = ParamType.String, Label = "Instruction" },
        new() { Key = "reference", Type = ParamType.Int, Min = 0, Max = 100, Label = "Reference %", Help = "0 = generate fresh · 100 = copy the source" },
    }.Concat(Common).ToArray();

    public static IReadOnlyList<ParamSpec> DreamOmniLike(string defPrompt) => new ParamSpec[]
    {
        new() { Key = "steps", Type = ParamType.Int,    Min = 1, Max = 100, Label = "Steps" },
        new() { Key = "cfg",   Type = ParamType.Double, Min = 1, Max = 30, Label = "Guidance scale" },
        new() { Key = "reference_max",    Type = ParamType.Int },
        new() { Key = "reference_inputs", Type = ParamType.String },
        new() { Key = "style_prompt", Type = ParamType.String, Label = "Instruction" },
        new() { Key = "reference", Type = ParamType.Int, Min = 0, Max = 100, Label = "Reference %", Help = "0 = generate fresh · 100 = copy the source" },
    }.Concat(Common).ToArray();

    /// <summary>The per-step PixelManifoldProjection model patch (node "35"), identical across pixelizers.</summary>
    public static Dictionary<string, object> Projection(object model, object vae, int gw, int gh, string palette, int vres, ParamValues p) =>
        ComfyGraph.Node("PixelManifoldProjection", new
        {
            model,
            vae,
            grid_w = gw,
            grid_h = gh,
            palette,
            method = p.StrReq("proj_method"),
            w_start = p.DblReq("w_start"),
            w_end = p.DblReq("w_end"),
            start_percent = p.DblReq("start_percent"),
            end_percent = p.DblReq("end_percent"),
            project_every = p.IntReq("project_every"),
            virtual_resolution = vres,
        });

    /// <summary>The authoritative final PixelQuantize render (node "36").</summary>
    public static Dictionary<string, object> FinalQuantize(object image, int gw, int gh, string palette, int vres, ParamValues p) =>
        ComfyGraph.Node("PixelQuantize", new
        {
            image,
            grid_w = gw,
            grid_h = gh,
            palette,
            method = p.StrReq("final_method"),
            virtual_resolution = vres,
        });
}
