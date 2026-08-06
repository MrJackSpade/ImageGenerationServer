using ImageGen.Application.Rendering;
using ImageGen.Comfy;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace ImageGen.Tests;

/// <summary>
/// Server-side workflow-parameter bounds (#102). The bound is DECLARED once — as a <c>[Range]</c> attribute on the
/// typed params DTO, fed by the same <see cref="ParamBounds"/> constant the UI schema reads — and ENFORCED reflectively
/// at the one typed boundary every submission crosses (<see cref="ParamsCodec.Deserialize{T}"/>). So a value sent past
/// the UI slider (via API/MCP) is refused before the graph is built, naming the value and its range. The resolved
/// render size is checked against the model's documented envelope by the same guard the settings-write path uses.
/// </summary>
public sealed class ParamBoundsValidationTests
{

    [Fact]
    public void Steps_beyond_the_ceiling_is_refused_naming_the_value()
    {
        RenderValidationException ex = Assert.Throws<RenderValidationException>(() =>
            ParamsCodec.Deserialize<SubmissionCommon>(new Dictionary<string, object?> { [WorkflowParamKeys.Steps] = 5000 }));

        Assert.Contains(WorkflowParamKeys.Steps, ex.Message);
        Assert.Contains("5000", ex.Message);
        Assert.Contains(ParamBounds.StepsMax.ToString(), ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Steps_below_one_is_refused(int bad) =>
        Assert.Throws<RenderValidationException>(() =>
            ParamsCodec.Deserialize<SubmissionCommon>(new Dictionary<string, object?> { [WorkflowParamKeys.Steps] = bad }));

    [Fact]
    public void Steps_in_range_passes()
    {
        SubmissionCommon c = ParamsCodec.Deserialize<SubmissionCommon>(
            new Dictionary<string, object?> { [WorkflowParamKeys.Steps] = 20 });
        Assert.Equal(20, c.Steps);
    }

    [Fact]
    public void An_absent_optional_param_is_not_treated_as_out_of_range()
    {
        // No steps/cfg supplied: "unspecified" is not "out of range" — the guard must not fire on a null member.
        SubmissionCommon c = ParamsCodec.Deserialize<SubmissionCommon>(new Dictionary<string, object?>());
        Assert.Null(c.Steps);
        Assert.Null(c.Cfg);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(99.0)]
    public void Cfg_outside_the_range_is_refused(double bad) =>
        Assert.Throws<RenderValidationException>(() =>
            ParamsCodec.Deserialize<SubmissionCommon>(new Dictionary<string, object?> { [WorkflowParamKeys.Cfg] = bad }));

    [Fact]
    public void Cfg_at_the_distilled_floor_of_one_passes()
    {
        SubmissionCommon c = ParamsCodec.Deserialize<SubmissionCommon>(
            new Dictionary<string, object?> { [WorkflowParamKeys.Cfg] = 1.0 });
        Assert.Equal(1.0, c.Cfg);
    }

    [Fact]
    public void Generate_lora_strength_beyond_its_ceiling_is_refused()
    {
        RenderValidationException ex = Assert.Throws<RenderValidationException>(() =>
            ParamsCodec.Deserialize<Txt2ImgParams>(BaseGenBag(loraStrength: ParamBounds.GenLoraStrengthMax + 1)));
        Assert.Contains(WorkflowParamKeys.LoraStrength, ex.Message);
    }

    [Fact]
    public void Generate_lora_strength_in_range_passes()
    {
        Txt2ImgParams p = ParamsCodec.Deserialize<Txt2ImgParams>(BaseGenBag(loraStrength: 1.0));
        Assert.Equal(1.0, p.LoraStrength);
    }

    /// <summary>A minimally valid txt2img bag: the DTO's <c>required</c> members present, so deserialization gets past
    /// STJ and the range validator is what runs.</summary>
    private static Dictionary<string, object?> BaseGenBag(double loraStrength) => new()
    {
        [WorkflowParamKeys.Steps] = 20,
        [WorkflowParamKeys.Sampler] = "euler",
        [WorkflowParamKeys.Scheduler] = "normal",
        [WorkflowParamKeys.LoraStrength] = loraStrength,
    };


    /// <summary>The strength/denoise floor is 0 (#104): a denoise of 0 is a valid "don't change" input, no longer
    /// clamped up to an arbitrary positive minimum. The submit guard accepts it where it used to reject it.</summary>
    [Fact]
    public void A_denoise_of_zero_is_accepted_not_rejected_as_below_an_arbitrary_floor()
    {
        AnimaInpaintParams p = ParamsCodec.Deserialize<AnimaInpaintParams>(InpaintBag(denoise: 0.0));
        Assert.Equal(0.0, p.Denoise);
    }

    [Theory]
    [InlineData(-0.1)]   // still below 0 — genuinely out of range
    [InlineData(1.5)]    // still above the full-redraw ceiling
    public void A_denoise_outside_zero_to_one_is_still_refused(double bad) =>
        Assert.Throws<RenderValidationException>(() =>
            ParamsCodec.Deserialize<AnimaInpaintParams>(InpaintBag(denoise: bad)));

    /// <summary>A minimally valid anima-inpaint bag: every <c>required</c> member present so STJ is satisfied and the
    /// range validator is what runs on <c>denoise</c>.</summary>
    private static Dictionary<string, object?> InpaintBag(double denoise) => new()
    {
        [WorkflowParamKeys.Loader] = "checkpoint",
        [WorkflowParamKeys.Steps] = 20,
        [WorkflowParamKeys.Cfg] = 6.0,
        [WorkflowParamKeys.Sampler] = "euler",
        [WorkflowParamKeys.Scheduler] = "normal",
        [WorkflowParamKeys.Denoise] = denoise,
        [WorkflowParamKeys.MaskGrow] = 0,
    };


    private static readonly ModelResolution Env = new() { MinW = 512, MinH = 512, MaxW = 1536, MaxH = 1536, Step = 16 };

    [Fact]
    public void A_size_within_the_envelope_passes() => ResolutionGuard.EnsureWithin(Env, 1024, 1024);

    [Theory]
    [InlineData(4096, 1024)]   // too wide
    [InlineData(1024, 64)]     // too short
    public void A_size_outside_the_envelope_is_refused(int w, int h) =>
        Assert.Throws<RenderValidationException>(() => ResolutionGuard.EnsureWithin(Env, w, h));

    [Fact]
    public void A_size_off_the_step_multiple_is_refused() =>
        Assert.Throws<RenderValidationException>(() => ResolutionGuard.EnsureWithin(Env, 1000, 1024));

    [Fact]
    public void A_null_envelope_is_not_second_guessed() => ResolutionGuard.EnsureWithin(null, 99999, 99999);

    /// <summary>RenderSizeViolation is the submit-path shape the catalog's request-size check (a composer Custom size)
    /// delegates to, so /enqueue can refuse an unsupported width/height up front with the model's own numbers.</summary>
    [Fact]
    public void A_valid_render_size_reports_no_violation() => Assert.Null(ResolutionGuard.RenderSizeViolation(Env, 1024, 1024));

    [Fact]
    public void A_null_envelope_reports_no_violation() => Assert.Null(ResolutionGuard.RenderSizeViolation(null, 99999, 99999));

    [Fact]
    public void An_out_of_envelope_render_size_is_reported_with_the_models_numbers()
    {
        string? msg = ResolutionGuard.RenderSizeViolation(Env, 4096, 1024);
        Assert.NotNull(msg);
        Assert.Contains("1536", msg);   // the model's own max, named in the refusal
    }


    [Fact]
    public void No_configuration_declares_a_bounded_param_outside_its_workflow_schema()
    {
        WorkflowRegistry registry = Registry();
        List<string> offenders = [];

        foreach ((string id, JsonElement root) in Configurations())
        {
            IWorkflow? wf = registry.Find(root.TryGetProperty("workflow", out JsonElement w) ? w.GetString() : null);
            if (wf is null || !root.TryGetProperty("params", out JsonElement ps))
            {
                continue;
            }

            foreach (ParamSpec spec in wf.Schema)
            {
                if ((spec.Min is null && spec.Max is null) || !ps.TryGetProperty(spec.Key, out JsonElement p))
                {
                    continue;
                }

                // The effective value (bare scalar or {value,min,max} envelope) must sit within the schema bound,
                // and a per-config min/max override may only TIGHTEN it — never widen past what the server enforces.
                (double? value, double? cmin, double? cmax) = ReadNumeric(p);
                if (value is double v && Outside(v, spec.Min, spec.Max))
                {
                    offenders.Add($"{id}.{spec.Key}: value {v} outside schema [{spec.Min}, {spec.Max}]");
                }

                if (cmin is double lo && spec.Min is double smin && lo < smin)
                {
                    offenders.Add($"{id}.{spec.Key}: min {lo} below schema min {smin}");
                }

                if (cmax is double hi && spec.Max is double smax && hi > smax)
                {
                    offenders.Add($"{id}.{spec.Key}: max {hi} above schema max {smax}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "A configuration declares a bounded param the server would reject, or a slider range wider than the "
            + "enforced range — so a user could pick a value the submit guard then refuses:\n  "
            + string.Join("\n  ", offenders));
    }

    private static bool Outside(double v, double? min, double? max) =>
        (min is double lo && v < lo) || (max is double hi && v > hi);

    private static (double? Value, double? Min, double? Max) ReadNumeric(JsonElement p)
    {
        if (p.ValueKind == JsonValueKind.Number)
        {
            return (p.GetDouble(), null, null);
        }

        if (p.ValueKind == JsonValueKind.Object)
        {
            return (
                p.TryGetProperty("value", out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null,
                p.TryGetProperty("min", out JsonElement mn) && mn.ValueKind == JsonValueKind.Number ? mn.GetDouble() : null,
                p.TryGetProperty("max", out JsonElement mx) && mx.ValueKind == JsonValueKind.Number ? mx.GetDouble() : null);
        }

        return (null, null, null);
    }

    private static WorkflowRegistry Registry() =>
        new ServiceCollection().AddWorkflows().BuildServiceProvider().GetRequiredService<WorkflowRegistry>();

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

        foreach (string f in Directory.EnumerateFiles(Path.Combine(dir, "configurations", "workflows"), "*.json"))
        {
            JsonDocument doc = JsonDocument.Parse(File.ReadAllText(f));
            yield return (doc.RootElement.GetProperty("id").GetString() ?? f, doc.RootElement.Clone());
        }
    }
}
