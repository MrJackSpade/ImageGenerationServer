using ImageGen.Application.Snapshots;
using ImageGen.Comfy;
using ImageGen.Comfy.Snapshots;
using Microsoft.Extensions.DependencyInjection;

namespace ImageGen.Tests;

/// <summary>
/// The snapshot DI graph AddComfy stands up resolves without a cycle (#198/#199). The loaders capture the ComfyClient
/// lazily, so constructing the worker + facades never constructs the client or runs a probe — which is exactly why the
/// app can start before ComfyUI is up. Resolving these is the cheap proof the registration wiring is consistent.
/// </summary>
public sealed class ComfySnapshotWiringTests : IDisposable
{
    private readonly string _root;
    private readonly ServiceProvider _provider;

    public ComfySnapshotWiringTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "imggen-wire-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(Path.Combine(_root, "workflows"));
        _ = Directory.CreateDirectory(Path.Combine(_root, "models"));

        ServiceCollection services = new();
        _ = services.AddLogging();
        _ = services.AddComfy(new ComfyOptions { CatalogPath = _root });
        _provider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _provider.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public void The_sync_worker_resolves_over_all_registered_comfy_and_sql_sources()
    {
        // Constructing the worker resolves and attaches every SnapshotEntry singleton — no loader runs, so no
        // ComfyClient/probe is constructed here.
        SnapshotSyncWorker worker = _provider.GetRequiredService<SnapshotSyncWorker>();
        Assert.NotNull(worker);
    }

    [Fact]
    public void The_probe_and_sql_facades_resolve_to_their_snapshot_sources()
    {
        ComfyProbeSnapshots probes = _provider.GetRequiredService<ComfyProbeSnapshots>();
        Assert.NotNull(probes.FilesByKind);
        Assert.NotNull(probes.PresentNodes);
        Assert.NotNull(probes.FolderPaths);

        CatalogSnapshots sql = _provider.GetRequiredService<CatalogSnapshots>();
        Assert.NotNull(sql.Bindings);
        Assert.NotNull(sql.ParamOverrides);
        Assert.NotNull(sql.Variants);

        // The same singleton instance is shared between the read surface and the collection the worker enumerates.
        Assert.Same(probes.FilesByKind, _provider.GetRequiredService<ISnapshot<ComfyFilesByKind>>());
    }
}
