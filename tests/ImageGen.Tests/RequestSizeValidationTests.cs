using ImageGen.Application.Snapshots;
using ImageGen.Comfy;
using ImageGen.Comfy.Snapshots;
using ImageGen.Domain;
using ImageGen.Domain.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace ImageGen.Tests;

/// <summary>
/// Exercises the submit-boundary size rules (#209) over the REAL shipped catalog: a request carries an aspect OR an
/// explicit width/height, never both; dims equal to a config's own aspect-map entry are an aspect resolution and are
/// exempt from the envelope check (several shipped maps sit deliberately OUTSIDE their envelope as pure ratio sources
/// for the megapixels snap — flux1-dev's 1616×912); a genuine custom size is envelope-checked; and the recorded shape
/// label resolves from whatever the caller supplied (dims, an aspect name, or the config's own fixed size).
/// </summary>
public sealed class RequestSizeValidationTests
{
    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "configurations", "models")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new DirectoryNotFoundException("configurations/ not found above the test bin dir.");
    }

    /// <summary>The service over the real shipped catalog. The size rules touch only the configuration tree; every
    /// other dependency is a throwing stand-in, so a test fails loudly if a rule ever starts reaching one.</summary>
    private static WorkflowCatalogService Service()
    {
        ComfyOptions cfg = new() { CatalogPath = Path.Combine(RepoRoot(), "configurations") };
        WorkflowCatalog catalog = new(cfg, NullLogger<WorkflowCatalog>.Instance);
        ComfyProbeSnapshots probes = new(
            new UnreachableSnapshot<ComfyFilesByKind>(), new UnreachableSnapshot<ComfyPresentNodes>(), new UnreachableSnapshot<ComfyFolderPaths>());
        CatalogSnapshots snapshots = new(
            new UnreachableSnapshot<BindingsSnapshot>(), new UnreachableSnapshot<ParamOverridesSnapshot>(), new UnreachableSnapshot<VariantsSnapshot>());
        return new WorkflowCatalogService(catalog, new WorkflowRegistry([]), probes, snapshots,
            new UnreachableSnapshot<GenTimingAverages>(), new UnreachableOverrideRepository(), new UnreachableVariantRepository(),
            NullLogger<WorkflowCatalogService>.Instance);
    }

    /// <summary>A dependency the size rules must never touch — any member is a loud test failure, not a silent default.</summary>
    private sealed class UnreachableSnapshot<T> : ISnapshot<T>
    {
        public ValueTask<T> GetAsync(CancellationToken ct) => throw new NotSupportedException("The size rules must not read snapshots.");

        public T PeekCurrent() => throw new NotSupportedException("The size rules must not read snapshots.");

        public void Invalidate() => throw new NotSupportedException("The size rules must not touch snapshots.");
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

    private static Dictionary<string, JsonElement> Size(int w, int h) => new()
    {
        [WorkflowParamKeys.Width] = JsonSerializer.SerializeToElement(w),
        [WorkflowParamKeys.Height] = JsonSerializer.SerializeToElement(h),
    };

    [Fact]
    public void An_aspect_and_an_explicit_size_together_are_refused()
    {
        string? refusal = Service().ValidateRequestedSize("flux1-dev", "landscape", Size(1616, 912));

        Assert.NotNull(refusal);
        Assert.Contains("not both", refusal);
    }

    [Fact]
    public void An_aspect_alone_passes() =>
        Assert.Null(Service().ValidateRequestedSize("flux1-dev", "landscape", null));

    /// <summary>flux1-dev's landscape map entry (1616×912) sits OUTSIDE its own 256–1440 envelope on purpose — it is a
    /// ratio source the megapixels snap rescales. The composer submits those literal dims for an aspect click (#209),
    /// so envelope-checking them would falsely refuse every landscape/portrait pick on such a model.</summary>
    [Fact]
    public void A_size_matching_an_aspect_map_entry_passes_even_outside_the_envelope() =>
        Assert.Null(Service().ValidateRequestedSize("flux1-dev", null, Size(1616, 912)));

    [Fact]
    public void A_custom_size_outside_the_envelope_is_refused() =>
        Assert.NotNull(Service().ValidateRequestedSize("flux1-dev", null, Size(2000, 2000)));

    [Fact]
    public void A_custom_size_inside_the_envelope_passes() =>
        Assert.Null(Service().ValidateRequestedSize("flux1-dev", null, Size(1024, 768)));

    /// <summary>A submitted width/height IS the shape: the recorded label follows the dims.</summary>
    [Theory]
    [InlineData(1616, 912, "landscape")]
    [InlineData(912, 1616, "portrait")]
    [InlineData(1216, 1216, "square")]
    public void An_explicit_size_resolves_to_the_shape_it_is(int w, int h, string expected) =>
        Assert.Equal(expected, Service().ResolveEffectiveAspect("flux1-dev", null, Size(w, h)));

    /// <summary>The API aspect-only path: the name rides through as given, exactly as the composer submitted pre-#209
    /// (the render path's NormalizeAspect still rejects a garbage name at submit, as it always did).</summary>
    [Fact]
    public void An_aspect_name_alone_rides_through_as_the_label() =>
        Assert.Equal("portrait", Service().ResolveEffectiveAspect("flux1-dev", "portrait", null));

    /// <summary>Neither supplied on a fixed-size config (no aspect map): the label is the shape of the config's own
    /// declared width/height — hunyuanvideo15-480p-t2v renders 832×480, a landscape.</summary>
    [Fact]
    public void A_fixed_size_config_with_nothing_supplied_labels_its_own_dims() =>
        Assert.Equal("landscape", Service().ResolveEffectiveAspect("hunyuanvideo15-480p-t2v", null, null));
}
