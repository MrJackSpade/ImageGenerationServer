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
        // EXACT-match samples only: a sample prices this request only when it rendered the identical signature —
        // same resolution, same steps, same frames (null matched to null). Render time is not linear in any of these
        // (video attention is superlinear in frames, and every model carries fixed overhead), so near-miss samples are
        // never scaled toward the current request; a signature with no matching history yields null and the caller
        // shows NO ETA.
        await using DbCommand cmd = conn.Command($@"
SELECT AVG(CAST(DurationMs AS FLOAT))
FROM (
    SELECT {_dialect.TopPrefix("@take")}DurationMs
    FROM dbo.GenTiming
    WHERE MachineName = @m AND ConfigId = @c
      AND RenderWidth = @w AND RenderHeight = @h
      AND ((@steps IS NULL AND Steps IS NULL) OR Steps = @steps)
      AND ((@frames IS NULL AND Frames IS NULL) OR Frames = @frames)
    ORDER BY Id DESC{_dialect.TopSuffix("@take")}
) t;");
        _ = cmd.AddParam("@take", take);
        _ = cmd.AddParam("@m", machineName);
        _ = cmd.AddParam("@c", configId);
        _ = cmd.AddParam("@w", current.Width);
        _ = cmd.AddParam("@h", current.Height);
        _ = cmd.AddParam("@steps", (object?)current.Steps ?? DBNull.Value);
        _ = cmd.AddParam("@frames", (object?)current.Frames ?? DBNull.Value);
        object? avg = await cmd.ExecuteScalarAsync(ct);
        return avg is null or DBNull ? null : Convert.ToDouble(avg, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyDictionary<string, double>> RecentAveragesMsAsync(string machineName, int take, CancellationToken ct)
    {
        // One round-trip for the whole catalog. Same matched-only rule as the per-request ETA: each config is priced
        // from renders whose signature (resolution/steps/frames) is IDENTICAL to that config's most recent render —
        // "how long does this take with the params you're actually using" — never a blend across signatures, because
        // render time is not linear in any of them. A config with no signature-bearing rows is absent (no time shown).
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command(@"
WITH latest AS (
    SELECT ConfigId, RenderWidth, RenderHeight, Steps, Frames,
           ROW_NUMBER() OVER (PARTITION BY ConfigId ORDER BY Id DESC) AS rn
    FROM dbo.GenTiming
    WHERE MachineName = @m AND RenderWidth IS NOT NULL
),
matched AS (
    SELECT g.ConfigId, g.DurationMs,
           ROW_NUMBER() OVER (PARTITION BY g.ConfigId ORDER BY g.Id DESC) AS rn
    FROM dbo.GenTiming g
    INNER JOIN latest l ON l.ConfigId = g.ConfigId AND l.rn = 1
        AND g.RenderWidth = l.RenderWidth AND g.RenderHeight = l.RenderHeight
        AND ((g.Steps IS NULL AND l.Steps IS NULL) OR g.Steps = l.Steps)
        AND ((g.Frames IS NULL AND l.Frames IS NULL) OR g.Frames = l.Frames)
    WHERE g.MachineName = @m
)
SELECT ConfigId, AVG(CAST(DurationMs AS FLOAT)) AS AvgMs
FROM matched
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
