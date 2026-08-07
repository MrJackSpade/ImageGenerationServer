using ImageGen.Application.Snapshots;
using ImageGen.Comfy;
using ImageGen.Comfy.Snapshots;
using ImageGen.Domain.Repositories;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImageGen.Tests;

/// <summary>
/// The relocated auto-bind recognition pass in the bindings-snapshot rebuild (#199): recognition is visible to a read
/// that awaits the rebuild, and the chain converges — a second rebuild over the now-bound slot writes nothing, because
/// the matcher only fires on a still-unbound slot and <c>AddAutoBindingsAsync</c> never overwrites.
/// </summary>
public sealed class CatalogBindingsSnapshotTests : IDisposable
{
    private readonly string _root;

    public CatalogBindingsSnapshotTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "imggen-bind-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(Path.Combine(_root, "workflows"));
        _ = Directory.CreateDirectory(Path.Combine(_root, "models"));
        // One matchable VAE slot: "^myvae$" recognises "myvae.safetensors" (stem drops the extension).
        File.WriteAllText(
            Path.Combine(_root, "models", "myvae.json"),
            """{ "id": "myvae", "kind": "vae", "label": "My VAE", "match": ["^myvae$"] }""");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }

    /// <summary>A fixed-value snapshot for a source the loader only reads.</summary>
    private sealed class FixedSnapshot<T>(T value) : ISnapshot<T>
    {
        public ValueTask<T> GetAsync(CancellationToken ct) => new(value);

        public T PeekCurrent() => value;

        public void Invalidate()
        {
        }
    }

    /// <summary>An in-memory bindings/overrides store that records writes, so a test can count auto-bind attempts.</summary>
    private sealed class FakeOverrides : ICatalogOverrideRepository
    {
        private readonly Dictionary<string, ModelBinding> _bindings = new(StringComparer.OrdinalIgnoreCase);

        public int AutoBindCalls { get; private set; }

        public Task<IReadOnlyDictionary<string, ModelBinding>> BindingsAsync(string machineName, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, ModelBinding>>(
                new Dictionary<string, ModelBinding>(_bindings, StringComparer.OrdinalIgnoreCase));

        public Task AddAutoBindingsAsync(string machineName, IReadOnlyDictionary<string, string> slotToFile, CancellationToken ct)
        {
            AutoBindCalls++;
            foreach ((string slot, string file) in slotToFile)
            {
                // Insert-if-absent, exactly like the real repo — never overwrites a binding already present.
                _ = _bindings.TryAdd(slot, new ModelBinding(slot, file, IsAuto: true));
            }

            return Task.CompletedTask;
        }

        public Task SetBindingAsync(string machineName, string slotId, string? fileName, bool isAuto, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> OverridesAsync(string machineName, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task SetOverrideAsync(string machineName, string configId, string settingKey, string? settingValue, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task ClearOverridesAsync(string machineName, string configId, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedVariants : IWorkflowVariantRepository
    {
        public Task<IReadOnlyList<WorkflowVariant>> VariantsAsync(string machineName, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorkflowVariant>>([]);

        public Task AddAsync(string machineName, WorkflowVariant variant, CancellationToken ct) => throw new NotSupportedException();

        public Task DeleteAsync(string machineName, string variantId, CancellationToken ct) => throw new NotSupportedException();
    }

    private CatalogSqlSnapshotSources NewSources(FakeOverrides overrides)
    {
        WorkflowCatalog catalog = new(new ComfyOptions { CatalogPath = _root }, NullLogger<WorkflowCatalog>.Instance);
        ComfyFilesByKind files = new(new Dictionary<RequirementKind, IReadOnlyList<string>>
        {
            [RequirementKind.Vae] = ["myvae.safetensors"],
        });
        return new CatalogSqlSnapshotSources(
            catalog, overrides, new UnusedVariants(), new FixedSnapshot<ComfyFilesByKind>(files),
            NullLogger<CatalogSqlSnapshotSources>.Instance);
    }

    [Fact]
    public async Task Recognition_is_visible_to_the_read_that_awaits_the_rebuild()
    {
        FakeOverrides overrides = new();
        CatalogSqlSnapshotSources sources = NewSources(overrides);

        BindingsSnapshot snapshot = await sources.LoadBindingsAsync(CancellationToken.None);

        // The auto-bound slot is in the published snapshot — the read observes the completed recognition pass.
        Assert.True(snapshot.Bindings.TryGetValue("myvae", out ModelBinding? bound));
        ModelBinding binding = Assert.IsType<ModelBinding>(bound);
        Assert.Equal("myvae.safetensors", binding.FileName);
        Assert.True(binding.IsAuto);
        Assert.Equal(["myvae.safetensors"], snapshot.Candidates["myvae"]);
    }

    [Fact]
    public async Task The_recognition_chain_converges_a_second_rebuild_writes_nothing()
    {
        FakeOverrides overrides = new();
        CatalogSqlSnapshotSources sources = NewSources(overrides);

        _ = await sources.LoadBindingsAsync(CancellationToken.None);   // binds myvae → one write
        _ = await sources.LoadBindingsAsync(CancellationToken.None);   // slot now bound → matcher skips it, no write

        Assert.Equal(1, overrides.AutoBindCalls);
    }
}
