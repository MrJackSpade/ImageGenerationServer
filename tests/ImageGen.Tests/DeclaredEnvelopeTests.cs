using ImageGen.Comfy;
using System.Text.Json;

namespace ImageGen.Tests;

/// <summary>
/// A declared minimum must be a value the workflow can actually produce.
///
/// <para>A declared minimum can be wrong in two ways, and only one of them is loud. A size below the rescale
/// floor — 32x32 turns into a 0.001024 MP budget for <c>ImageScaleToTotalPixels</c>, below its
/// <c>min: 0.01</c> — makes ComfyUI reject the prompt outright. A <c>length: 1</c> renders a single frame and
/// saves as a still WEBP: nothing rejects it, the job succeeds, and the result only fails later when something
/// tries to read it as a clip. A floor that cannot produce the output the workflow exists to produce is not a
/// floor.</para>
/// </summary>
public sealed class DeclaredEnvelopeTests
{
    /// <summary>ImageScaleToTotalPixels declares megapixels min 0.01 — 10,000 pixels of area.</summary>
    private const int MinBudgetPixels = 10_000;

    /// <summary>
    /// Krea's official ComfyUI workflow describes its supported range as 1–2 megapixels, not a 1024px minimum on
    /// each side. The shipped ~1MP portrait/landscape presets therefore establish the 768px short-edge floor used by
    /// these configurations. Every sibling must keep its literal quick sizes inside that envelope, and the coupled
    /// megapixel resolver must leave the defaults alone rather than independently clamping and distorting them.
    /// Source: https://docs.comfy.org/tutorials/image/krea/krea-2
    /// </summary>
    [Theory]
    [InlineData("krea2")]
    [InlineData("krea2-turbo")]
    [InlineData("krea2-refine")]
    public void Krea_generation_presets_fit_their_envelope_without_reshaping(string configId)
    {
        JsonElement root = Configurations().Single(x => x.Id == configId).Root;
        JsonElement resolution = root.GetProperty("resolution");
        ModelResolution envelope = new()
        {
            MinW = resolution.GetProperty("min_w").GetInt32(),
            MinH = resolution.GetProperty("min_h").GetInt32(),
            MaxW = resolution.GetProperty("max_w").GetInt32(),
            MaxH = resolution.GetProperty("max_h").GetInt32(),
            Step = resolution.GetProperty("step").GetInt32(),
        };
        JsonElement parameters = root.GetProperty("params");
        double megapixels = parameters.GetProperty("megapixels").GetProperty("value").GetDouble();

        Assert.Equal((768, 768), (envelope.MinW, envelope.MinH));
        foreach (JsonProperty aspect in parameters.GetProperty("aspect").EnumerateObject())
        {
            int width = aspect.Value[0].GetInt32(), height = aspect.Value[1].GetInt32();
            Assert.Null(ResolutionGuard.Violation(envelope, width, height, aspect.Name));
            Assert.Equal((width, height), RenderSizing.Resolve((width, height), megapixels, envelope));
        }

        Assert.Null(ResolutionGuard.RenderSizeViolation(envelope, 768, 1280));
        Assert.NotNull(ResolutionGuard.RenderSizeViolation(envelope, 752, 1280));
    }

    [Fact]
    public void No_video_configuration_allows_a_length_that_renders_a_still()
    {
        List<string> offenders = [];
        foreach ((string? id, JsonElement root) in Configurations())
        {
            if (!TryParam(root, "length", out JsonElement length))
            {
                continue;
            }

            if (!length.TryGetProperty("min", out JsonElement min) || min.GetInt32() < 2)
            {
                offenders.Add($"{id}: length min {(length.TryGetProperty("min", out JsonElement m) ? m.GetInt32() : 1)}");
            }
            else if (!length.TryGetProperty("step", out JsonElement step) || step.ValueKind == JsonValueKind.Null)
            {
                offenders.Add($"{id}: length declares no step, so the picker offers counts the sampler cannot use");
            }
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
        List<string> offenders = [];
        foreach ((string? id, JsonElement root) in Configurations())
        {
            if (!id.Contains("i2v", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!root.TryGetProperty("resolution", out JsonElement res))
            {
                continue;
            }

            int w = res.GetProperty("min_w").GetInt32(), h = res.GetProperty("min_h").GetInt32();
            if (w * h < MinBudgetPixels)
            {
                offenders.Add($"{id}: {w}x{h} = {w * h} px, below the {MinBudgetPixels} px the rescale node accepts");
            }
        }

        Assert.True(offenders.Count == 0,
            "These declare a size their graph turns into a megapixel budget below what ImageScaleToTotalPixels "
            + "accepts, so ComfyUI refuses the prompt before sampling:\n  " + string.Join("\n  ", offenders));
    }

    private static bool TryParam(JsonElement root, string key, out JsonElement param)
    {
        param = default;
        return root.TryGetProperty("params", out JsonElement ps)
               && ps.TryGetProperty(key, out param)
               && param.ValueKind == JsonValueKind.Object;
    }

    private static IEnumerable<(string Id, JsonElement Root)> Configurations()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "configurations", "workflows")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        if (dir is null)
        {
            throw new DirectoryNotFoundException("configurations/workflows not found.");
        }

        foreach (string file in Directory.EnumerateFiles(Path.Combine(dir, "configurations", "workflows"), "*.json"))
        {
            JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
            yield return (doc.RootElement.GetProperty("id").RequireString(), doc.RootElement);
        }
    }
}
