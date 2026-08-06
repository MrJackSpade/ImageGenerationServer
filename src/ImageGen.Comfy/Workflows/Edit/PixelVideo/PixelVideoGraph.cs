using ImageGen.Application.Rendering;
using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Comfy.Edit.PixelVideo;

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
/// <para>Typed (#93): the inner i2v bases all emit typed <see cref="ComfyNode"/>s now, so the splices locate a node by
/// its <c>class_type</c> and rewrite an immutable record with <c>with</c> — reading and rerouting the one edge, leaving
/// every other input untouched, so the emitted graph is byte-identical to the hand-built dictionary this replaced. A
/// node matched by class_type whose concrete record shape isn't handled is REFUSED, not silently skipped.</para>
/// </summary>
[AllowMagicStrings("exception-message fragments naming the unhandled pixel-video splice operation")]
internal static class PixelVideoGraph
{
    /// <summary>Node ids named by role. Only the inserted quantize node has a fixed id; the per-consumer projection ids
    /// are computed (710+). Value preserved exactly.</summary>
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
    public static void QuantizeFrames(ComfyWorkflowGraph g, PixelVideoParams p, string quantNodeId = Nodes.Quantize)
    {
        if (FindByClassType(g, ComfyNodeTypes.SaveAnimatedWEBP) is not (string saveId, ComfyNode saveNode))
        {
            return;
        }

        Output<Slot.Image> imagesSrc = ReadImages(saveNode);

        (int gw, int gh) = Grid(p);
        g[quantNodeId] = new global::ImageGen.Comfy.PixelQuantize
        {
            Image = imagesSrc,
            GridW = gw,
            GridH = gh,
            Palette = p.Palette,
            Method = p.Method,
            VirtualResolution = p.VirtualResolution,
        };
        g[saveId] = WithImages(saveNode, global::ImageGen.Comfy.PixelQuantize.Out(quantNodeId));
    }

    /// <summary>Insert a <c>PixelManifoldProjection</c> in front of every terminal model-consumer's <c>model</c> input,
    /// so the per-step projection runs for each (both experts of an MoE). The VAE is taken from the decode node.</summary>
    public static void PatchModelProjection(ComfyWorkflowGraph g, PixelVideoParams p)
    {
        if (FindByClassType(g, ComfyNodeTypes.VAEDecode, ComfyNodeTypes.VAEDecodeTiled) is not (_, ComfyNode decode))
        {
            return;
        }

        Output<Slot.Vae> vae = ReadVae(decode);

        (int gw, int gh) = Grid(p);
        List<(string id, ComfyNode node)> consumers = g.Nodes
            .Where(kv => kv.Value is ComfyNode n && ModelConsumers.Contains(n.ClassType))
            .Select(kv => (kv.Key, (ComfyNode)kv.Value))
            .ToList();

        int next = 710;
        foreach ((string id, ComfyNode node) in consumers)
        {
            Output<Slot.Model> modelSrc = ReadModel(node);
            string projId = next++.ToString();
            g[projId] = new PixelManifoldProjection
            {
                Model = modelSrc,
                Vae = vae,
                GridW = gw,
                GridH = gh,
                Palette = p.Palette,
                Method = p.Method,
                WStart = p.RequiredWStart(),
                WEnd = p.RequiredWEnd(),
                StartPercent = p.RequiredStartPercent(),
                EndPercent = p.RequiredEndPercent(),
                ProjectEvery = p.RequiredProjectEvery(),
                VirtualResolution = p.VirtualResolution,
            };
            g[id] = WithModel(node, PixelManifoldProjection.Out(projId));
        }
    }

    /// <summary>The quantize grid: explicit grid_w/grid_h (both required by the pixel-video configs).</summary>
    private static (int gw, int gh) Grid(PixelVideoParams p) => (p.GridW, p.GridH);

    /// <summary>The first node (in insertion order) whose <c>class_type</c> is one of <paramref name="classTypes"/>, or
    /// null. Only typed <see cref="ComfyNode"/>s are considered — the inner i2v bases emit nothing else.</summary>
    private static (string id, ComfyNode node)? FindByClassType(ComfyWorkflowGraph g, params string[] classTypes)
    {
        foreach (KeyValuePair<string, ComfyNode> kv in g.Nodes)
        {
            if (classTypes.Contains(kv.Value.ClassType))
            {
                return (kv.Key, kv.Value);
            }
        }

        return null;
    }

    /// <summary>The frames edge feeding a save node (only the animated-WEBP saves the video bases emit).</summary>
    private static Output<Slot.Image> ReadImages(ComfyNode n) => n switch
    {
        SaveAnimatedWEBPLiteralFps s => s.Images,
        SaveAnimatedWEBP s => s.Images,
        _ => throw Unhandled("reroute the frames of", n),
    };

    /// <summary>The save node with its frames rerouted through the quantizer.</summary>
    private static ComfyNode WithImages(ComfyNode n, Output<Slot.Image> images) => n switch
    {
        SaveAnimatedWEBPLiteralFps s => s with { Images = images },
        SaveAnimatedWEBP s => s with { Images = images },
        _ => throw Unhandled("reroute the frames of", n),
    };

    /// <summary>The VAE edge feeding a decode node.</summary>
    private static Output<Slot.Vae> ReadVae(ComfyNode n) => n switch
    {
        VAEDecode d => d.Vae,
        VAEDecodeTiled d => d.Vae,
        _ => throw Unhandled("read the VAE of", n),
    };

    /// <summary>The model edge feeding a terminal consumer.</summary>
    private static Output<Slot.Model> ReadModel(ComfyNode n) => n switch
    {
        KSampler k => k.Model,
        KSamplerAdvanced k => k.Model,
        SamplerCustom k => k.Model,
        CFGGuider k => k.Model,
        BasicGuider k => k.Model,
        _ => throw Unhandled("read the model of", n),
    };

    /// <summary>The consumer with its <c>model</c> input rerouted through the projection.</summary>
    private static ComfyNode WithModel(ComfyNode n, Output<Slot.Model> model) => n switch
    {
        KSampler k => k with { Model = model },
        KSamplerAdvanced k => k with { Model = model },
        SamplerCustom k => k with { Model = model },
        CFGGuider k => k with { Model = model },
        BasicGuider k => k with { Model = model },
        _ => throw Unhandled("reroute the model of", n),
    };

    private static RenderValidationException Unhandled(string what, ComfyNode n) =>
        new($"The pixel-video decorator cannot {what} a '{n.ClassType}' node — its record shape is not handled.");

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
        new() { Key = WorkflowParamKeys.Method,  Type = ParamType.Enum, Choices = ComfyWidgetChoices.PixelizeMethods, Label = "Cell method", Help = "median = crisp + straight edges; box = smoother" },
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
