using System.Data.Common;
using ImageGen.Application.Security;
using ImageGen.Domain.CodeAnalysis;
using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;
using ImageGen.Infrastructure.Database;
using Microsoft.Data.SqlClient;

namespace ImageGen.Infrastructure.Repositories;

/// <summary>
/// ADO.NET storage for the per-user encrypted application log (<c>dbo.UserLog</c>). Payloads are stored verbatim
/// (already ciphertext) on write and decrypted on read. Stateless (fresh connection per call) → registered singleton.
/// </summary>
[AllowMagicStrings("SQL query text and its bound @parameter-name tokens")]
public sealed class UserLogRepository(IDbConnectionFactory connectionFactory, IUserCipher cipher, ISqlDialect dialect) : IUserLogRepository
{
    private readonly IDbConnectionFactory _connectionFactory = connectionFactory;
    /// <summary>Supplies the few SQL fragments the two engines spell differently.</summary>
    private readonly ISqlDialect _dialect = dialect;
    private readonly IUserCipher _cipher = cipher;

    public async Task AddAsync(
        long userId, string category, string encryptedPayload, DateTime createdAtUtc, CancellationToken ct)
    {
        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var cmd = conn.Command(
            "INSERT INTO dbo.UserLog (UserId, Category, Payload, CreatedAtUtc) "
            + "VALUES (@userId, @category, @payload, @created);");
        cmd.AddParam("@userId", userId);
        cmd.AddParam("@category", category);
        cmd.AddParam("@payload", encryptedPayload);
        cmd.AddParam("@created", createdAtUtc);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Row-count bounds for a recent-log read: at least one, and a hard ceiling the SQL TOP/LIMIT is bound to.</summary>
    private const int MinLimit = 1;
    private const int MaxLimit = 1000;

    public async Task<IReadOnlyList<UserLogEntry>> GetRecentAsync(long userId, int limit, CancellationToken ct)
    {
        // An out-of-range limit is REFUSED, not clamped — silently returning 1,000 for a request of a million reads
        // to the caller as "that's all there is".
        if (limit is < MinLimit or > MaxLimit)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, $"limit must be between {MinLimit} and {MaxLimit}.");
        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var cmd = conn.Command(
            $"SELECT {_dialect.TopPrefix("@limit")}Id, UserId, Category, Payload, CreatedAtUtc FROM dbo.UserLog "
            + $"WHERE UserId = @userId ORDER BY CreatedAtUtc DESC, Id DESC{_dialect.TopSuffix("@limit")};");
        cmd.AddParam("@limit", limit);
        cmd.AddParam("@userId", userId);

        var raw = new List<UserLogRow>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                raw.Add(new UserLogRow(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
                    DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc)));

        var list = new List<UserLogEntry>(raw.Count);
        foreach (var r in raw)
            list.Add(new UserLogEntry
            {
                Id = r.Id,
                UserId = r.UserId,
                Category = r.Category,
                Payload = await _cipher.DecryptAsync(r.UserId, r.Payload, ct),
                CreatedAtUtc = r.Created,
            });
        return list;
    }

    /// <summary>A raw user-log row buffered with its still-encrypted payload before deferred decryption.</summary>
    private readonly record struct UserLogRow(long Id, long UserId, string Category, string Payload, DateTime Created);
}
