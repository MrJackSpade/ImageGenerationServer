//TODO: CHECK FOR FALLBACKS
using System.Reflection;

namespace ImageGen.Comfy;

/// <summary>
/// Shared graph surgery for the pixel-art video decorator. Two splices, both base-agnostic (located by class_type so
/// they work on any i2v graph):
/// <list type="bullet">
/// <item><see cref="QuantizeFrames"/> — reroute the <c>SaveAnimatedWEBP</c> frames through <c>PixelQuantize</c> (the
/// deterministic final render). Always applied.</item>
/// <item><see cref="PatchModelProjection"/> — wrap EVERY terminal model-consumer's <c>model</c> with
/// <c>PixelManifoldProjection</c>, so each denoise step steers the latent onto the pixel manifold (pixels baked into
/// the motion → no per-frame line shimmer). Applied only when the <c>guided</c> param is set. Patches all consumers,
/// so a two-expert MoE like WAN-A14B gets both experts.</item>
/// </list>
/// </summary>
internal static class PixelVideoGraph
{
    /// <summary>Terminal model-consumers whose <c>model</c> input drives the actual denoise (and whose post-CFG hook
    /// the projection rides). NOT schedulers/ModelSampling* (those use the model only for sigmas).</summary>
    private static readonly HashSet<string> ModelConsumers = new(StringComparer.Ordinal)
    {
        "KSampler", "KSamplerAdvanced", "SamplerCustom", "CFGGuider", "BasicGuider",
    };

    /// <summary>Reroute the animated-WEBP save node's frames through a <c>PixelQuantize</c> (its exact still-pixelizer
    /// params + defaults). The quantizer flattens the <c>(B,T,H,W,3)</c> video decode into per-frame batches itself.</summary>
    public static void QuantizeFrames(Dictionary<string, object> wf, ParamValues p, string quantNodeId = "700")
    {
        var save = wf.Values.OfType<Dictionary<string, object>>()
            .FirstOrDefault(n => n.TryGetValue("class_type", out var ct) && ct as string == "SaveAnimatedWEBP");
        if (save is null) return;
        var inputs = AsInputDict(save["inputs"]);
        if (!inputs.TryGetValue("images", out var imagesSrc)) return;

        var (gw, gh) = Grid(p);
        wf[quantNodeId] = ComfyGraph.Node("PixelQuantize", new
        {
            image = imagesSrc,
            grid_w = gw,
            grid_h = gh,
            palette = p.Str("palette") ?? "chroma-256",
            method = p.Str("method") ?? "median",
            virtual_resolution = p.Int("virtual_resolution", 0),
        });
        inputs["images"] = ComfyGraph.Ref(quantNodeId, 0);
        save["inputs"] = inputs;
    }

    /// <summary>Insert a <c>PixelManifoldProjection</c> in front of every terminal model-consumer's <c>model</c> input,
    /// so the per-step projection runs for each (both experts of an MoE). The VAE is taken from the decode node.</summary>
    public static void PatchModelProjection(Dictionary<string, object> wf, ParamValues p)
    {
        var decode = wf.Values.OfType<Dictionary<string, object>>()
            .FirstOrDefault(n => n.TryGetValue("class_type", out var ct)
                                 && ct as string is "VAEDecode" or "VAEDecodeTiled");
        if (decode is null) return;
        var decInputs = AsInputDict(decode["inputs"]);
        if (!decInputs.TryGetValue("vae", out var vae) || vae is null) return;

        var (gw, gh) = Grid(p);
        var consumers = wf.Values.OfType<Dictionary<string, object>>()
            .Where(n => n.TryGetValue("class_type", out var ct) && ct is string s && ModelConsumers.Contains(s))
            .ToList();

        int next = 710;
        foreach (var node in consumers)
        {
            var inputs = AsInputDict(node["inputs"]);
            if (!inputs.TryGetValue("model", out var modelSrc) || modelSrc is null) continue;
            var projId = (next++).ToString();
            wf[projId] = ComfyGraph.Node("PixelManifoldProjection", new
            {
                model = modelSrc,
                vae,
                grid_w = gw,
                grid_h = gh,
                palette = p.Str("palette") ?? "chroma-256",
                method = p.Str("method") ?? "median",
                w_start = p.Dbl("w_start", 0.5),
                w_end = p.Dbl("w_end", 1.0),
                start_percent = p.Dbl("start_percent", 0.6),
                end_percent = p.Dbl("end_percent", 1.0),
                project_every = p.Int("project_every", 1),
                virtual_resolution = p.Int("virtual_resolution", 0),
            });
            inputs["model"] = ComfyGraph.Ref(projId, 0);
            node["inputs"] = inputs;
        }
    }

    /// <summary>The quantize grid: explicit grid_w/grid_h, falling back to 384×256 (PixelQuantizeWorkflow's default).</summary>
    private static (int gw, int gh) Grid(ParamValues p)
    {
        int gw = p.Int("grid_w", 0); if (gw <= 0) gw = 384;
        int gh = p.Int("grid_h", 0); if (gh <= 0) gh = 256;
        return (gw, gh);
    }

    /// <summary>A node's <c>inputs</c> is an anonymous object; reflect it into a mutable dict so an edge can be
    /// rerouted. A <c>Dictionary&lt;string,object&gt;</c> JSON-emits identically to the anonymous object.</summary>
    private static Dictionary<string, object?> AsInputDict(object inputs)
    {
        if (inputs is Dictionary<string, object?> d) return d;
        return inputs.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(pi => pi.Name, pi => pi.GetValue(inputs));
    }

    /// <summary>Params for the pixel-video decorator: the deterministic quantizer's knobs (an exact copy of
    /// <see cref="PixelQuantizeWorkflow"/>'s schema), the <c>guided</c> toggle, and the per-step projection
    /// ramp/window used only when guided. Palette defaults to a fixed bundled palette (locked = temporally
    /// consistent). start_percent defaults to 0.6 because projecting the noisy early steps of a full-denoise video
    /// destroys the image — the projection must engage only on the low-noise tail.</summary>
    public static readonly ParamSpec[] Params =
    {
        new() { Key = "virtual_resolution", Type = ParamType.Int, Default = 0, Min = 0, Max = 4096, Label = "Virtual res", Help = "Sprite pixel count on its longest edge" },
        new() { Key = "grid_w", Type = ParamType.Int, Default = 0, Min = 0, Max = 4096, Label = "Grid width" },
        new() { Key = "grid_h", Type = ParamType.Int, Default = 0, Min = 0, Max = 4096, Label = "Grid height" },
        new() { Key = "palette", Type = ParamType.String, Default = "chroma-256", Label = "Palette", Help = "A locked (named) palette is temporally consistent — no frame-to-frame flicker" },
        new() { Key = "method",  Type = ParamType.Enum, Choices = new[] { "median", "mode", "box", "nearest_present", "mean_srgb", "mean_linear", "mean_oklab", "lanczos", "var_hybrid", "supersample_mode" }, Default = "median", Label = "Cell method", Help = "median = crisp + straight edges; box = smoother" },
        // The toggle: false = fast post-quantize only; true = also project the latent onto the manifold every step
        // (pixels baked into the motion, no shimmer — much slower).
        new() { Key = "guided", Type = ParamType.Bool, Default = false, Label = "Pixel-guided", Help = "Project the latent every step — kills shimmer, but much slower" },
        // Projection ramp/window (used only when guided). project_every and the start/end window trade fidelity for
        // speed; start_percent skips the destructive noisy steps.
        new() { Key = "w_start",       Type = ParamType.Double, Default = 0.5, Min = 0.0, Max = 1.0, Label = "Proj weight start", Help = "Projection blend weight at the start of the window" },
        new() { Key = "w_end",         Type = ParamType.Double, Default = 1.0, Min = 0.0, Max = 1.0, Label = "Proj weight end", Help = "Projection blend weight at the end of the window" },
        new() { Key = "start_percent", Type = ParamType.Double, Default = 0.6, Min = 0.0, Max = 1.0, Label = "Project from %", Help = "Step % to begin projecting (skips the noisy early steps)" },
        new() { Key = "end_percent",   Type = ParamType.Double, Default = 1.0, Min = 0.0, Max = 1.0, Label = "Project until %", Help = "Step % to stop projecting" },
        new() { Key = "project_every", Type = ParamType.Int,    Default = 1,   Min = 1,   Max = 8,   Label = "Project every", Help = "Project every Nth step (higher = faster, less faithful)" },
    };
}
