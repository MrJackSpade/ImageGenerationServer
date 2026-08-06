using ImageGen.Domain.CodeAnalysis;
using ImageGen.Domain.Repositories;
using ImageGen.Infrastructure.Database;
using System.Data.Common;

namespace ImageGen.Infrastructure.Repositories;

/// <summary>
/// Records and reads gen/edit render times in dbo.GenTiming. Stateless (a fresh connection per call), so it's
/// registered as a singleton — the singleton JobQueue resolves it from the root provider to log each successful
/// render's duration and to compute the per-machine ETA when a job starts.
/// </summary>
[AllowMagicStrings("SQL query text and its bound @parameter-name tokens")]
public sealed class GenTimingRepository(IDbConnectionFactory connectionFactory, ISqlDialect dialect) : IGenTimingRepository
{
    private readonly IDbConnectionFactory _connectionFactory = connectionFactory;
    /// <summary>Supplies the few SQL fragments the two engines spell differently.</summary>
    private readonly ISqlDialect _dialect = dialect;

    public async Task AddAsync(GenTimingEntry entry, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command(@"
INSERT INTO dbo.GenTiming (MachineName, ConfigId, IsEdit, DurationMs, RenderWidth, RenderHeight, Steps, Frames)
VALUES (@m, @c, @edit, @ms, @rw, @rh, @steps, @frames);");
        _ = cmd.AddParam("@m", entry.MachineName);
        _ = cmd.AddParam("@c", entry.ConfigId);
        _ = cmd.AddParam("@edit", entry.IsEdit);
        _ = cmd.AddParam("@ms", entry.DurationMs);
        _ = cmd.AddParam("@rw", (object?)entry.RenderWidth ?? DBNull.Value);
        _ = cmd.AddParam("@rh", (object?)entry.RenderHeight ?? DBNull.Value);
        _ = cmd.AddParam("@steps", (object?)entry.Steps ?? DBNull.Value);
        _ = cmd.AddParam("@frames", (object?)entry.Frames ?? DBNull.Value);
        _ = await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<double?> EtaAverageMsAsync(string machineName, string configId, EtaSignature current, int take, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        // Only signature-bearing rows (RenderWidth captured) — a machine/config with none yields null, and the caller
        // shows NO ETA (there is no fall-back to a param-blind average). Unit-cost: scale each sample's time by how the
        // CURRENT request's pixels×steps×frames compares to that sample's, then average — so an unseen param combo still
        // gets a scaled estimate, and a config whose params never vary returns ~the plain average (every ratio ≈ 1).
        await using DbCommand cmd = conn.Command($@"
SELECT {_dialect.TopPrefix("@take")}DurationMs, RenderWidth, RenderHeight, Steps, Frames
FROM dbo.GenTiming
WHERE MachineName = @m AND ConfigId = @c AND RenderWidth IS NOT NULL
ORDER BY Id DESC{_dialect.TopSuffix("@take")};");
        _ = cmd.AddParam("@take", take);
        _ = cmd.AddParam("@m", machineName);
        _ = cmd.AddParam("@c", configId);
        double currentWork = current.Work();
        double sum = 0;
        int n = 0;
        await using DbDataReader rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            double ms = rd.AsDouble(0);
            double w = rd.IsDBNull(1) ? 0 : rd.AsDouble(1);
            double h = rd.IsDBNull(2) ? 0 : rd.AsDouble(2);
            double steps = rd.IsDBNull(3) ? 1 : Math.Max(1, rd.AsDouble(3));
            double frames = rd.IsDBNull(4) ? 1 : Math.Max(1, rd.AsDouble(4));
            double rowWork = Math.Max(1.0, w * h) * steps * frames;
            sum += ms * currentWork / rowWork;
            n++;
        }

        return n > 0 ? sum / n : null;
    }

    public async Task<IReadOnlyDictionary<string, double>> RecentAveragesMsAsync(string machineName, int take, CancellationToken ct)
    {
        // One round-trip for the whole catalog: average the last @take durations PER ConfigId on this machine.
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command(@"
SELECT ConfigId, AVG(CAST(DurationMs AS FLOAT)) AS AvgMs
FROM (
    SELECT ConfigId, DurationMs,
           ROW_NUMBER() OVER (PARTITION BY ConfigId ORDER BY Id DESC) AS rn
    FROM dbo.GenTiming
    WHERE MachineName = @m
) t
WHERE rn <= @take
GROUP BY ConfigId;");
        _ = cmd.AddParam("@m", machineName);
        _ = cmd.AddParam("@take", take);
        Dictionary<string, double> map = new(StringComparer.Ordinal);
        await using DbDataReader rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            map[rd.GetString(0)] = rd.AsDouble(1);
        }

        return map;
    }
}
