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
    /// <summary>Node ids named by role. Only the inserted quantize node has a fixed id (default for
    /// <see cref="QuantizeFrames"/>'s parameter); the per-consumer projection ids are computed (710+). Value preserved
    /// exactly.</summary>
    private static class Nodes
    {
        public const string Quantize = "700";
    }

    /// <summary>Terminal model-consumers whose <c>model</c> input drives the actual denoise (and whose post-CFG hook
    /// the projection rides). NOT schedulers/ModelSampling* (those use the model only for sigmas).</summary>
    private static readonly HashSet<string> ModelConsumers = new(StringComparer.Ordinal)
    {
        ComfyNodeTypes.KSampler, ComfyNodeTypes.KSamplerAdvanced, ComfyNodeTypes.SamplerCustom,
        ComfyNodeTypes.CFGGuider, ComfyNodeTypes.BasicGuider,
    };

    /// <summary>Reroute the animated-WEBP save node's frames through a <c>PixelQuantize</c> (its exact still-pixelizer
    /// params + defaults). The quantizer flattens the <c>(B,T,H,W,3)</c> video decode into per-frame batches itself.</summary>
    public static void QuantizeFrames(Dictionary<string, object> wf, ParamValues p, string quantNodeId = Nodes.Quantize)
    {
        var save = wf.Values.OfType<Dictionary<string, object>>()
            .FirstOrDefault(n => n.TryGetValue(ComfyGraphKeys.ClassType, out var ct) && ct as string == ComfyNodeTypes.SaveAnimatedWEBP);
        if (save is null) return;
        var inputs = AsInputDict(save[ComfyGraphKeys.Inputs]);
        if (!inputs.TryGetValue(ComfyGraphKeys.Images, out var imagesSrc)) return;

        var (gw, gh) = Grid(p);
        wf[quantNodeId] = ComfyGraph.Node(ComfyNodeTypes.PixelQuantize, new
        {
            image = imagesSrc,
            grid_w = gw,
            grid_h = gh,
            palette = p.StrReq(WorkflowParamKeys.Palette),
            method = p.StrReq(WorkflowParamKeys.Method),
            virtual_resolution = p.IntReq(WorkflowParamKeys.VirtualResolution),
        });
        inputs[ComfyGraphKeys.Images] = ComfyGraph.Ref(quantNodeId, 0);
        save[ComfyGraphKeys.Inputs] = inputs;
    }

    /// <summary>Insert a <c>PixelManifoldProjection</c> in front of every terminal model-consumer's <c>model</c> input,
    /// so the per-step projection runs for each (both experts of an MoE). The VAE is taken from the decode node.</summary>
    public static void PatchModelProjection(Dictionary<string, object> wf, ParamValues p)
    {
        var decode = wf.Values.OfType<Dictionary<string, object>>()
            .FirstOrDefault(n => n.TryGetValue(ComfyGraphKeys.ClassType, out var ct)
                                 && ct as string is ComfyNodeTypes.VAEDecode or ComfyNodeTypes.VAEDecodeTiled);
        if (decode is null) return;
        var decInputs = AsInputDict(decode[ComfyGraphKeys.Inputs]);
        if (!decInputs.TryGetValue(ComfyGraphKeys.Vae, out var vae) || vae is null) return;

        var (gw, gh) = Grid(p);
        var consumers = wf.Values.OfType<Dictionary<string, object>>()
            .Where(n => n.TryGetValue(ComfyGraphKeys.ClassType, out var ct) && ct is string s && ModelConsumers.Contains(s))
            .ToList();

        int next = 710;
        foreach (var node in consumers)
        {
            var inputs = AsInputDict(node[ComfyGraphKeys.Inputs]);
            if (!inputs.TryGetValue(ComfyGraphKeys.Model, out var modelSrc) || modelSrc is null) continue;
            var projId = (next++).ToString();
            wf[projId] = ComfyGraph.Node(ComfyNodeTypes.PixelManifoldProjection, new
            {
                model = modelSrc,
                vae,
                grid_w = gw,
                grid_h = gh,
                palette = p.StrReq(WorkflowParamKeys.Palette),
                method = p.StrReq(WorkflowParamKeys.Method),
                w_start = p.DblReq(WorkflowParamKeys.WStart),
                w_end = p.DblReq(WorkflowParamKeys.WEnd),
                start_percent = p.DblReq(WorkflowParamKeys.StartPercent),
                end_percent = p.DblReq(WorkflowParamKeys.EndPercent),
                project_every = p.IntReq(WorkflowParamKeys.ProjectEvery),
                virtual_resolution = p.IntReq(WorkflowParamKeys.VirtualResolution),
            });
            inputs[ComfyGraphKeys.Model] = ComfyGraph.Ref(projId, 0);
            node[ComfyGraphKeys.Inputs] = inputs;
        }
    }

    /// <summary>The quantize grid: explicit grid_w/grid_h, falling back to 384×256 (PixelQuantizeWorkflow's default).</summary>
    private static (int gw, int gh) Grid(ParamValues p)
    {
        int gw = p.IntReq(WorkflowParamKeys.GridW);
        int gh = p.IntReq(WorkflowParamKeys.GridH);
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
        new() { Key = WorkflowParamKeys.VirtualResolution, Type = ParamType.Int, Min = 0, Max = 4096, Label = "Virtual res", Help = "Sprite pixel count on its longest edge" },
        new() { Key = WorkflowParamKeys.GridW, Type = ParamType.Int, Min = 0, Max = 4096, Label = "Grid width" },
        new() { Key = WorkflowParamKeys.GridH, Type = ParamType.Int, Min = 0, Max = 4096, Label = "Grid height" },
        new() { Key = WorkflowParamKeys.Palette, Type = ParamType.String, Label = "Palette", Help = "A locked (named) palette is temporally consistent — no frame-to-frame flicker" },
        new() { Key = WorkflowParamKeys.Method,  Type = ParamType.Enum, Choices = new[] { "median", "mode", "box", "nearest_present", "mean_srgb", "mean_linear", "mean_oklab", "lanczos", "var_hybrid", "supersample_mode" }, Label = "Cell method", Help = "median = crisp + straight edges; box = smoother" },
        // The toggle: false = fast post-quantize only; true = also project the latent onto the manifold every step
        // (pixels baked into the motion, no shimmer — much slower).
        new() { Key = WorkflowParamKeys.Guided, Type = ParamType.Bool, Label = "Pixel-guided", Help = "Project the latent every step — kills shimmer, but much slower" },
        // Projection ramp/window (used only when guided). project_every and the start/end window trade fidelity for
        // speed; start_percent skips the destructive noisy steps.
        new() { Key = WorkflowParamKeys.WStart,       Type = ParamType.Double, Min = 0.0, Max = 1.0, Label = "Proj weight start", Help = "Projection blend weight at the start of the window" },
        new() { Key = WorkflowParamKeys.WEnd,         Type = ParamType.Double, Min = 0.0, Max = 1.0, Label = "Proj weight end", Help = "Projection blend weight at the end of the window" },
        new() { Key = WorkflowParamKeys.StartPercent, Type = ParamType.Double, Min = 0.0, Max = 1.0, Label = "Project from %", Help = "Step % to begin projecting (skips the noisy early steps)" },
        new() { Key = WorkflowParamKeys.EndPercent,   Type = ParamType.Double, Min = 0.0, Max = 1.0, Label = "Project until %", Help = "Step % to stop projecting" },
        new() { Key = WorkflowParamKeys.ProjectEvery, Type = ParamType.Int,    Min = 1,   Max = 8,   Label = "Project every", Help = "Project every Nth step (higher = faster, less faithful)" },
    };
}
