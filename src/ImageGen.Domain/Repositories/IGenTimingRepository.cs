namespace ImageGen.Domain.Repositories;

/// <summary>
/// Durable per-machine record of how long each successful gen/edit actually took to render (ComfyUI execution
/// only, queue wait excluded). Drives the UI's ETA: the expected time for a model on a machine is the average of
/// its most recent records there.
/// </summary>
public interface IGenTimingRepository
{
    /// <summary>Record one successful render's duration for a machine + workflow configuration.</summary>
    Task AddAsync(GenTimingEntry entry, CancellationToken ct);

    /// <summary>Average render time (ms) of the last <paramref name="take"/> successful renders for this machine +
    /// configuration, or null if it has never rendered here (so the UI shows no ETA the first time).</summary>
    Task<double?> RecentAverageMsAsync(string machineName, string configId, int take, CancellationToken ct);

    /// <summary>Average render time (ms) of the last <paramref name="take"/> renders PER configuration on this
    /// machine, in one query. Keyed by config id; configs with no history here are absent (the catalog then shows
    /// no time for them). Used to annotate the /workflows list with a per-model average runtime.</summary>
    Task<IReadOnlyDictionary<string, double>> RecentAveragesMsAsync(string machineName, int take, CancellationToken ct);
}
