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

/// <summary>Catalog, settings, fan-out, and graph contracts for the shared hidden edit-quality MP selector.</summary>
public sealed class EditQualityTests
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

    private static (WorkflowCatalog Catalog, WorkflowRegistry Registry) Build()
    {
        WorkflowCatalog catalog = new(
            new ComfyOptions { CatalogPath = Path.Combine(RepoRoot(), "configurations") },
            NullLogger<WorkflowCatalog>.Instance);
        catalog.SetBindings(catalog.AllRequirements().ToDictionary(r => r.Id, r => r.Id + ".safetensors"));
        WorkflowRegistry registry = new ServiceCollection().AddWorkflows().BuildServiceProvider()
            .GetRequiredService<WorkflowRegistry>();
        return (catalog, registry);
    }

    private static Dictionary<string, object?> Merge(WorkflowCatalog catalog, IWorkflow workflow, WorkflowConfiguration config)
    {
        Dictionary<string, object?> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (ParamSpec spec in workflow.Schema.Where(s => s.Default is not null))
        {
            values[spec.Key] = spec.Default;
        }

        foreach ((string key, ConfigParam param) in config.Params)
        {
            values[key] = param.Value;
        }

        catalog.ResolveModelRefs(workflow, config.Id, values);
        return values;
    }

    private static WorkflowCatalogService Service(WorkflowCatalog catalog, WorkflowRegistry registry)
    {
        ComfyProbeSnapshots probes = new(
            new UnreachableSnapshot<ComfyFilesByKind>(), new UnreachableSnapshot<ComfyPresentNodes>(),
            new UnreachableSnapshot<ComfyFolderPaths>());
        CatalogSnapshots snapshots = new(
            new UnreachableSnapshot<BindingsSnapshot>(), new UnreachableSnapshot<ParamOverridesSnapshot>(),
            new UnreachableSnapshot<VariantsSnapshot>());
        return new WorkflowCatalogService(catalog, registry, probes, snapshots,
            new UnreachableSnapshot<GenTimingAverages>(), new UnreachableOverrideRepository(),
            new UnreachableVariantRepository(), NullLogger<WorkflowCatalogService>.Instance);
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

    [Fact]
    public void Every_quality_capable_configuration_declares_the_complete_hidden_contract()
    {
        (WorkflowCatalog catalog, WorkflowRegistry registry) = Build();
        int count = 0;
        foreach (WorkflowConfiguration config in catalog.AllConfigs())
        {
            IWorkflow? workflow = registry.Find(config.WorkflowName);
            if (workflow?.SupportsEditQuality != true)
            {
                continue;
            }

            count++;
            ConfigParam selector = Assert.IsType<ConfigParam>(config.Params[WorkflowParamKeys.EditQuality]);
            Assert.Equal("Medium", selector.Value);
            Assert.Equal(ParamVisibility.Hidden, selector.Visibility);
            double low = Convert.ToDouble(config.Params[WorkflowParamKeys.EditQualityLowMp].Value);
            double medium = Convert.ToDouble(config.Params[WorkflowParamKeys.EditQualityMediumMp].Value);
            double high = Convert.ToDouble(config.Params[WorkflowParamKeys.EditQualityHighMp].Value);
            Assert.True(low <= medium && medium <= high, $"{config.Id}: {low} <= {medium} <= {high}");
            Assert.Equal(ParamVisibility.Locked, config.Params[WorkflowParamKeys.EditQualityLowMp].Visibility);
            Assert.Equal(ParamVisibility.Locked, config.Params[WorkflowParamKeys.EditQualityMediumMp].Visibility);
            Assert.Equal(ParamVisibility.Locked, config.Params[WorkflowParamKeys.EditQualityHighMp].Visibility);
        }

        Assert.True(count >= 30, $"Expected the still-image editor families to opt in; found {count} configurations.");
    }

    [Fact]
    public void Workflow_settings_expose_three_editable_budgets_and_a_hidden_three_choice_selector()
    {
        (WorkflowCatalog catalog, WorkflowRegistry registry) = Build();
        WorkflowCatalogService service = Service(catalog, registry);

        WorkflowSettings settings = Assert.IsType<WorkflowSettings>(service.GetSettings("chronoedit"));
        ConfigSetting selector = Assert.Single(settings.Settings, s => s.Key == WorkflowParamKeys.EditQuality);
        Assert.Equal("enum", selector.Type);
        Assert.Equal("hidden", selector.Visibility);
        Assert.Equal(["Low", "Medium", "High"], selector.Choices);
        Assert.Equal("Medium", selector.Shipped);

        ConfigSetting low = Assert.Single(settings.Settings, s => s.Key == WorkflowParamKeys.EditQualityLowMp);
        ConfigSetting medium = Assert.Single(settings.Settings, s => s.Key == WorkflowParamKeys.EditQualityMediumMp);
        ConfigSetting high = Assert.Single(settings.Settings, s => s.Key == WorkflowParamKeys.EditQualityHighMp);
        Assert.Equal((0.26, 0.52, 1.0),
            (Convert.ToDouble(low.Shipped), Convert.ToDouble(medium.Shipped), Convert.ToDouble(high.Shipped)));
        Assert.All([low, medium, high], s => Assert.Equal("locked", s.Visibility));

        catalog.SetParamOverrides(new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["chronoedit"] = new Dictionary<string, string> { ["param.edit_quality_medium_mp"] = "0.64" },
        });
        WorkflowSettings changedSettings = Assert.IsType<WorkflowSettings>(service.GetSettings("chronoedit"));
        ConfigSetting changed = Assert.Single(changedSettings.Settings,
            s => s.Key == WorkflowParamKeys.EditQualityMediumMp);
        Assert.Equal(0.64, Assert.IsType<JsonElement>(changed.Override).GetDouble());

        catalog.SetParamOverrides(new Dictionary<string, IReadOnlyDictionary<string, string>>());
        WorkflowSettings resetSettings = Assert.IsType<WorkflowSettings>(service.GetSettings("chronoedit"));
        ConfigSetting reset = Assert.Single(resetSettings.Settings,
            s => s.Key == WorkflowParamKeys.EditQualityMediumMp);
        Assert.Null(reset.Override);
        Assert.Equal(0.52, Convert.ToDouble(reset.Shipped));
    }

    [Fact]
    public void One_quality_label_resolves_against_each_workflows_own_budget_and_preserves_aspect()
    {
        (WorkflowCatalog catalog, WorkflowRegistry registry) = Build();
        (int W, int H) Size(string id, string quality)
        {
            WorkflowConfiguration config = Assert.IsType<WorkflowConfiguration>(catalog.FindConfig(id));
            IWorkflow workflow = Assert.IsAssignableFrom<IWorkflow>(registry.Find(config.WorkflowName));
            Dictionary<string, object?> values = Merge(catalog, workflow, config);
            values[WorkflowParamKeys.EditQuality] = quality;
            return workflow.EtaRenderSize(values, catalog.Resolve(config), 1600, 900);
        }

        (int lowW, int lowH) = Size("flux2-klein-4b-edit", "Low");
        (int mediumW, int mediumH) = Size("flux2-klein-4b-edit", "Medium");
        (int highW, int highH) = Size("flux2-klein-4b-edit", "High");
        Assert.True(lowW * lowH < mediumW * mediumH && mediumW * mediumH < highW * highH);
        Assert.All([(lowW, lowH), (mediumW, mediumH), (highW, highH)], size =>
        {
            Assert.Equal(0, size.Item1 % 16);
            Assert.Equal(0, size.Item2 % 16);
            Assert.InRange((double)size.Item1 / size.Item2, 1.70, 1.85);
        });

        (int chronoW, int chronoH) = Size("chronoedit", "High");
        Assert.True(chronoW * chronoH < highW * highH); // High means Chrono's 1 MP, not FLUX.2's 2 MP.
    }

    [Fact]
    public void Selected_budget_is_applied_to_source_and_every_spatial_reference()
    {
        (WorkflowCatalog catalog, WorkflowRegistry registry) = Build();
        WorkflowConfiguration config = Assert.IsType<WorkflowConfiguration>(catalog.FindConfig("flux2-klein-4b-edit"));
        IWorkflow workflow = Assert.IsAssignableFrom<IWorkflow>(registry.Find(config.WorkflowName));
        WorkflowInputs inputs = new()
        {
            Positive = "make it red",
            SourceImageName = "source.png",
            SourceWidth = 1600,
            SourceHeight = 900,
            EditMegapixels = 2.0,
            References = [new ReferenceInput("a.png", ReferenceKind.Image), new ReferenceInput("b.png", ReferenceKind.Image)],
        };
        ComfyWorkflowGraph graph = workflow.Build(Merge(catalog, workflow, config), catalog.Resolve(config), inputs);
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(graph));
        List<JsonElement> scales = [.. document.RootElement.EnumerateObject()
            .Where(n => n.Value.GetProperty("class_type").GetString() == "ImageScaleToTotalPixels")
            .Select(n => n.Value.GetProperty("inputs"))];

        Assert.Equal(3, scales.Count);
        Assert.All(scales, scale =>
        {
            Assert.Equal(2.0, scale.GetProperty("megapixels").GetDouble());
            Assert.Equal(16, scale.GetProperty("resolution_steps").GetInt32());
        });
    }

    [Fact]
    public void Outpaint_page_only_submits_the_revealed_quality_param_alongside_pad_controls()
    {
        string script = File.ReadAllText(Path.Combine(RepoRoot(), "src", "ImageGen.Web", "wwwroot", "js", "edit.js"));
        string view = File.ReadAllText(Path.Combine(RepoRoot(), "src", "ImageGen.Web", "Views", "Edit", "Index.cshtml"));
        Assert.Contains("id=\"outpaintParams\"", view);
        Assert.Contains("p.key === \"edit_quality\"", script);
        Assert.Contains("...readOverrides($outpaintParams)", script);
        Assert.DoesNotContain("...readOverrides($(\"editParams\"))", script);
    }
}
