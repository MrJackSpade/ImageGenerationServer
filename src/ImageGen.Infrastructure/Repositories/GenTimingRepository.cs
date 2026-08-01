using System.Data.Common;
using ImageGen.Domain.Repositories;
using ImageGen.Infrastructure.Database;
using Microsoft.Data.SqlClient;

namespace ImageGen.Infrastructure.Repositories;

/// <summary>
/// Records and reads gen/edit render times in dbo.GenTiming. Stateless (a fresh connection per call), so it's
/// registered as a singleton — the singleton JobQueue resolves it from the root provider to log each successful
/// render's duration and to compute the per-machine ETA when a job starts.
/// </summary>
public sealed class GenTimingRepository(IDbConnectionFactory connectionFactory, ISqlDialect dialect) : IGenTimingRepository
{
    private readonly IDbConnectionFactory _connectionFactory = connectionFactory;
    /// <summary>Supplies the few SQL fragments the two engines spell differently.</summary>
    private readonly ISqlDialect _dialect = dialect;

    public async Task AddAsync(GenTimingEntry entry, CancellationToken ct)
    {
        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var cmd = conn.Command(@"
INSERT INTO dbo.GenTiming (MachineName, ConfigId, IsEdit, DurationMs)
VALUES (@m, @c, @edit, @ms);");
        cmd.AddParam("@m", entry.MachineName);
        cmd.AddParam("@c", entry.ConfigId);
        cmd.AddParam("@edit", entry.IsEdit);
        cmd.AddParam("@ms", entry.DurationMs);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<double?> RecentAverageMsAsync(string machineName, string configId, int take, CancellationToken ct)
    {
        await using var conn = await _connectionFactory.OpenAsync(ct);
        // CAST(... AS FLOAT) is portable: SQLite reads REAL affinity out of any type name containing "FLOA".
        await using var cmd = conn.Command($@"
SELECT AVG(CAST(DurationMs AS FLOAT))
FROM (
    SELECT {_dialect.TopPrefix("@take")}DurationMs
    FROM dbo.GenTiming
    WHERE MachineName = @m AND ConfigId = @c
    ORDER BY Id DESC{_dialect.TopSuffix("@take")}
) t;");
        cmd.AddParam("@take", take);
        cmd.AddParam("@m", machineName);
        cmd.AddParam("@c", configId);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : Convert.ToDouble(result);
    }

    public async Task<IReadOnlyDictionary<string, double>> RecentAveragesMsAsync(string machineName, int take, CancellationToken ct)
    {
        // One round-trip for the whole catalog: average the last @take durations PER ConfigId on this machine.
        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var cmd = conn.Command(@"
SELECT ConfigId, AVG(CAST(DurationMs AS FLOAT)) AS AvgMs
FROM (
    SELECT ConfigId, DurationMs,
           ROW_NUMBER() OVER (PARTITION BY ConfigId ORDER BY Id DESC) AS rn
    FROM dbo.GenTiming
    WHERE MachineName = @m
) t
WHERE rn <= @take
GROUP BY ConfigId;");
        cmd.AddParam("@m", machineName);
        cmd.AddParam("@take", take);
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            map[rd.GetString(0)] = rd.AsDouble(1);
        return map;
    }
}
