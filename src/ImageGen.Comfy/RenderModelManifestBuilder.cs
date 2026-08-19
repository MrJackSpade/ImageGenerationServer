using ImageGen.Domain.Entities;
using ImageGen.Domain.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ImageGen.Comfy;

/// <summary>Builds the immutable, user-visible model manifest from the same merged params and resolved bindings used
/// to build the graph. It never opens model files: ComfyUI may be remote, so quantization beyond an explicit loader
/// dtype is deliberately only a filename-derived hint.</summary>
public static partial class RenderModelManifestBuilder
{
    public static RenderModelManifest? Build(IReadOnlyDictionary<string, object?> parameters, ResolvedRequirements req)
    {
        string? checkpoint = Basename(req.Checkpoint);
        string? vae = Basename(req.Vae);
        string[] encoders = [.. req.TextEncoders.Select(Basename).OfType<string>()];
        if (checkpoint is null && vae is null && encoders.Length == 0)
        {
            return null;
        }

        return new RenderModelManifest
        {
            Checkpoint = checkpoint,
            Loader = Text(parameters, WorkflowParamKeys.Loader) ?? "workflow-defined",
            WeightDtype = Text(parameters, WorkflowParamKeys.WeightDtype) ?? ComfyGraph.Loading.AutoWeightDtype,
            Quantization = QuantizationHint(checkpoint),
            Vae = vae,
            TextEncoders = encoders,
        };
    }

    /// <summary>Only the portable basename is retained; bindings may contain either slash style.</summary>
    private static string? Basename(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string normalized = path.Replace('\\', '/');
        return normalized[(normalized.LastIndexOf('/') + 1)..];
    }

    private static string? Text(IReadOnlyDictionary<string, object?> parameters, string key)
    {
        if (!parameters.TryGetValue(key, out object? raw) || raw is null)
        {
            return null;
        }

        return raw switch
        {
            string s when !string.IsNullOrWhiteSpace(s) => s,
            JsonElement { ValueKind: JsonValueKind.String } e when !string.IsNullOrWhiteSpace(e.GetString()) => e.GetString(),
            _ => null,
        };
    }

    [AllowMagicStrings("published precision tokens embedded in model filenames")]
    private static string QuantizationHint(string? checkpoint)
    {
        if (checkpoint is null)
        {
            return "unknown";
        }

        string stem = checkpoint.ToLowerInvariant();
        if (stem.Contains("int8", StringComparison.Ordinal) && stem.Contains("convrot", StringComparison.Ordinal))
        {
            return "int8-convrot";
        }

        foreach ((string token, string label) in new[]
        {
            ("bf16", "bf16"), ("bfloat16", "bf16"), ("fp16", "fp16"), ("float16", "fp16"),
            ("fp8", "fp8"), ("int8", "int8"),
        })
        {
            if (stem.Contains(token, StringComparison.Ordinal))
            {
                return label;
            }
        }

        Match q = GgufQuantization().Match(stem);
        return q.Success ? q.Groups[1].Value.Replace('-', '_') : "unknown";
    }

    [GeneratedRegex(@"(?:^|[-_.])(q[2-8](?:[-_][a-z0-9]+)*)(?:[-_.]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex GgufQuantization();
}
