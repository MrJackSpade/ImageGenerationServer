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

    /// <summary>Average render time (ms) of the last <paramref name="take"/> renders PER configuration on this
    /// machine, in one query. Keyed by config id; configs with no history here are absent (the catalog then shows
    /// no time for them). Used to annotate the /workflows list with a per-model average runtime.</summary>
    Task<IReadOnlyDictionary<string, double>> RecentAveragesMsAsync(string machineName, int take, CancellationToken ct);

    /// <summary>Parameter-MATCHED ETA (ms) for this machine + configuration: scales the recent signature-bearing samples
    /// by how much the current request's resolution × steps × frames differs from each sample's, then averages (a
    /// unit-cost model). Null when there are no signature samples yet — the UI then shows NO ETA. There is deliberately
    /// no fall-back to a param-blind average: on a fresh config that would be a wrong number, not an honest "unknown".</summary>
    Task<double?> EtaAverageMsAsync(string machineName, string configId, EtaSignature current, int take, CancellationToken ct);
}
