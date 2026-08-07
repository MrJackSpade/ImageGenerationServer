using ImageGen.Application.Snapshots;

namespace ImageGen.Web.Hosting;

/// <summary>
/// Hosted-service adapter that runs the <see cref="SnapshotSyncWorker"/>'s serial rebuild loop. Lives in the web host
/// so the Application layer stays free of the generic host: the worker is a plain singleton exposing
/// <see cref="SnapshotSyncWorker.RunAsync"/>, and this bridges it to a <see cref="BackgroundService"/> — the same split
/// as <see cref="RenderWorker"/>.
/// </summary>
public sealed class SnapshotSyncService(SnapshotSyncWorker worker) : BackgroundService
{
    private readonly SnapshotSyncWorker _worker = worker;

    /// <inheritdoc/>
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => _worker.RunAsync(stoppingToken);
}
