using ImageGen.Domain.Repositories;
using ImageGen.Infrastructure.Database;

namespace ImageGen.Infrastructure.Repositories;

/// <summary>
/// <see cref="ICatalogOverrideRepository"/> over <c>dbo.ModelBinding</c> and <c>dbo.ConfigOverride</c>. Stateless
/// (a fresh connection per call), so it registers as a singleton alongside the other machine-scoped repositories.
///
/// <para>Nothing here is encrypted. These rows are facts about the machine — a filename on its disk, a VRAM
/// figure for its GPU — not a user's words, and there is no owning user to key a cipher by.</para>
/// </summary>
public sealed class CatalogOverrideRepository(IDbConnectionFactory connectionFactory) : ICatalogOverrideRepository
{
    private readonly IDbConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, ModelBinding>> BindingsAsync(string machineName, CancellationToken ct)
    {
        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var cmd = conn.Command(
            "SELECT SlotId, FileName, IsAuto FROM dbo.ModelBinding WHERE MachineName = @m;");
        cmd.AddParam("@m", machineName);

        var result = new Dictionary<string, ModelBinding>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var slot = reader.GetString(0);
            result[slot] = new ModelBinding(slot, reader.GetString(1), reader.AsBool(2));
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task SetBindingAsync(
        string machineName, string slotId, string? fileName, bool isAuto, CancellationToken ct)
    {
        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Delete-then-insert rather than an engine-specific upsert: MERGE and ON CONFLICT are spelled differently
        // and this is a single small row guarded by a unique index. The transaction is what makes it atomic.
        await using (var del = conn.Command(
            "DELETE FROM dbo.ModelBinding WHERE MachineName = @m AND SlotId = @s;"))
        {
            del.Transaction = tx;
            del.AddParam("@m", machineName);
            del.AddParam("@s", slotId);
            await del.ExecuteNonQueryAsync(ct);
        }

        if (!string.IsNullOrWhiteSpace(fileName))
        {
            await using var ins = conn.Command(@"
INSERT INTO dbo.ModelBinding (MachineName, SlotId, FileName, IsAuto, UpdatedAtUtc)
VALUES (@m, @s, @f, @auto, @now);");
            ins.Transaction = tx;
            ins.AddParam("@m", machineName);
            ins.AddParam("@s", slotId);
            ins.AddParam("@f", fileName.Trim());
            ins.AddParam("@auto", isAuto);
            ins.AddParam("@now", DateTime.UtcNow);
            await ins.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    /// <inheritdoc/>
    public async Task AddAutoBindingsAsync(
        string machineName, IReadOnlyDictionary<string, string> slotToFile, CancellationToken ct)
    {
        if (slotToFile.Count == 0) return;

        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        foreach (var (slot, file) in slotToFile)
        {
            // WHERE NOT EXISTS, not a blind insert: a slot the user has already bound by hand must never be
            // overwritten by a pattern, and this runs on every catalogue load.
            await using var cmd = conn.Command(@"
INSERT INTO dbo.ModelBinding (MachineName, SlotId, FileName, IsAuto, UpdatedAtUtc)
SELECT @m, @s, @f, 1, @now
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.ModelBinding WHERE MachineName = @m AND SlotId = @s
);");
            cmd.Transaction = tx;
            cmd.AddParam("@m", machineName);
            cmd.AddParam("@s", slot);
            cmd.AddParam("@f", file);
            cmd.AddParam("@now", DateTime.UtcNow);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> OverridesAsync(
        string machineName, CancellationToken ct)
    {
        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var cmd = conn.Command(
            "SELECT ConfigId, SettingKey, SettingValue FROM dbo.ConfigOverride WHERE MachineName = @m;");
        cmd.AddParam("@m", machineName);

        var byConfig = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var configId = reader.GetString(0);
            if (!byConfig.TryGetValue(configId, out var settings))
                byConfig[configId] = settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            settings[reader.GetString(1)] = reader.GetString(2);
        }

        return byConfig.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyDictionary<string, string>)kv.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public async Task SetOverrideAsync(
        string machineName, string configId, string settingKey, string? settingValue, CancellationToken ct)
    {
        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var del = conn.Command(
            "DELETE FROM dbo.ConfigOverride WHERE MachineName = @m AND ConfigId = @c AND SettingKey = @k;"))
        {
            del.Transaction = tx;
            del.AddParam("@m", machineName);
            del.AddParam("@c", configId);
            del.AddParam("@k", settingKey);
            await del.ExecuteNonQueryAsync(ct);
        }

        // Blank REMOVES the override rather than storing an empty string, so "reset to the shipped default" and
        // "set it to nothing" cannot be confused with each other.
        if (!string.IsNullOrWhiteSpace(settingValue))
        {
            await using var ins = conn.Command(@"
INSERT INTO dbo.ConfigOverride (MachineName, ConfigId, SettingKey, SettingValue, UpdatedAtUtc)
VALUES (@m, @c, @k, @v, @now);");
            ins.Transaction = tx;
            ins.AddParam("@m", machineName);
            ins.AddParam("@c", configId);
            ins.AddParam("@k", settingKey);
            ins.AddParam("@v", settingValue.Trim());
            ins.AddParam("@now", DateTime.UtcNow);
            await ins.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }
}
