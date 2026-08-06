using ImageGen.Domain.CodeAnalysis;
using ImageGen.Domain.Repositories;
using ImageGen.Infrastructure.Database;
using System.Data.Common;

namespace ImageGen.Infrastructure.Repositories;

/// <summary>
/// <see cref="IMachineSettingRepository"/> over <c>dbo.MachineSetting</c>. Stateless (a fresh connection per call),
/// so it registers as a singleton alongside the other machine-scoped repositories.
/// </summary>
[AllowMagicStrings("SQL query text and its bound @parameter-name tokens")]
public sealed class MachineSettingRepository(IDbConnectionFactory connectionFactory) : IMachineSettingRepository
{
    private readonly IDbConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, string>> AllAsync(string machineName, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command(
            "SELECT SettingKey, SettingValue FROM dbo.MachineSetting WHERE MachineName = @m;");
        _ = cmd.AddParam("@m", machineName);

        // Ordinal-insensitive, because that is how IConfiguration compares keys.
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }

        return result;
    }

    /// <inheritdoc/>
    public async Task SetAsync(string machineName, string key, string? value, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbTransaction tx = await conn.BeginTransactionAsync(ct);

        // Delete-then-insert rather than an engine-specific upsert: MERGE and ON CONFLICT are spelled differently,
        // and this is one small row guarded by a unique index. The transaction is what makes it atomic.
        await using (DbCommand del = conn.Command(
            "DELETE FROM dbo.MachineSetting WHERE MachineName = @m AND SettingKey = @k;"))
        {
            del.Transaction = tx;
            _ = del.AddParam("@m", machineName);
            _ = del.AddParam("@k", key);
            _ = await del.ExecuteNonQueryAsync(ct);
        }

        if (value is not null)
        {
            await using DbCommand ins = conn.Command(@"
INSERT INTO dbo.MachineSetting (MachineName, SettingKey, SettingValue, UpdatedAtUtc)
VALUES (@m, @k, @v, @now);");
            ins.Transaction = tx;
            _ = ins.AddParam("@m", machineName);
            _ = ins.AddParam("@k", key);
            _ = ins.AddParam("@v", value);
            _ = ins.AddParam("@now", DateTime.UtcNow);
            _ = await ins.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }
}
