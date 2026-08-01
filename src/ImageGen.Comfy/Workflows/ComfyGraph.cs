namespace ImageGen.Comfy;

/// <summary>
/// Low-level ComfyUI graph-emit primitives shared by every workflow: node/edge construction and the
/// Forge→ComfyUI sampler/scheduler name maps. These are JSON-shape utilities, not workflow logic — each
/// workflow class builds its own topology by calling them. (Lifted verbatim from the old ComfyClient so the
/// emitted graphs stay byte-identical.)
/// </summary>
public static class ComfyGraph
{
    /// <summary>A ComfyUI node: <c>{ "class_type": ..., "inputs": ... }</c>.</summary>
    public static Dictionary<string, object> Node(string classType, object inputs) =>
        new() { ["class_type"] = classType, ["inputs"] = inputs };

    /// <summary>An edge reference to another node's output: <c>[nodeId, outputIndex]</c>.</summary>
    public static object Ref(string nodeId, int outputIndex) => new object[] { nodeId, outputIndex };

    /// <summary>
    /// The diffusion-model loader for a file, chosen BY THE FILE: a <c>.gguf</c> needs <c>UnetLoaderGGUF</c>,
    /// anything else <c>UNETLoader</c>.
    ///
    /// <para>This used to be a <c>loader</c> parameter in the configuration, which made the quantisation of the
    /// weights part of the workflow's identity — the reason the catalogue grew a second workflow per model whose
    /// only difference was which precision it loaded. A workflow has no business knowing: the topology either side
    /// of the loader is identical, and which file is on the disk is the user's choice, made when they bind it.</para>
    ///
    /// <para>The text encoders already worked this way (<c>CLIPLoaderGGUF</c> is picked off the same extension
    /// test); this is the diffusion loader catching up.</para>
    /// </summary>
    public static Dictionary<string, object> DiffusionLoader(string file, string? weightDtype = null) =>
        IsGguf(file)
            ? Node("UnetLoaderGGUF", new { unet_name = file })
            : Node("UNETLoader", new { unet_name = file, weight_dtype = weightDtype ?? "default" });

    /// <summary>Whether a bound filename is a GGUF, and so needs the GGUF loader for its kind.</summary>
    public static bool IsGguf(string? file) =>
        (file ?? "").EndsWith(".gguf", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The catalog's recommended settings use Forge/A1111 sampler+scheduler names; ComfyUI uses its own. Map the
    /// ones we use; pass through anything already in ComfyUI form (e.g. "dpmpp_2m", "euler", "karras").
    /// </summary>
    private static readonly Dictionary<string, string> SamplerMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DPM++ 2M"] = "dpmpp_2m", ["DPM++ 2M Karras"] = "dpmpp_2m", ["DPM++ 2M SDE"] = "dpmpp_2m_sde",
        ["DPM++ SDE"] = "dpmpp_sde", ["DPM++ 3M SDE"] = "dpmpp_3m_sde", ["Euler a"] = "euler_ancestral",
        ["Euler"] = "euler", ["Heun"] = "heun", ["LMS"] = "lms", ["DDIM"] = "ddim", ["UniPC"] = "uni_pc", ["DPM2"] = "dpm_2"
    };

    public static string MapSampler(string? s) =>
        string.IsNullOrWhiteSpace(s) ? "euler" : (SamplerMap.TryGetValue(s, out var v) ? v : s);

    public static string MapScheduler(string? s) =>
        string.IsNullOrWhiteSpace(s) ? "normal" : (s.Equals("automatic", StringComparison.OrdinalIgnoreCase) ? "normal" : s);

    public static long Seed() => Random.Shared.NextInt64(0, long.MaxValue);

    /// <summary>The generation's seed, read from the resolved parameter set — where it was materialized ONCE (see
    /// <c>ComfyClient.MergeParamsDict</c>). Every sampler node uses THIS instead of rolling its own inline value, so
    /// the seed is single-sourced and persisted with the image (reproducible). A caller can pin it via a "seed"
    /// override. Defensive random fallback only if a param set somehow lacks it.</summary>
    public static long Seed(ParamValues p)
    {
        var s = p.Long("seed");
        return s != 0 ? s : Random.Shared.NextInt64(1, long.MaxValue);
    }

    /// <summary>Optionally apply a model-only LoRA on top of a base model. If the <c>lora</c> param is set, inserts
    /// a LoraLoaderModelOnly and returns its output; otherwise returns the model unchanged. Lets any workflow become
    /// a "base model + LoRA" variant just by a configuration setting the <c>lora</c> param (a Civit LoRA filename).</summary>
    public static object ApplyLora(Dictionary<string, object> wf, object model, ParamValues p, string nodeId = "90")
    {
        var lora = p.Str("lora");
        if (string.IsNullOrWhiteSpace(lora)) return model;
        wf[nodeId] = Node("LoraLoaderModelOnly", new { model, lora_name = lora, strength_model = p.Dbl("lora_strength", 1.0) });
        return Ref(nodeId, 0);
    }

    /// <summary>The standard quality/anatomy negative used by the txt2img workflows when a model supports negatives
    /// and its config declares no model-specific default.</summary>
    public const string DefaultNegative =
        "lowres, bad anatomy, bad hands, missing fingers, extra digit, cropped, worst quality, low quality, jpeg artifacts, blurry, text, watermark, signature";

    /// <summary>Compose the effective negative conditioning: the user's UI negative FIRST, then the model's DEFAULT
    /// negative (a config <c>negative</c> param, or the shared <see cref="DefaultNegative"/> when the config declares
    /// none). The user's tags lead so they aren't buried behind the baseline quality tags; the default is always
    /// present (never replaced). A blank user negative yields just the default; a blank default yields just the
    /// user's. Tags are comma-joined verbatim (no dedup). Single-sourced here so the generate path
    /// (<c>ApplyGenPromptRules</c>) and every edit workflow resolve the negative identically.</summary>
    public static string ComposeNegative(string? modelDefault, string? userNegative)
    {
        var baseNeg = (string.IsNullOrWhiteSpace(modelDefault) ? DefaultNegative : modelDefault).Trim();
        var user = userNegative?.Trim();
        if (string.IsNullOrEmpty(user)) return baseNeg;
        if (baseNeg.Length == 0) return user;
        return user.TrimEnd(',').TrimEnd() + ", " + baseNeg.TrimEnd(',').TrimEnd();
    }

    /// <summary>Normalize a requested aspect to one of the three the workflows understand.</summary>
    public static string NormalizeAspect(string? a)
    {
        a = a?.Trim().ToLowerInvariant();
        return a is "landscape" or "portrait" ? a : "square";
    }
}
