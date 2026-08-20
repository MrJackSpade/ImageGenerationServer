using ImageGen.Application.Rendering;
using ImageGen.Application.Workflows;
using ImageGen.Comfy;
using ImageGen.Comfy.Generation.MiniMaxH3T2V;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace ImageGen.Tests;

/// <summary>The field-level alternate range contract and MiniMax-H3's recommended-versus-node-safe boundary.</summary>
public sealed class WorkflowRangeOverrideTests
{
    private static readonly string[] H3Configurations =
    [
        "minimax-h3-t2v", "minimax-h3-t2v-turbo",
        "minimax-h3-i2v", "minimax-h3-i2v-turbo",
        "minimax-h3-ref2v", "minimax-h3-ref2v-turbo",
    ];

    [Fact]
    public void Every_h3_variant_projects_the_extended_range_only_when_its_workflow_enabled_it()
    {
        WorkflowCatalog catalog = Catalog();
        WorkflowRegistry registry = Registry();

        foreach (string id in H3Configurations)
        {
            WorkflowConfiguration cfg = catalog.FindConfig(id)
                ?? throw new Xunit.Sdk.XunitException($"Missing configuration {id}");
            IWorkflow wf = registry.Find(cfg.WorkflowName)
                ?? throw new Xunit.Sdk.XunitException($"Missing workflow {cfg.WorkflowName}");
            KeyValuePair<string, ConfigParam> length = Assert.Single(
                cfg.Params, p => p.Key == WorkflowParamKeys.Length);

            WorkflowExposedParam normal = WorkflowCatalogService.ExposedParam(length, wf, cfg,
                new Dictionary<string, System.Text.Json.JsonElement>());

            Assert.Equal(WorkflowParamKeys.DurationSeconds, normal.Key);
            Assert.Equal(15.08, normal.Max);
            Assert.Equal(0.71, normal.Step);
            Assert.Null(normal.RangeOverride);

            WorkflowExposedParam enabled = WorkflowCatalogService.ExposedParam(length, wf, cfg,
                new Dictionary<string, JsonElement>
                {
                    ["allowRangeOverride.length"] = JsonSerializer.SerializeToElement(true),
                });
            WorkflowParamRangeOverride alternate = Assert.IsType<WorkflowParamRangeOverride>(enabled.RangeOverride);
            Assert.Null(alternate.Min);
            Assert.Equal(149.67, alternate.Max);
        }
    }

    [Fact]
    public void H3_extended_values_are_refused_until_the_workflow_setting_is_enabled()
    {
        WorkflowConfiguration cfg = Catalog().FindConfig("minimax-h3-t2v")
            ?? throw new Xunit.Sdk.XunitException("Missing minimax-h3-t2v");
        Dictionary<string, object?> values = H3Bag(379);

        RenderValidationException ex = Assert.Throws<RenderValidationException>(() =>
            WorkflowRangeOverridePolicy.Validate(cfg, new Dictionary<string, JsonElement>(), values));
        Assert.Contains("Allow untested lengths", ex.Message);
        Assert.Contains("workflow's settings page", ex.Message);

        WorkflowRangeOverridePolicy.Validate(cfg, new Dictionary<string, JsonElement>
        {
            ["allowRangeOverride.length"] = JsonSerializer.SerializeToElement(true),
        }, values);
    }

    [Theory]
    [InlineData(362)]  // final value in the recommended range
    [InlineData(379)]  // first cadence-valid value above it
    [InlineData(3592)] // final cadence-valid value under ComfyUI's 3600-frame node ceiling
    public void H3_accepts_recommended_and_opted_in_lengths_through_the_node_safe_boundary(int length)
    {
        MiniMaxH3Params p = ParamsCodec.Deserialize<MiniMaxH3Params>(H3Bag(length));
        Assert.Equal(length, p.Length);
    }

    [Fact]
    public void H3_rejects_a_length_past_the_node_safe_boundary()
    {
        RenderValidationException ex = Assert.Throws<RenderValidationException>(() =>
            ParamsCodec.Deserialize<MiniMaxH3Params>(H3Bag(3593)));
        Assert.Contains(WorkflowParamKeys.Length, ex.Message);
        Assert.Contains("3592", ex.Message);
    }

    private static Dictionary<string, object?> H3Bag(int length) => new()
    {
        [WorkflowParamKeys.AudioVae] = "audio.safetensors",
        [WorkflowParamKeys.Length] = length,
        [WorkflowParamKeys.Fps] = 24.0,
        [WorkflowParamKeys.Steps] = 20,
        [WorkflowParamKeys.Sampler] = "res_multistep",
        [WorkflowParamKeys.Scheduler] = "simple",
    };

    private static WorkflowCatalog Catalog() => new(
        new ComfyOptions { CatalogPath = Path.Combine(RepoRoot(), "configurations") },
        NullLogger<WorkflowCatalog>.Instance);

    private static WorkflowRegistry Registry() =>
        new ServiceCollection().AddWorkflows().BuildServiceProvider().GetRequiredService<WorkflowRegistry>();

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "configurations", "workflows")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new DirectoryNotFoundException("configurations/workflows not found.");
    }
}
