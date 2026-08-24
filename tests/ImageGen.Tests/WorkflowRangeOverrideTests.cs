using ImageGen.Application.Rendering;
using ImageGen.Application.Workflows;
using ImageGen.Comfy;
using ImageGen.Comfy.Generation.MiniMaxH3T2V;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace ImageGen.Tests;

/// <summary>The field-level alternate range contract and migration of MiniMax-H3 onto arbitrary frame pass-through.</summary>
public sealed class WorkflowRangeOverrideTests
{
    private static readonly string[] H3Configurations =
    [
        "minimax-h3-t2v", "minimax-h3-t2v-turbo",
        "minimax-h3-i2v", "minimax-h3-i2v-turbo",
        "minimax-h3-ref2v", "minimax-h3-ref2v-turbo",
    ];

    [Fact]
    public void Every_h3_variant_projects_arbitrary_positive_frames_when_its_legacy_setting_is_enabled()
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
            Assert.Equal(0.01, enabled.Step);
            Assert.Null(enabled.Max);
            Assert.Null(enabled.RangeOverride);

            WorkflowExposedParam explicitlyDisabled = WorkflowCatalogService.ExposedParam(length, wf, cfg,
                new Dictionary<string, JsonElement>
                {
                    ["allowRangeOverride.length"] = JsonSerializer.SerializeToElement(true),
                    ["allowUntrainedFrameCounts"] = JsonSerializer.SerializeToElement(false),
                });
            Assert.Equal(15.08, explicitlyDisabled.Max);
            Assert.Equal(0.71, explicitlyDisabled.Step);
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
    [InlineData(362)]
    [InlineData(379)]
    [InlineData(3592)]
    public void H3_dto_retains_its_default_node_safe_boundary(int length)
    {
        MiniMaxH3Params p = ParamsCodec.Deserialize<MiniMaxH3Params>(H3Bag(length));
        Assert.Equal(length, p.Length);
    }

    [Fact]
    public void H3_dto_rejects_past_its_default_boundary_without_the_workflow_policy()
    {
        _ = Assert.Throws<RenderValidationException>(() =>
            ParamsCodec.Deserialize<MiniMaxH3Params>(H3Bag(3593)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3593)]
    [InlineData(4000)]
    public void H3_policy_bypasses_only_the_length_bound_for_ComfyUI_to_accept_or_reject(int length)
    {
        MiniMaxH3Params p = ParamsCodec.Deserialize<MiniMaxH3Params>(
            H3Bag(length), WorkflowParamKeys.Length);
        Assert.Equal(length, p.Length);
    }

    private static Dictionary<string, object?> H3Bag(int length) => new()
    {
        [WorkflowParamKeys.AudioVae] = "audio.safetensors",
        [WorkflowParamKeys.Length] = length,
        [WorkflowParamKeys.Fps] = 24.0,
        [WorkflowParamKeys.PreviewEvery] = 4,
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
