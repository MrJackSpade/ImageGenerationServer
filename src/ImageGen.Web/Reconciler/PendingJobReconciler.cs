using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;

namespace ImageGen.Web.Reconciler;

/// <summary>
/// Vestigial housekeeping. History is now written exactly once, server-side, by the <c>JobQueue</c> worker the moment
/// an image is produced — the SOLE writer. The browser never writes history, and NEITHER DOES THIS RECONCILER: a second
/// writer using an insert-if-absent would resurrect a deleted image (it can't tell "never made" from "made then
/// deleted"). So this loop no longer writes anything — it only clears the now-pointless <c>PendingJob</c> rows once
/// their job has finalized (or aged out), so the table doesn't grow.
///
/// <para><c>PendingJob</c> + <c>POST /api/pending</c> are obsolete (the worker covers closed-tab persistence because it
/// runs regardless of any browser) and can be removed wholesale in a follow-up; this is kept minimal for now.</para>
///
/// Cross-instance rule (invariant #4): only this instance's own jobs (matching <see cref="JobRecord.MachineName"/>) are
/// touched; another instance's rows are left for it, and orphans age out via <c>maxAge</c>.
/// </summary>
public sealed class PendingJobReconciler(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    ILogger<PendingJobReconciler> logger) : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly IConfiguration _config = config;
    private readonly ILogger<PendingJobReconciler> _logger = logger;
    private readonly string _machine = Environment.MachineName;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollSeconds = Math.Clamp(_config.GetValue("Reconciler:PollSeconds", 15), 3, 600);
        var maxAge = TimeSpan.FromHours(Math.Clamp(_config.GetValue("Reconciler:MaxAgeHours", 3.0), 0.1, 24.0));

        _logger.LogInformation("PendingJobReconciler started (poll {Poll}s, history is worker-written; this only reaps pending rows).", pollSeconds);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(pollSeconds));
        do
        {
            try { await ReconcileOnceAsync(maxAge, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "PendingJobReconciler cycle failed."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task ReconcileOnceAsync(TimeSpan maxAge, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var pending = scope.ServiceProvider.GetRequiredService<Application.Services.PendingJobService>();
        var jobs = scope.ServiceProvider.GetRequiredService<IJobRepository>();

        var rows = await pending.ListAllAsync(ct);
        if (rows.Count == 0)
            return;

        var now = DateTime.UtcNow;
        foreach (var pj in rows)
        {
            ct.ThrowIfCancellationRequested();

            if (now - pj.CreatedAtUtc > maxAge)
            {
                await pending.RemoveAsync(pj.Id, ct);   // aged out — drop it
                continue;
            }

            var job = await jobs.GetAsync(pj.JobId, ct);
            if (job is null) continue;                       // not persisted yet (race) — next cycle
            if (job.MachineName != _machine) continue;       // another instance's job — leave it (invariant #4)
            if (job.Status == JobStatus.Active) continue;    // still rendering — leave it

            // Finalized: the worker already wrote this job's history at completion. Nothing to persist here — just
            // clear the vestigial pending row. (No history write => no resurrection of a since-deleted image.)
            await pending.RemoveAsync(pj.Id, ct);
        }
    }
}
