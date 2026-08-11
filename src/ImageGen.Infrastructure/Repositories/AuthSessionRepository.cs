using ImageGen.Domain.CodeAnalysis;
using ImageGen.Domain.Repositories;
using ImageGen.Infrastructure.Database;
using System.Data.Common;

namespace ImageGen.Infrastructure.Repositories;

/// <summary>Stateless (a fresh connection per call) — registered singleton so the cookie handler's singleton
/// session store can hold it.</summary>
[AllowMagicStrings("SQL query text and its bound @parameter-name tokens")]
public sealed class AuthSessionRepository(IDbConnectionFactory connectionFactory) : IAuthSessionRepository
{
    private readonly IDbConnectionFactory _connectionFactory = connectionFactory;

    public async Task UpsertAsync(string key, byte[] ticket, DateTime? expiresAtUtc, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);

        // Update-then-insert, like MachineSettingRepository: a session key has a single owner (the browser that
        // holds the cookie), so the two statements cannot race another writer for the same key.
        await using (DbCommand update = conn.Command(
            "UPDATE dbo.AuthSession SET Ticket = @ticket, ExpiresAtUtc = @expires WHERE SessionKey = @key;"))
        {
            _ = update.AddLargeParam("@ticket", ticket);
            _ = update.AddParam("@expires", expiresAtUtc);
            _ = update.AddParam("@key", key);
            if (await update.ExecuteNonQueryAsync(ct) > 0)
            {
                return;
            }
        }

        await using DbCommand insert = conn.Command(
            "INSERT INTO dbo.AuthSession (SessionKey, Ticket, ExpiresAtUtc) VALUES (@key, @ticket, @expires);");
        _ = insert.AddParam("@key", key);
        _ = insert.AddLargeParam("@ticket", ticket);
        _ = insert.AddParam("@expires", expiresAtUtc);
        _ = await insert.ExecuteNonQueryAsync(ct);
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        // The expiry filter is the session's own lapse check — a lapsed row is dead the moment its time passes,
        // whether or not DeleteExpiredAsync has swept it yet.
        await using DbCommand cmd = conn.Command(
            "SELECT Ticket FROM dbo.AuthSession WHERE SessionKey = @key AND (ExpiresAtUtc IS NULL OR ExpiresAtUtc > @now);");
        _ = cmd.AddParam("@key", key);
        _ = cmd.AddParam("@now", DateTime.UtcNow);
        return await cmd.ExecuteScalarAsync(ct) as byte[];
    }

    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command("DELETE FROM dbo.AuthSession WHERE SessionKey = @key;");
        _ = cmd.AddParam("@key", key);
        _ = await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteExpiredAsync(DateTime nowUtc, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command(
            "DELETE FROM dbo.AuthSession WHERE ExpiresAtUtc IS NOT NULL AND ExpiresAtUtc <= @now;");
        _ = cmd.AddParam("@now", nowUtc);
        _ = await cmd.ExecuteNonQueryAsync(ct);
    }
}
