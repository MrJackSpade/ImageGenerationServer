using ImageGen.Domain.Repositories;
using ImageGen.Infrastructure.Database;

namespace ImageGen.Infrastructure.Repositories;

/// <summary>
/// <see cref="IMachineSettingRepository"/> over <c>dbo.MachineSetting</c>. Stateless (a fresh connection per call),
/// so it registers as a singleton alongside the other machine-scoped repositories.
/// </summary>
public sealed class MachineSettingRepository(IDbConnectionFactory connectionFactory) : IMachineSettingRepository
{
    private readonly IDbConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, string>> AllAsync(string machineName, CancellationToken ct)
    {
        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var cmd = conn.Command(
            "SELECT SettingKey, SettingValue FROM dbo.MachineSetting WHERE MachineName = @m;");
        cmd.AddParam("@m", machineName);

        // Ordinal-insensitive, because that is how IConfiguration compares keys.
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    /// <inheritdoc/>
    public async Task SetAsync(string machineName, string key, string? value, CancellationToken ct)
    {
        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Delete-then-insert rather than an engine-specific upsert: MERGE and ON CONFLICT are spelled differently,
        // and this is one small row guarded by a unique index. The transaction is what makes it atomic.
        await using (var del = conn.Command(
            "DELETE FROM dbo.MachineSetting WHERE MachineName = @m AND SettingKey = @k;"))
        {
            del.Transaction = tx;
            del.AddParam("@m", machineName);
            del.AddParam("@k", key);
            await del.ExecuteNonQueryAsync(ct);
        }

        if (value is not null)
        {
            await using var ins = conn.Command(@"
INSERT INTO dbo.MachineSetting (MachineName, SettingKey, SettingValue, UpdatedAtUtc)
VALUES (@m, @k, @v, @now);");
            ins.Transaction = tx;
            ins.AddParam("@m", machineName);
            ins.AddParam("@k", key);
            ins.AddParam("@v", value);
            ins.AddParam("@now", DateTime.UtcNow);
            await ins.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }
}
