using ImageGen.Application.Snapshots;
using ImageGen.Application.Workflows;
using ImageGen.Comfy;
using ImageGen.Comfy.Snapshots;
using ImageGen.Domain.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImageGen.Tests;

/// <summary>Workflow-facing consumers must all observe pin → shared → unbound, including missing-file gating.</summary>
public sealed class EffectiveBindingStatusTests
{
    [Fact]
    public async Task Variant_lifecycle_copies_only_explicit_pin_rows_and_clears_them_on_delete()
    {
        WorkflowCatalog catalog = new(
            new ComfyOptions { CatalogPath = Path.Combine(RepoRoot(), "configurations") },
            NullLogger<WorkflowCatalog>.Instance);
        WorkflowRegistry registry = new ServiceCollection().AddWorkflows().BuildServiceProvider()
            .GetRequiredService<WorkflowRegistry>();
        WorkflowConfiguration source = catalog.AllConfigs().First(c => registry.Find(c.WorkflowName) is not null);
        Dictionary<string, ModelBinding> shared = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, IReadOnlyDictionary<string, ConfigModelBindingOverride>> pins = new(StringComparer.OrdinalIgnoreCase);
        catalog.SetBindings(shared, pins);
        BindingsSnapshot bindingSnapshot = new(shared, pins,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));
        RecordingOverrides overrides = new();
        RecordingVariants variants = new();
        WorkflowCatalogService service = Service(catalog, registry,
            new Dictionary<RequirementKind, IReadOnlyList<string>>(), new HashSet<string>(), bindingSnapshot,
            overrides, variants);

        string variantId = await service.DuplicateWorkflowAsync(source.Id, "Pinned copy", CancellationToken.None);

        Assert.Equal((source.Id, variantId), overrides.LastCopy);
        Assert.Equal(variantId, Assert.Single(variants.Added).VariantId);

        catalog.SetVariants([new VariantSpec(variantId, source.Id, "Pinned copy",
            new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.OrdinalIgnoreCase))]);
        await service.DeleteVariantAsync(variantId, CancellationToken.None);

        Assert.Contains(variantId, variants.Deleted);
        Assert.Contains(variantId, overrides.ClearedBindingConfigs);
        Assert.Contains(variantId, overrides.ClearedOverrideConfigs);
    }

    [Fact]
    public async Task Workflow_selection_validates_the_config_slot_and_model_kind_before_writing()
    {
        WorkflowCatalog catalog = new(
            new ComfyOptions { CatalogPath = Path.Combine(RepoRoot(), "configurations") },
            NullLogger<WorkflowCatalog>.Instance);
        WorkflowRegistry registry = new ServiceCollection().AddWorkflows().BuildServiceProvider()
            .GetRequiredService<WorkflowRegistry>();
        WorkflowConfiguration config = catalog.AllConfigs().First(c => registry.Find(c.WorkflowName) is not null
            && c.Requirements.All().Any(s => string.IsNullOrWhiteSpace(catalog.FindRequirement(s)?.Node)));
        IWorkflow workflow = Assert.IsAssignableFrom<IWorkflow>(registry.Find(config.WorkflowName));
        string slotId = config.Requirements.All().First(s => string.IsNullOrWhiteSpace(catalog.FindRequirement(s)?.Node));
        Requirement requirement = Assert.IsType<Requirement>(catalog.FindRequirement(slotId));
        string otherSlot = catalog.AllRequirements().First(r => !config.Requirements.All()
            .Concat(catalog.ModelRefSlots(workflow, config)).Contains(r.Id, StringComparer.OrdinalIgnoreCase)).Id;
        const string canonicalFile = "folder/Allowed.safetensors";
        Dictionary<RequirementKind, IReadOnlyList<string>> byKind = new()
        {
            [requirement.Kind] = [canonicalFile],
        };
        Dictionary<string, ModelBinding> shared = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, IReadOnlyDictionary<string, ConfigModelBindingOverride>> pins = new(StringComparer.OrdinalIgnoreCase);
        catalog.SetBindings(shared, pins);
        BindingsSnapshot bindingSnapshot = new(shared, pins,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));
        RecordingOverrides repository = new();
        WorkflowCatalogService service = Service(catalog, registry, byKind, new HashSet<string>(), bindingSnapshot, repository);

        _ = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SetConfigBindingAsync("does-not-exist", slotId, canonicalFile, CancellationToken.None));
        _ = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SetConfigBindingAsync(config.Id, otherSlot, canonicalFile, CancellationToken.None));
        _ = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SetConfigBindingAsync(config.Id, slotId, "wrong-kind.safetensors", CancellationToken.None));

        WorkflowBindingResult result = await service.SetConfigBindingAsync(
            config.Id, slotId, canonicalFile.ToUpperInvariant(), CancellationToken.None);
        Assert.Equal(WorkflowBindingResult.SharedCreated, result);
        Assert.Equal((config.Id, slotId, canonicalFile), repository.LastSelection);
    }

    [Fact]
    public async Task A_missing_pin_disables_only_its_workflow_without_falling_back_to_the_present_shared_file()
    {
        WorkflowCatalog catalog = new(
            new ComfyOptions { CatalogPath = Path.Combine(RepoRoot(), "configurations") },
            NullLogger<WorkflowCatalog>.Instance);
        WorkflowRegistry registry = new ServiceCollection().AddWorkflows().BuildServiceProvider()
            .GetRequiredService<WorkflowRegistry>();

        List<(WorkflowConfiguration Config, IWorkflow Workflow, List<string> Slots)> configs = [];
        foreach (WorkflowConfiguration config in catalog.AllConfigs())
        {
            if (registry.Find(config.WorkflowName) is not { } workflow)
            {
                continue;
            }

            List<string> slots = [.. config.Requirements.All().Concat(catalog.ModelRefSlots(workflow, config))
                .Distinct(StringComparer.OrdinalIgnoreCase)];
            configs.Add((config, workflow, slots));
        }

        string sharedSlot = configs.SelectMany(x => x.Slots.Select(s => (Slot: s, x.Config)))
            .Where(x => string.IsNullOrWhiteSpace(catalog.FindRequirement(x.Slot)?.Node))
            .GroupBy(x => x.Slot, StringComparer.OrdinalIgnoreCase)
            .First(g => g.Select(x => x.Config.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1).Key;
        WorkflowConfiguration target = configs.First(x => x.Slots.Contains(sharedSlot, StringComparer.OrdinalIgnoreCase)).Config;

        Dictionary<string, ModelBinding> shared = catalog.AllRequirements().ToDictionary(
            r => r.Id, r => new ModelBinding(r.Id, $"shared-{r.Id}.safetensors", false),
            StringComparer.OrdinalIgnoreCase);
        Dictionary<string, IReadOnlyDictionary<string, ConfigModelBindingOverride>> pins = new(StringComparer.OrdinalIgnoreCase)
        {
            [target.Id] = new Dictionary<string, ConfigModelBindingOverride>(StringComparer.OrdinalIgnoreCase)
            {
                [sharedSlot] = new(target.Id, sharedSlot, "missing-pinned-file.safetensors", DateTime.UtcNow),
            },
        };
        catalog.SetBindings(shared, pins);

        Dictionary<RequirementKind, IReadOnlyList<string>> byKind = catalog.AllRequirements()
            .Where(r => string.IsNullOrWhiteSpace(r.Node))
            .GroupBy(r => r.Kind)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(r => shared[r.Id].FileName)]);
        HashSet<string> nodes = [.. catalog.AllRequirements().Select(r => r.Node).OfType<string>()];
        BindingsSnapshot bindingSnapshot = new(shared, pins,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));
        WorkflowCatalogService service = Service(catalog, registry, byKind, nodes, bindingSnapshot);

        CatalogStatus status = await service.GetStatusAsync(CancellationToken.None);
        WorkflowStatus targetStatus = Assert.Single(status.Workflows, w => w.Id == target.Id);
        Assert.Contains(sharedSlot, targetStatus.MissingSlots, StringComparer.OrdinalIgnoreCase);
        foreach (WorkflowStatus inheritor in status.Workflows.Where(w => w.Id != target.Id
                     && w.RequiredSlots.Contains(sharedSlot, StringComparer.OrdinalIgnoreCase)))
        {
            Assert.DoesNotContain(sharedSlot, inheritor.MissingSlots, StringComparer.OrdinalIgnoreCase);
        }

        IReadOnlyList<ConfigSlotStatus> configSlots = Assert.IsAssignableFrom<IReadOnlyList<ConfigSlotStatus>>(
            await service.GetConfigSlotsAsync(target.Id, CancellationToken.None));
        ConfigSlotStatus slot = Assert.Single(configSlots, s => s.Id == sharedSlot);
        Assert.Equal(ModelBindingSourceTokens.Pinned, slot.Source);
        Assert.Equal(shared[sharedSlot].FileName, slot.SharedFile);
        Assert.Equal("missing-pinned-file.safetensors", slot.PinnedFile);
        Assert.Equal(slot.PinnedFile, slot.EffectiveFile);
        Assert.DoesNotContain(await service.ListEligibleAsync(CancellationToken.None), w => w.Id == target.Id);
    }

    private static WorkflowCatalogService Service(
        WorkflowCatalog catalog, WorkflowRegistry registry,
        IReadOnlyDictionary<RequirementKind, IReadOnlyList<string>> byKind, IReadOnlySet<string> nodes,
        BindingsSnapshot bindings, ICatalogOverrideRepository? overrides = null, IWorkflowVariantRepository? variants = null)
    {
        ComfyProbeSnapshots probes = new(
            new FixedSnapshot<ComfyFilesByKind>(new ComfyFilesByKind(byKind)),
            new FixedSnapshot<ComfyPresentNodes>(new ComfyPresentNodes(nodes)),
            new FixedSnapshot<ComfyFolderPaths>(new ComfyFolderPaths(
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase))));
        CatalogSnapshots snapshots = new(
            new FixedSnapshot<BindingsSnapshot>(bindings),
            new FixedSnapshot<ParamOverridesSnapshot>(new ParamOverridesSnapshot(
                new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase))),
            new FixedSnapshot<VariantsSnapshot>(new VariantsSnapshot([])));
        return new WorkflowCatalogService(catalog, registry, probes, snapshots,
            new FixedSnapshot<GenTimingAverages>(new GenTimingAverages(new Dictionary<string, double>())),
            overrides ?? new RecordingOverrides(), variants ?? new RecordingVariants(), NullLogger<WorkflowCatalogService>.Instance);
    }

    private sealed class FixedSnapshot<T>(T value) : ISnapshot<T>
    {
        public ValueTask<T> GetAsync(CancellationToken ct) => ValueTask.FromResult(value);
        public T PeekCurrent() => value;
        public void Invalidate() { }
    }

    private sealed class RecordingOverrides : ICatalogOverrideRepository
    {
        public (string ConfigId, string SlotId, string FileName) LastSelection { get; private set; } = ("", "", "");
        public (string SourceConfigId, string TargetConfigId) LastCopy { get; private set; } = ("", "");
        public List<string> ClearedBindingConfigs { get; } = [];
        public List<string> ClearedOverrideConfigs { get; } = [];

        public Task<IReadOnlyDictionary<string, ModelBinding>> BindingsAsync(string machineName, CancellationToken ct) => throw new NotSupportedException();
        public Task SetBindingAsync(string machineName, string slotId, string? fileName, bool isAuto, CancellationToken ct) => throw new NotSupportedException();
        public Task AddAutoBindingsAsync(string machineName, IReadOnlyDictionary<string, string> slotToFile, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, ConfigModelBindingOverride>>> BindingOverridesAsync(string machineName, CancellationToken ct) => throw new NotSupportedException();
        public Task<WorkflowBindingResult> SetConfigBindingAsync(string machineName, string configId, string slotId, string fileName, CancellationToken ct)
        {
            LastSelection = (configId, slotId, fileName);
            return Task.FromResult(WorkflowBindingResult.SharedCreated);
        }
        public Task ClearConfigBindingAsync(string machineName, string configId, string slotId, CancellationToken ct) => throw new NotSupportedException();
        public Task CopyConfigBindingsAsync(string machineName, string sourceConfigId, string targetConfigId, CancellationToken ct)
        {
            LastCopy = (sourceConfigId, targetConfigId);
            return Task.CompletedTask;
        }
        public Task ClearConfigBindingsAsync(string machineName, string configId, CancellationToken ct)
        {
            ClearedBindingConfigs.Add(configId);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> OverridesAsync(string machineName, CancellationToken ct) => throw new NotSupportedException();
        public Task SetOverrideAsync(string machineName, string configId, string settingKey, string? settingValue, CancellationToken ct) => throw new NotSupportedException();
        public Task ClearOverridesAsync(string machineName, string configId, CancellationToken ct)
        {
            ClearedOverrideConfigs.Add(configId);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingVariants : IWorkflowVariantRepository
    {
        public List<WorkflowVariant> Added { get; } = [];
        public List<string> Deleted { get; } = [];
        public Task<IReadOnlyList<WorkflowVariant>> VariantsAsync(string machineName, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorkflowVariant>>(Added);
        public Task AddAsync(string machineName, WorkflowVariant variant, CancellationToken ct)
        {
            Added.Add(variant);
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string machineName, string variantId, CancellationToken ct)
        {
            Deleted.Add(variantId);
            return Task.CompletedTask;
        }
    }

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "configurations", "models")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new DirectoryNotFoundException("repo root not found.");
    }
}
