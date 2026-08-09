namespace ImageGen.Domain.Repositories;

/// <summary>
/// Durable per-machine record of how long each successful gen/edit actually took to render (ComfyUI execution
/// only, queue wait excluded). Drives the UI's ETA. Every read is parameter-MATCHED: a sample only ever prices a
/// render with the identical <see cref="EtaSignature"/> (resolution/steps/frames). Render time is not a linear
/// function of any of those, so samples are never scaled/extrapolated toward different params — an unmatched
/// signature has NO time, which the UI shows as unknown rather than a wrong number.
/// </summary>
public interface IGenTimingRepository
{
    /// <summary>Record one successful render's duration for a machine + workflow configuration.</summary>
    Task AddAsync(GenTimingEntry entry, CancellationToken ct);

    /// <summary>Per-configuration matched average (ms) on this machine, in one query: for each config, the average of
    /// its last <paramref name="take"/> renders whose signature is IDENTICAL to that config's most recent render —
    /// "how long with the params you're actually using". Keyed by config id; configs with no signature-bearing history
    /// are absent (the catalog then shows no time for them). Annotates the /workflows list and prices waiting queue
    /// slots in the header ETA.</summary>
    Task<IReadOnlyDictionary<string, double>> RecentAveragesMsAsync(string machineName, int take, CancellationToken ct);

    /// <summary>Matched ETA (ms) for this machine + configuration: the plain average of the last <paramref name="take"/>
    /// samples whose signature EXACTLY equals <paramref name="current"/> (null steps/frames matched null-to-null).
    /// Null when no sample matches — the UI then shows NO ETA. There is deliberately no fall-back to a param-blind
    /// average and no scaling of near-miss samples: both produce confidently wrong numbers, not honest unknowns.</summary>
    Task<double?> EtaAverageMsAsync(string machineName, string configId, EtaSignature current, int take, CancellationToken ct);
}
