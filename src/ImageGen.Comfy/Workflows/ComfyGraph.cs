using ImageGen.Application.Rendering;
using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Comfy;

/// <summary>
/// Low-level ComfyUI graph-emit primitives shared by every workflow: node/edge construction and the
/// Forge→ComfyUI sampler/scheduler name maps. These are JSON-shape utilities, not workflow logic — each
/// workflow class builds its own topology by calling them.
/// </summary>
public static class ComfyGraph
{
    /// <summary>ComfyUI's <c>UNETLoader.weight_dtype</c> is a REQUIRED input (its validator rejects a graph that omits
    /// it), so a value must be sent. This is that enum's <c>"default"</c> option, which means "load the model's native
    /// precision — apply no cast." Named so the emitted graph reads as an explicit AUTOMATIC-precision choice rather
    /// than an unexplained literal; a configuration overrides it (e.g. <c>fp8_e4m3fn</c>) to force a cast.</summary>
    public const string AutoWeightDtype = "default";

    /// <summary>The GGUF-quantized weight file extension — the test that routes a file to the GGUF loader variant.</summary>
    public const string GgufExtension = ".gguf";

    /// <summary>The Forge scheduler name that maps to ComfyUI's <c>normal</c> — the one alias the scheduler map rewrites.</summary>
    private const string SchedulerAutomatic = "automatic";

    /// <summary>Whether a bound filename is a GGUF, and so needs the GGUF loader for its kind.</summary>
    public static bool IsGguf(string file) =>
        file.EndsWith(GgufExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The catalog's recommended settings use Forge/A1111 sampler+scheduler names; ComfyUI uses its own. Map the
    /// ones we use; pass through anything already in ComfyUI form (e.g. "dpmpp_2m", "euler", "karras").
    /// </summary>
    private static readonly Dictionary<string, string> SamplerMap = BuildSamplerMap();

    [AllowMagicStrings("Forge/A1111 sampler names mapped to their ComfyUI equivalents")]
    private static Dictionary<string, string> BuildSamplerMap() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["DPM++ 2M"] = "dpmpp_2m",
        ["DPM++ 2M Karras"] = "dpmpp_2m",
        ["DPM++ 2M SDE"] = "dpmpp_2m_sde",
        ["DPM++ SDE"] = "dpmpp_sde",
        ["DPM++ 3M SDE"] = "dpmpp_3m_sde",
        ["Euler a"] = "euler_ancestral",
        ["Euler"] = "euler",
        ["Heun"] = "heun",
        ["LMS"] = "lms",
        ["DDIM"] = "ddim",
        ["UniPC"] = "uni_pc",
        ["DPM2"] = "dpm_2"
    };

    [AllowMagicStrings("exception message naming the missing sampler setting")]
    public static string MapSampler(string? s) =>
        string.IsNullOrWhiteSpace(s)
            ? throw new RenderValidationException("This configuration has no sampler set; a sampler is required.")
            : (SamplerMap.TryGetValue(s, out string? v) ? v : s);

    [AllowMagicStrings("exception message naming the missing scheduler setting")]
    public static string MapScheduler(string? s) =>
        string.IsNullOrWhiteSpace(s)
            ? throw new RenderValidationException("This configuration has no scheduler set; a scheduler is required.")
            : (s.Equals(SchedulerAutomatic, StringComparison.OrdinalIgnoreCase) ? "normal" : s);

    public static long Seed() => Random.Shared.NextInt64(0, long.MaxValue);

    /// <summary>The generation seed from the resolved value: it if set, else a fresh random (never 0). Every sampler
    /// node uses THIS instead of rolling its own inline value, so the seed is single-sourced and persisted with the
    /// image (reproducible). A caller can pin it via a "seed" override.</summary>
    public static long Seed(long seed) => seed != 0 ? seed : Random.Shared.NextInt64(1, long.MaxValue);

    /// <summary>
    /// The diffusion-model loader node for a file, chosen BY THE FILE: a <c>.gguf</c> needs <c>UnetLoaderGGUF</c>,
    /// anything else <c>UNETLoader</c>.
    ///
    /// <para>The loader is chosen by the file, not by a configuration <c>loader</c> parameter: making it a parameter
    /// would put the quantisation of the weights into the workflow's identity, growing a second workflow per model
    /// whose only difference is which precision it loads. A workflow has no business knowing — the topology either
    /// side of the loader is identical, and which file is on the disk is the user's choice, made when they bind it.</para>
    ///
    /// <para>The text encoders work the same way: <c>CLIPLoaderGGUF</c> is picked off the same extension test.</para>
    /// </summary>
    public static ComfyNode DiffusionLoaderNode(string file, string weightDtype = AutoWeightDtype) =>
        IsGguf(file)
            ? new UnetLoaderGGUF { UnetName = file }
            : new UNETLoader { UnetName = file, WeightDtype = weightDtype };

    /// <summary>Optionally apply a model-only LoRA on top of a base model. If <paramref name="lora"/> is set, inserts a
    /// LoraLoaderModelOnly and returns its output; otherwise returns the model unchanged. Lets any workflow become a
    /// "base model + LoRA" variant just by a configuration setting the <c>lora</c> param (a Civit LoRA filename).</summary>
    public static Output<Slot.Model> ApplyLora(ComfyWorkflowGraph g, Output<Slot.Model> model, string? lora, double loraStrength, string nodeId = "90")
    {
        if (string.IsNullOrWhiteSpace(lora)) return model;
        g[nodeId] = new LoraLoaderModelOnly { Model = model, LoraName = lora, StrengthModel = loraStrength };
        return LoraLoaderModelOnly.Out(nodeId);
    }

    /// <summary>Chain the user's LoRA stack on top of a base model/clip — one <c>LoraLoader</c> per entry, starting at
    /// <paramref name="startNodeId"/> (91, 92, …). BOTH the model and the CLIP are routed through every LoRA (each at
    /// its own strength), so a style/character LoRA reaches the text encoder too. Returns the final (model, clip); an
    /// empty stack returns them unchanged and emits nothing. Node id 90 is the preset model-only LoRA
    /// (<see cref="ApplyLora"/>), so the user stack begins at 91 to avoid colliding with it or the reserved 13/35/36.</summary>
    public static (Output<Slot.Model> model, Output<Slot.Clip> clip) ApplyLoraStack(
        ComfyWorkflowGraph g, Output<Slot.Model> model, Output<Slot.Clip> clip, IReadOnlyList<LoraSelection>? loras, int startNodeId = 91)
    {
        if (loras is not { Count: > 0 }) return (model, clip);
        int nodeId = startNodeId;
        foreach (LoraSelection lora in loras)
        {
            string id = nodeId++.ToString();
            g[id] = new LoraLoader { Model = model, Clip = clip, LoraName = lora.Name, StrengthModel = lora.Weight, StrengthClip = lora.Weight };
            model = LoraLoader.ModelOut(id);
            clip = LoraLoader.ClipOut(id);
        }
        return (model, clip);
    }

    /// <summary>Compose the effective negative conditioning: the user's UI negative FIRST, then the model's OWN
    /// documented negative (the config's <c>negative</c> param). There is NO shared/implicit baseline — a model gets a
    /// negative only if its configuration deliberately sets one (relevant to that model, documented by its trainers).
    /// Either side blank yields just the other; both blank yields an empty negative (unconditioned). Comma-joined
    /// verbatim (no dedup). Single-sourced here so the generate path and every edit workflow resolve it identically.</summary>
    public static string ComposeNegative(string? modelNegative, string? userNegative)
    {
        string model = (modelNegative ?? "").Trim().TrimEnd(',').TrimEnd();
        string user = (userNegative ?? "").Trim().TrimEnd(',').TrimEnd();
        if (user.Length == 0) return model;
        if (model.Length == 0) return user;
        return user + ", " + model;
    }

    /// <summary>Normalize a requested aspect to one of the three the workflows understand (case/whitespace-insensitive).
    /// An unrecognized value is REFUSED, not silently coerced to square — a bad aspect surfaces (log + browser via the
    /// submit path's FailSlot) instead of quietly rendering the wrong shape.</summary>
    [AllowMagicStrings("exception message listing the accepted aspect names")]
    public static string NormalizeAspect(string? a)
    {
        string? norm = a?.Trim().ToLowerInvariant();
        return norm is Aspects.Square or Aspects.Landscape or Aspects.Portrait
            ? norm
            : throw new RenderValidationException($"Unrecognized aspect '{a}'. Expected one of: square, landscape, portrait.");
    }
}
