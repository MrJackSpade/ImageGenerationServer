using ImageGen.Domain.CodeAnalysis;
using ImageGen.Domain.Repositories;
using ImageGen.Infrastructure.Database;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using System.Data;
using System.Data.Common;

namespace ImageGen.Infrastructure.Repositories;

/// <summary>
/// <see cref="ICatalogOverrideRepository"/> over <c>dbo.ModelBinding</c> and <c>dbo.ConfigOverride</c>. Stateless
/// (a fresh connection per call), so it registers as a singleton alongside the other machine-scoped repositories.
///
/// <para>Nothing here is encrypted. These rows are facts about the machine — a filename on its disk, a VRAM
/// figure for its GPU — not a user's words, and there is no owning user to key a cipher by.</para>
/// </summary>
[AllowMagicStrings("SQL query text and its bound @parameter-name tokens")]
public sealed class CatalogOverrideRepository(IDbConnectionFactory connectionFactory) : ICatalogOverrideRepository
{
    private readonly IDbConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, ModelBinding>> BindingsAsync(string machineName, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command(
            "SELECT SlotId, FileName, IsAuto FROM dbo.ModelBinding WHERE MachineName = @m;");
        _ = cmd.AddParam("@m", machineName);

        Dictionary<string, ModelBinding> result = new(StringComparer.OrdinalIgnoreCase);
        await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string slot = reader.GetString(0);
            result[slot] = new ModelBinding(slot, reader.GetString(1), reader.AsBool(2));
        }

        return result;
    }

    /// <inheritdoc/>
    public async Task SetBindingAsync(
        string machineName, string slotId, string? fileName, bool isAuto, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbTransaction tx = await conn.BeginTransactionAsync(ct);

        // Delete-then-insert rather than an engine-specific upsert: MERGE and ON CONFLICT are spelled differently
        // and this is a single small row guarded by a unique index. The transaction is what makes it atomic.
        await using (DbCommand del = conn.Command(
            "DELETE FROM dbo.ModelBinding WHERE MachineName = @m AND SlotId = @s;"))
        {
            del.Transaction = tx;
            _ = del.AddParam("@m", machineName);
            _ = del.AddParam("@s", slotId);
            _ = await del.ExecuteNonQueryAsync(ct);
        }

        if (!string.IsNullOrWhiteSpace(fileName))
        {
            await using DbCommand ins = conn.Command(@"
INSERT INTO dbo.ModelBinding (MachineName, SlotId, FileName, IsAuto, UpdatedAtUtc)
VALUES (@m, @s, @f, @auto, @now);");
            ins.Transaction = tx;
            _ = ins.AddParam("@m", machineName);
            _ = ins.AddParam("@s", slotId);
            _ = ins.AddParam("@f", fileName.Trim());
            _ = ins.AddParam("@auto", isAuto);
            _ = ins.AddParam("@now", DateTime.UtcNow);
            _ = await ins.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    /// <inheritdoc/>
    public async Task AddAutoBindingsAsync(
        string machineName, IReadOnlyDictionary<string, string> slotToFile, CancellationToken ct)
    {
        if (slotToFile.Count == 0)
        {
            return;
        }

        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbTransaction tx = await conn.BeginTransactionAsync(ct);

        foreach ((string? slot, string? file) in slotToFile)
        {
            // WHERE NOT EXISTS, not a blind insert: a slot the user has already bound by hand must never be
            // overwritten by a pattern, and this runs on every catalogue load.
            await using DbCommand cmd = conn.Command(@"
INSERT INTO dbo.ModelBinding (MachineName, SlotId, FileName, IsAuto, UpdatedAtUtc)
SELECT @m, @s, @f, 1, @now
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.ModelBinding WHERE MachineName = @m AND SlotId = @s
);");
            cmd.Transaction = tx;
            _ = cmd.AddParam("@m", machineName);
            _ = cmd.AddParam("@s", slot);
            _ = cmd.AddParam("@f", file);
            _ = cmd.AddParam("@now", DateTime.UtcNow);
            _ = await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, ConfigModelBindingOverride>>> BindingOverridesAsync(
        string machineName, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command(@"
SELECT ConfigId, SlotId, FileName, UpdatedAtUtc
FROM dbo.ConfigModelBindingOverride
WHERE MachineName = @m;");
        _ = cmd.AddParam("@m", machineName);

        Dictionary<string, Dictionary<string, ConfigModelBindingOverride>> byConfig =
            new(StringComparer.OrdinalIgnoreCase);
        await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string configId = reader.GetString(0);
            string slotId = reader.GetString(1);
            if (!byConfig.TryGetValue(configId, out Dictionary<string, ConfigModelBindingOverride>? slots))
            {
                byConfig[configId] = slots = new Dictionary<string, ConfigModelBindingOverride>(StringComparer.OrdinalIgnoreCase);
            }

            slots[slotId] = new ConfigModelBindingOverride(
                configId, slotId, reader.GetString(2), DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc));
        }

        return byConfig.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyDictionary<string, ConfigModelBindingOverride>)kv.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public async Task<WorkflowBindingResult> SetConfigBindingAsync(
        string machineName, string configId, string slotId, string fileName, CancellationToken ct)
    {
        string selected = fileName.Trim();
        if (selected.Length == 0)
        {
            throw new ArgumentException("A model filename is required.", nameof(fileName));
        }

        // The unique shared-binding key is the arbiter for concurrent first selections. Serializable protects the
        // not-exists predicate on SQL Server; SQLite serializes the write statement. A deadlock/busy/unique race is
        // retried from a fresh transaction, where the winner's shared row is visible and this caller deterministically
        // becomes the workflow pin.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await SetConfigBindingAttemptAsync(machineName, configId, slotId, selected, ct);
            }
            catch (Exception ex) when (attempt < 4 && IsConcurrencyConflict(ex))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10 * (attempt + 1)), ct);
            }
        }
    }

    private async Task<WorkflowBindingResult> SetConfigBindingAttemptAsync(
        string machineName, string configId, string slotId, string fileName, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbTransaction tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        int inserted;
        await using (DbCommand shared = conn.Command(@"
INSERT INTO dbo.ModelBinding (MachineName, SlotId, FileName, IsAuto, UpdatedAtUtc)
SELECT @m, @s, @f, 0, @now
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.ModelBinding WHERE MachineName = @m AND SlotId = @s
);"))
        {
            shared.Transaction = tx;
            _ = shared.AddParam("@m", machineName);
            _ = shared.AddParam("@s", slotId);
            _ = shared.AddParam("@f", fileName);
            _ = shared.AddParam("@now", DateTime.UtcNow);
            inserted = await shared.ExecuteNonQueryAsync(ct);
        }

        if (inserted > 0)
        {
            await DeleteConfigBindingAsync(conn, tx, machineName, configId, slotId, ct);
            await tx.CommitAsync(ct);
            return WorkflowBindingResult.SharedCreated;
        }

        await DeleteConfigBindingAsync(conn, tx, machineName, configId, slotId, ct);
        await using (DbCommand pin = conn.Command(@"
INSERT INTO dbo.ConfigModelBindingOverride (MachineName, ConfigId, SlotId, FileName, UpdatedAtUtc)
VALUES (@m, @c, @s, @f, @now);"))
        {
            pin.Transaction = tx;
            _ = pin.AddParam("@m", machineName);
            _ = pin.AddParam("@c", configId);
            _ = pin.AddParam("@s", slotId);
            _ = pin.AddParam("@f", fileName);
            _ = pin.AddParam("@now", DateTime.UtcNow);
            _ = await pin.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return WorkflowBindingResult.WorkflowPinned;
    }

    /// <inheritdoc/>
    public async Task ClearConfigBindingAsync(
        string machineName, string configId, string slotId, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command(@"
DELETE FROM dbo.ConfigModelBindingOverride
WHERE MachineName = @m AND ConfigId = @c AND SlotId = @s;");
        _ = cmd.AddParam("@m", machineName);
        _ = cmd.AddParam("@c", configId);
        _ = cmd.AddParam("@s", slotId);
        _ = await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc/>
    public async Task CopyConfigBindingsAsync(
        string machineName, string sourceConfigId, string targetConfigId, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbTransaction tx = await conn.BeginTransactionAsync(ct);

        await using (DbCommand del = conn.Command(@"
DELETE FROM dbo.ConfigModelBindingOverride WHERE MachineName = @m AND ConfigId = @target;"))
        {
            del.Transaction = tx;
            _ = del.AddParam("@m", machineName);
            _ = del.AddParam("@target", targetConfigId);
            _ = await del.ExecuteNonQueryAsync(ct);
        }

        await using (DbCommand copy = conn.Command(@"
INSERT INTO dbo.ConfigModelBindingOverride (MachineName, ConfigId, SlotId, FileName, UpdatedAtUtc)
SELECT MachineName, @target, SlotId, FileName, @now
FROM dbo.ConfigModelBindingOverride
WHERE MachineName = @m AND ConfigId = @source;"))
        {
            copy.Transaction = tx;
            _ = copy.AddParam("@m", machineName);
            _ = copy.AddParam("@source", sourceConfigId);
            _ = copy.AddParam("@target", targetConfigId);
            _ = copy.AddParam("@now", DateTime.UtcNow);
            _ = await copy.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    /// <inheritdoc/>
    public async Task ClearConfigBindingsAsync(string machineName, string configId, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command(
            "DELETE FROM dbo.ConfigModelBindingOverride WHERE MachineName = @m AND ConfigId = @c;");
        _ = cmd.AddParam("@m", machineName);
        _ = cmd.AddParam("@c", configId);
        _ = await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task DeleteConfigBindingAsync(
        DbConnection conn, DbTransaction tx, string machineName, string configId, string slotId, CancellationToken ct)
    {
        await using DbCommand del = conn.Command(@"
DELETE FROM dbo.ConfigModelBindingOverride
WHERE MachineName = @m AND ConfigId = @c AND SlotId = @s;");
        del.Transaction = tx;
        _ = del.AddParam("@m", machineName);
        _ = del.AddParam("@c", configId);
        _ = del.AddParam("@s", slotId);
        _ = await del.ExecuteNonQueryAsync(ct);
    }

    private static bool IsConcurrencyConflict(Exception ex) => ex switch
    {
        SqlException sql => sql.Number is 1205 or 2601 or 2627,
        SqliteException sqlite => sqlite.SqliteErrorCode is 5 or 6 or 19,
        _ => false,
    };

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> OverridesAsync(
        string machineName, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command(
            "SELECT ConfigId, SettingKey, SettingValue FROM dbo.ConfigOverride WHERE MachineName = @m;");
        _ = cmd.AddParam("@m", machineName);

        Dictionary<string, Dictionary<string, string>> byConfig = new(StringComparer.OrdinalIgnoreCase);
        await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string configId = reader.GetString(0);
            if (!byConfig.TryGetValue(configId, out Dictionary<string, string>? settings))
            {
                byConfig[configId] = settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

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
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbTransaction tx = await conn.BeginTransactionAsync(ct);

        await using (DbCommand del = conn.Command(
            "DELETE FROM dbo.ConfigOverride WHERE MachineName = @m AND ConfigId = @c AND SettingKey = @k;"))
        {
            del.Transaction = tx;
            _ = del.AddParam("@m", machineName);
            _ = del.AddParam("@c", configId);
            _ = del.AddParam("@k", settingKey);
            _ = await del.ExecuteNonQueryAsync(ct);
        }

        // Blank REMOVES the override rather than storing an empty string, so "reset to the shipped default" and
        // "set it to nothing" cannot be confused with each other.
        if (!string.IsNullOrWhiteSpace(settingValue))
        {
            await using DbCommand ins = conn.Command(@"
INSERT INTO dbo.ConfigOverride (MachineName, ConfigId, SettingKey, SettingValue, UpdatedAtUtc)
VALUES (@m, @c, @k, @v, @now);");
            ins.Transaction = tx;
            _ = ins.AddParam("@m", machineName);
            _ = ins.AddParam("@c", configId);
            _ = ins.AddParam("@k", settingKey);
            _ = ins.AddParam("@v", settingValue.Trim());
            _ = ins.AddParam("@now", DateTime.UtcNow);
            _ = await ins.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    /// <inheritdoc/>
    public async Task ClearOverridesAsync(string machineName, string configId, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command(
            "DELETE FROM dbo.ConfigOverride WHERE MachineName = @m AND ConfigId = @c;");
        _ = cmd.AddParam("@m", machineName);
        _ = cmd.AddParam("@c", configId);
        _ = await cmd.ExecuteNonQueryAsync(ct);
    }
}
