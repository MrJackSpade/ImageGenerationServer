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

    private static (WorkflowCatalog Catalog, WorkflowCatalogService Service) Build()
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
        return (catalog, service);
    }

    [Theory]
    [InlineData("anima", true)]
    [InlineData("pony-v6", true)]
    [InlineData("krea2-turbo", false)]
    public void Tag_generator_defaults_to_the_previous_tagging_capability(string configId, bool expected)
    {
        (_, WorkflowCatalogService service) = Build();

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
        (WorkflowCatalog catalog, WorkflowCatalogService service) = Build();
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
        (WorkflowCatalog catalog, WorkflowCatalogService service) = Build();
        ConfigSetting shipped = Assert.Single(
            Assert.IsType<WorkflowSettings>(service.GetSettings("minimax-h3-t2v")).Settings,
            s => s.Key == "allowRangeOverride.length");
        Assert.Equal("Allow untested lengths", shipped.Label);
        Assert.False(Assert.IsType<bool>(shipped.Shipped));
        Assert.Null(shipped.Override);

        catalog.SetParamOverrides(new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["minimax-h3-t2v"] = new Dictionary<string, string>
            {
                ["param.allowRangeOverride.length"] = "true",
            },
        });

        ConfigSetting enabled = Assert.Single(
            Assert.IsType<WorkflowSettings>(service.GetSettings("minimax-h3-t2v")).Settings,
            s => s.Key == "allowRangeOverride.length");
        Assert.True(Assert.IsType<JsonElement>(enabled.Override).GetBoolean());
    }

    [Theory]
    [InlineData("ltx25-t2v", true)]
    [InlineData("ltx25-i2v", true)]
    [InlineData("ltx23-i2v", false)]
    public void Audio_capability_follows_the_configuration_audio_vae_slot(string configId, bool expected)
    {
        (_, WorkflowCatalogService service) = Build();
        Assert.Equal(expected, Assert.IsType<WorkflowInfo>(service.ResolveInfo(configId)).HasAudio);
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
