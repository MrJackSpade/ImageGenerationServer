using System.Text.Json;

namespace ImageGen.Tests;

/// <summary>
/// A declared minimum must be a value the workflow can actually produce.
///
/// <para>These failed in two different ways, and only one of them was loud. The LTX i2v configurations declared
/// 32x32, which their graph turns into a 0.001024 MP budget for <c>ImageScaleToTotalPixels</c> — below its
/// <c>min: 0.01</c>, so ComfyUI rejected the prompt outright. Every video configuration declared
/// <c>length: 1</c>, which renders a single frame and saves as a still WEBP: nothing rejected it, the job
/// succeeded, and the result only failed later when something tried to read it as a clip. A floor that cannot
/// produce the output the workflow exists to produce is not a floor.</para>
/// </summary>
public sealed class DeclaredEnvelopeTests
{
    /// <summary>ImageScaleToTotalPixels declares megapixels min 0.01 — 10,000 pixels of area.</summary>
    private const int MinBudgetPixels = 10_000;

    [Fact]
    public void No_video_configuration_allows_a_length_that_renders_a_still()
    {
        var offenders = new List<string>();
        foreach (var (id, root) in Configurations())
        {
            if (!TryParam(root, "length", out var length)) continue;

            if (!length.TryGetProperty("min", out var min) || min.GetInt32() < 2)
                offenders.Add($"{id}: length min {(length.TryGetProperty("min", out var m) ? m.GetInt32() : 1)}");
            else if (!length.TryGetProperty("step", out var step) || step.ValueKind == JsonValueKind.Null)
                offenders.Add($"{id}: length declares no step, so the picker offers counts the sampler cannot use");
        }

        Assert.True(offenders.Count == 0,
            "A length below 2 renders one frame, which saves as a still rather than a clip — and nothing rejects "
            + "it, so the job succeeds and the failure surfaces somewhere else entirely:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void An_i2v_configuration_cannot_declare_a_size_below_the_rescale_floor()
    {
        // For image-to-video, width x height is a pixel BUDGET the source is rescaled to, not an output size.
        var offenders = new List<string>();
        foreach (var (id, root) in Configurations())
        {
            if (!id.Contains("i2v", StringComparison.OrdinalIgnoreCase)) continue;
            if (!root.TryGetProperty("resolution", out var res)) continue;

            int w = res.GetProperty("min_w").GetInt32(), h = res.GetProperty("min_h").GetInt32();
            if (w * h < MinBudgetPixels)
                offenders.Add($"{id}: {w}x{h} = {w * h} px, below the {MinBudgetPixels} px the rescale node accepts");
        }

        Assert.True(offenders.Count == 0,
            "These declare a size their graph turns into a megapixel budget below what ImageScaleToTotalPixels "
            + "accepts, so ComfyUI refuses the prompt before sampling:\n  " + string.Join("\n  ", offenders));
    }

    private static bool TryParam(JsonElement root, string key, out JsonElement param)
    {
        param = default;
        return root.TryGetProperty("params", out var ps)
               && ps.TryGetProperty(key, out param)
               && param.ValueKind == JsonValueKind.Object;
    }

    private static IEnumerable<(string Id, JsonElement Root)> Configurations()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "configurations", "workflows")))
            dir = Path.GetDirectoryName(dir);
        if (dir is null) throw new DirectoryNotFoundException("configurations/workflows not found.");

        foreach (var file in Directory.EnumerateFiles(Path.Combine(dir, "configurations", "workflows"), "*.json"))
        {
            var doc = JsonDocument.Parse(File.ReadAllText(file));
            yield return (doc.RootElement.GetProperty("id").RequireString(), doc.RootElement);
        }
    }
}
