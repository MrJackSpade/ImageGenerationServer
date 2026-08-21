using ImageGen.Application.Snapshots;
using ImageGen.Application.Workflows;
using ImageGen.Comfy;
using ImageGen.Comfy.Snapshots;
using ImageGen.Domain;
using ImageGen.Domain.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace ImageGen.Tests;

/// <summary>Configuration-specific feature switches projected by the workflow catalogue.</summary>
public sealed class WorkflowFeatureToggleTests
{
    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "configurations", "workflows")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new DirectoryNotFoundException("configurations/workflows not found.");
    }

    private static (WorkflowCatalog Catalog, WorkflowCatalogService Service, WorkflowRegistry Registry) Build()
    {
        WorkflowCatalog catalog = new(
            new ComfyOptions { CatalogPath = Path.Combine(RepoRoot(), "configurations") },
            NullLogger<WorkflowCatalog>.Instance);
        WorkflowRegistry registry = new ServiceCollection().AddWorkflows().BuildServiceProvider()
            .GetRequiredService<WorkflowRegistry>();
        ComfyProbeSnapshots probes = new(
            new UnreachableSnapshot<ComfyFilesByKind>(), new UnreachableSnapshot<ComfyPresentNodes>(),
            new UnreachableSnapshot<ComfyFolderPaths>());
        CatalogSnapshots snapshots = new(
            new UnreachableSnapshot<BindingsSnapshot>(), new UnreachableSnapshot<ParamOverridesSnapshot>(),
            new UnreachableSnapshot<VariantsSnapshot>());
        WorkflowCatalogService service = new(
            catalog, registry, probes, snapshots, new UnreachableSnapshot<GenTimingAverages>(),
            new UnreachableOverrideRepository(), new UnreachableVariantRepository(),
            NullLogger<WorkflowCatalogService>.Instance);
        return (catalog, service, registry);
    }

    [Theory]
    [InlineData("anima", true)]
    [InlineData("pony-v6", true)]
    [InlineData("krea2-turbo", false)]
    public void Tag_generator_defaults_to_the_previous_tagging_capability(string configId, bool expected)
    {
        (_, WorkflowCatalogService service, _) = Build();

        Assert.Equal(expected, Assert.IsType<WorkflowInfo>(service.ResolveInfo(configId)).TagGeneratorEnabled);
        ConfigSetting setting = Assert.Single(
            Assert.IsType<WorkflowSettings>(service.GetSettings(configId)).Settings,
            s => s.Key == "tagGenerator");
        Assert.Equal(expected, Assert.IsType<bool>(setting.Shipped));
        Assert.Null(setting.Override);
    }

    [Fact]
    public void Tag_generator_override_can_enable_prose_and_disable_an_existing_tag_workflow()
    {
        (WorkflowCatalog catalog, WorkflowCatalogService service, _) = Build();
        catalog.SetParamOverrides(new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["krea2-turbo"] = new Dictionary<string, string> { ["param.tagGenerator"] = "true" },
            ["anima"] = new Dictionary<string, string> { ["param.tagGenerator"] = "false" },
        });

        Assert.True(Assert.IsType<WorkflowInfo>(service.ResolveInfo("krea2-turbo")).TagGeneratorEnabled);
        Assert.False(Assert.IsType<WorkflowInfo>(service.ResolveInfo("anima")).TagGeneratorEnabled);
    }

    [Fact]
    public void Extended_length_is_a_workflow_setting_not_a_generation_control()
    {
        (WorkflowCatalog catalog, WorkflowCatalogService service, _) = Build();
        ConfigSetting shipped = Assert.Single(
            Assert.IsType<WorkflowSettings>(service.GetSettings("minimax-h3-t2v")).Settings,
            s => s.Key == "allowUntrainedFrameCounts");
        Assert.Equal("Allow untrained frame counts", shipped.Label);
        Assert.False(Assert.IsType<bool>(shipped.Shipped));
        Assert.Null(shipped.Override);

        catalog.SetParamOverrides(new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["minimax-h3-t2v"] = new Dictionary<string, string>
            {
                ["param.allowRangeOverride.length"] = "true",
            },
        });

        WorkflowSettings enabledSettings = Assert.IsType<WorkflowSettings>(service.GetSettings("minimax-h3-t2v"));
        ConfigSetting enabled = Assert.Single(
            enabledSettings.Settings,
            s => s.Key == "allowUntrainedFrameCounts");
        Assert.True(Assert.IsType<JsonElement>(enabled.Override).GetBoolean());
        Assert.True(enabledSettings.AllowUntrainedFrameCounts);
        ConfigSetting length = Assert.Single(enabledSettings.Settings, s => s.Key == WorkflowParamKeys.Length);
        Assert.Equal(1, length.Min);
        Assert.Null(length.Max);
        Assert.Equal(1, length.Step);
        WorkflowConfiguration config = Assert.IsType<WorkflowConfiguration>(catalog.FindConfig("minimax-h3-t2v"));
        Assert.True(catalog.Resolve(config).AllowUntrainedFrameCounts);
    }

    [Theory]
    [InlineData("ltx25-t2v", true)]
    [InlineData("ltx25-i2v", true)]
    [InlineData("ltx23-i2v", false)]
    public void Audio_capability_follows_the_configuration_audio_vae_slot(string configId, bool expected)
    {
        (_, WorkflowCatalogService service, _) = Build();
        Assert.Equal(expected, Assert.IsType<WorkflowInfo>(service.ResolveInfo(configId)).HasAudio);
    }

    [Fact]
    public void Every_image_generation_workflow_has_an_untrained_resolution_setting()
    {
        (WorkflowCatalog catalog, WorkflowCatalogService service, WorkflowRegistry registry) = Build();
        List<string> missing = [];

        foreach (WorkflowConfiguration config in catalog.AllConfigs())
        {
            IWorkflow? workflow = registry.Find(config.WorkflowName);
            if (workflow is not { Kind: WorkflowKind.Generate, Media: WorkflowMedia.Image })
            {
                continue;
            }

            WorkflowSettings settings = Assert.IsType<WorkflowSettings>(service.GetSettings(config.Id));
            ConfigSetting? toggle = settings.Settings.SingleOrDefault(s => s.Key == "allowUntrainedResolution");
            if (toggle is null)
            {
                missing.Add(config.Id);
                continue;
            }

            Assert.Equal("Allow untrained resolutions", toggle.Label);
            Assert.False(Assert.IsType<bool>(toggle.Shipped));
            Assert.False(settings.AllowUntrainedResolution);
        }

        Assert.Empty(missing);
    }

    [Fact]
    public void Enabling_untrained_resolutions_is_a_workflow_policy_and_implies_custom_sizing()
    {
        (WorkflowCatalog catalog, WorkflowCatalogService service, _) = Build();
        catalog.SetParamOverrides(new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["flux1-dev"] = new Dictionary<string, string>
            {
                ["param.allowUntrainedResolution"] = "true",
            },
        });

        WorkflowSettings settings = Assert.IsType<WorkflowSettings>(service.GetSettings("flux1-dev"));
        ConfigSetting toggle = Assert.Single(settings.Settings, s => s.Key == "allowUntrainedResolution");
        Assert.True(settings.AllowUntrainedResolution);
        Assert.True(Assert.IsType<JsonElement>(toggle.Override).GetBoolean());

        WorkflowConfiguration config = Assert.IsType<WorkflowConfiguration>(catalog.FindConfig("flux1-dev"));
        Assert.True(catalog.Resolve(config).AllowUntrainedResolution);
    }

    [Fact]
    public void Every_video_workflow_with_a_length_has_one_untrained_frame_count_setting()
    {
        (WorkflowCatalog catalog, WorkflowCatalogService service, WorkflowRegistry registry) = Build();
        List<string> missing = [];

        foreach (WorkflowConfiguration config in catalog.AllConfigs())
        {
            IWorkflow? workflow = registry.Find(config.WorkflowName);
            if (workflow is not { Media: WorkflowMedia.Video }
                || !config.Params.ContainsKey(WorkflowParamKeys.Length))
            {
                continue;
            }

            WorkflowSettings settings = Assert.IsType<WorkflowSettings>(service.GetSettings(config.Id));
            if (settings.Settings.Count(s => s.Key == "allowUntrainedFrameCounts") != 1)
            {
                missing.Add(config.Id);
            }

            Assert.DoesNotContain(settings.Settings, s => s.Key == "allowRangeOverride.length");
        }

        Assert.Empty(missing);
    }

    [Fact]
    public void Untrained_frame_policy_exposes_arbitrary_positive_seconds_in_the_generation_control()
    {
        (WorkflowCatalog catalog, _, WorkflowRegistry registry) = Build();
        WorkflowConfiguration config = Assert.IsType<WorkflowConfiguration>(catalog.FindConfig("wan22-t2v-a14b"));
        IWorkflow workflow = Assert.IsAssignableFrom<IWorkflow>(registry.Find(config.WorkflowName));
        Dictionary<string, JsonElement> machine = new(StringComparer.OrdinalIgnoreCase)
        {
            ["allowUntrainedFrameCounts"] = JsonSerializer.SerializeToElement(true),
        };
        KeyValuePair<string, ConfigParam> length = config.Params.Single(p => p.Key == WorkflowParamKeys.Length);

        WorkflowExposedParam exposed = WorkflowCatalogService.ExposedParam(length, workflow, config, machine);

        Assert.Equal(WorkflowParamKeys.DurationSeconds, exposed.Key);
        Assert.Equal(0.01, exposed.Step);
        Assert.Null(exposed.Max);
        Assert.Contains("any positive frame count", Assert.IsType<string>(exposed.Help),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cadence_free_video_workflows_expose_arbitrary_positive_frames_when_enabled()
    {
        (WorkflowCatalog catalog, WorkflowCatalogService service, WorkflowRegistry registry) = Build();
        catalog.SetParamOverrides(new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["sdxl-i2v"] = new Dictionary<string, string>
            {
                ["param.allowUntrainedFrameCounts"] = "true",
            },
        });
        WorkflowConfiguration config = Assert.IsType<WorkflowConfiguration>(catalog.FindConfig("sdxl-i2v"));
        IWorkflow workflow = Assert.IsAssignableFrom<IWorkflow>(registry.Find(config.WorkflowName));
        Assert.Null(workflow.FrameRule);

        WorkflowSettings settings = Assert.IsType<WorkflowSettings>(service.GetSettings(config.Id));
        Assert.True(settings.AllowUntrainedFrameCounts);
        ConfigSetting setting = Assert.Single(settings.Settings, s => s.Key == "allowUntrainedFrameCounts");
        Assert.True(Assert.IsType<JsonElement>(setting.Override).GetBoolean());

        IReadOnlyDictionary<string, JsonElement> machine = catalog.ParamOverridesFor(config.Id);
        KeyValuePair<string, ConfigParam> length = config.Params.Single(p => p.Key == WorkflowParamKeys.Length);
        WorkflowExposedParam exposed = WorkflowCatalogService.ExposedParam(length, workflow, config, machine);
        Assert.Equal(WorkflowParamKeys.Length, exposed.Key);
        Assert.Equal(1, exposed.Min);
        Assert.Null(exposed.Max);
        Assert.Equal(1, exposed.Step);
        Assert.Contains("any positive frame count", Assert.IsType<string>(exposed.Help),
            StringComparison.OrdinalIgnoreCase);
    }

    private sealed class UnreachableSnapshot<T> : ISnapshot<T>
    {
        public ValueTask<T> GetAsync(CancellationToken ct) => throw new NotSupportedException();
        public T PeekCurrent() => throw new NotSupportedException();
        public void Invalidate() => throw new NotSupportedException();
    }

    private sealed class UnreachableOverrideRepository : ICatalogOverrideRepository
    {
        public Task<IReadOnlyDictionary<string, ModelBinding>> BindingsAsync(string machineName, CancellationToken ct) => throw new NotSupportedException();
        public Task SetBindingAsync(string machineName, string slotId, string? fileName, bool isAuto, CancellationToken ct) => throw new NotSupportedException();
        public Task AddAutoBindingsAsync(string machineName, IReadOnlyDictionary<string, string> slotToFile, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> OverridesAsync(string machineName, CancellationToken ct) => throw new NotSupportedException();
        public Task SetOverrideAsync(string machineName, string configId, string paramKey, string? value, CancellationToken ct) => throw new NotSupportedException();
        public Task ClearOverridesAsync(string machineName, string configId, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class UnreachableVariantRepository : IWorkflowVariantRepository
    {
        public Task<IReadOnlyList<WorkflowVariant>> VariantsAsync(string machineName, CancellationToken ct) => throw new NotSupportedException();
        public Task AddAsync(string machineName, WorkflowVariant variant, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(string machineName, string variantId, CancellationToken ct) => throw new NotSupportedException();
    }
}
