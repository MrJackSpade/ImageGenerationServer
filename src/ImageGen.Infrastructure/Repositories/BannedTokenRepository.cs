using System.Data.Common;
using ImageGen.Application.Security;
using ImageGen.Domain;
using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;
using ImageGen.Infrastructure.Database;
using Microsoft.Data.SqlClient;

namespace ImageGen.Infrastructure.Repositories;

public sealed class BannedTokenRepository(IDbConnectionFactory connectionFactory, IUserCipher cipher) : IBannedTokenRepository
{
    private const string Columns = "Id, UserId, ModelId, Name, Kind, SavedAtUtc";

    private readonly IDbConnectionFactory _connectionFactory = connectionFactory;
    private readonly IUserCipher _cipher = cipher;

    public async Task<IReadOnlyList<BannedToken>> GetAllAsync(long userId, CancellationToken ct)
    {
        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var cmd = conn.Command(
            $"SELECT {Columns} FROM dbo.BannedToken WHERE UserId = @userId "
            + "ORDER BY ModelId, SavedAtUtc DESC, Id DESC;");
        cmd.AddParam("@userId", userId);
        return await ReadAllAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<BannedToken>> GetForModelAsync(long userId, string modelId, CancellationToken ct)
    {
        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var cmd = conn.Command(
            $"SELECT {Columns} FROM dbo.BannedToken WHERE UserId = @userId AND ModelId = @modelId "
            + "ORDER BY SavedAtUtc DESC, Id DESC;");
        cmd.AddParam("@userId", userId);
        cmd.AddParam("@modelId", modelId);
        return await ReadAllAsync(cmd, ct);
    }

    public async Task<bool> AddAsync(BannedToken ban, CancellationToken ct)
    {
        const string sql = @"
INSERT INTO dbo.BannedToken (UserId, ModelId, Name, Kind, SavedAtUtc)
SELECT @userId, @modelId, @name, @kind, @saved
WHERE NOT EXISTS (SELECT 1 FROM dbo.BannedToken
                  WHERE UserId = @userId AND ModelId = @modelId AND Name = @name AND Kind = @kind);";
        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var cmd = conn.Command(sql);
        cmd.AddParam("@userId", ban.UserId);
        cmd.AddParam("@modelId", ban.ModelId);
        cmd.AddParam("@name", await _cipher.DeterministicAsync(ban.UserId, ban.Name, ct));
        cmd.AddParam("@kind", (byte)ban.Kind);
        cmd.AddParam("@saved", ban.SavedAtUtc);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> RemoveAsync(BannedTokenKey key, CancellationToken ct)
    {
        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var cmd = conn.Command(
            "DELETE FROM dbo.BannedToken WHERE UserId = @userId AND ModelId = @modelId AND Name = @name AND Kind = @kind;");
        cmd.AddParam("@userId", key.UserId);
        cmd.AddParam("@modelId", key.ModelId);
        cmd.AddParam("@name", await _cipher.DeterministicAsync(key.UserId, key.Name, ct));
        cmd.AddParam("@kind", (byte)key.Kind);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private async Task<IReadOnlyList<BannedToken>> ReadAllAsync(DbCommand cmd, CancellationToken ct)
    {
        var raw = new List<BannedTokenRow>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                raw.Add(new BannedTokenRow(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
                    (TokenKind)reader.AsByte(4), DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc)));

        var list = new List<BannedToken>(raw.Count);
        foreach (var r in raw)
            list.Add(new BannedToken
            {
                Id = r.Id,
                UserId = r.UserId,
                ModelId = r.ModelId,
                Name = await _cipher.DecryptDeterministicAsync(r.UserId, r.Name, ct),
                Kind = r.Kind,
                SavedAtUtc = r.Saved,
            });
        return list;
    }

    /// <summary>A raw banned-token row buffered with its still-encrypted name before deferred decryption.</summary>
    private readonly record struct BannedTokenRow(
        long Id, long UserId, string ModelId, string Name, TokenKind Kind, DateTime Saved);
}
