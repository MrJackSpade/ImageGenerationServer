using System.Data.Common;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using ImageGen.Application.Security;
using ImageGen.Domain.CodeAnalysis;
using ImageGen.Infrastructure.Database;
using Microsoft.Data.SqlClient;

namespace ImageGen.Infrastructure.Security;

/// <summary>
/// <see cref="IUserCipher"/> backed by <c>dbo.UserEncryptionKey</c>. Each user's random 32-byte master key is loaded
/// once (and provisioned on first use if absent), stretched into subkeys via <see cref="UserCrypto"/>, and cached in
/// memory. Holds no per-call state and opens a fresh connection per key load, so it is registered as a singleton
/// (the singleton <c>JobRepository</c>/<c>JobQueue</c> depend on it). Keys are immutable — there is no rotation — so
/// the cache is never invalidated; any future rotation feature must change that.
/// </summary>
[AllowMagicStrings("SQL query text and its bound @parameter-name tokens")]
public sealed class UserCipher(IDbConnectionFactory connectionFactory) : IUserCipher
{
    private readonly IDbConnectionFactory _connectionFactory = connectionFactory;
    private readonly ConcurrentDictionary<long, UserCrypto.UserKeys> _cache = new();

    /// <inheritdoc />
    public Task EnsureKeyAsync(long userId, CancellationToken ct) => GetKeysAsync(userId, ct);

    public async Task<string> EncryptAsync(long userId, string plaintext, CancellationToken ct) =>
        UserCrypto.EncryptRandomized(await GetKeysAsync(userId, ct), plaintext);

    public async Task<string?> EncryptNullableAsync(long userId, string? plaintext, CancellationToken ct) =>
        plaintext is null ? null : UserCrypto.EncryptRandomized(await GetKeysAsync(userId, ct), plaintext);

    public async Task<string> DecryptAsync(long userId, string stored, CancellationToken ct) =>
        UserCrypto.DecryptTolerant(await GetKeysAsync(userId, ct), stored);

    public async Task<string?> DecryptNullableAsync(long userId, string? stored, CancellationToken ct) =>
        stored is null ? null : UserCrypto.DecryptTolerant(await GetKeysAsync(userId, ct), stored);

    public async Task<string> DeterministicAsync(long userId, string token, CancellationToken ct) =>
        UserCrypto.EncryptDeterministic(await GetKeysAsync(userId, ct), token);

    public async Task<string> DecryptDeterministicAsync(long userId, string stored, CancellationToken ct) =>
        UserCrypto.DecryptTolerant(await GetKeysAsync(userId, ct), stored);

    private async Task<UserCrypto.UserKeys> GetKeysAsync(long userId, CancellationToken ct)
    {
        if (_cache.TryGetValue(userId, out var cached))
            return cached;
        var material = await LoadOrProvisionKeyAsync(userId, ct);
        return _cache.GetOrAdd(userId, UserCrypto.DeriveSubkeys(material));
    }

    private async Task<byte[]> LoadOrProvisionKeyAsync(long userId, CancellationToken ct)
    {
        await using var conn = await _connectionFactory.OpenAsync(ct);

        var existing = await SelectKeyAsync(conn, userId, ct);
        if (existing is not null)
            return existing;

        // Provision a fresh key, race-safely: only the first writer for this user inserts; everyone re-reads the
        // committed row afterwards, so there is never more than one key per user even under concurrent first use.
        await using (var insert = conn.Command(
            @"INSERT INTO dbo.UserEncryptionKey (UserId, KeyMaterial, CreatedAtUtc)
              SELECT @id, @key, @created
              WHERE NOT EXISTS (SELECT 1 FROM dbo.UserEncryptionKey WHERE UserId = @id);"))
        {
            insert.AddParam("@id", userId);
            insert.AddParam("@key", RandomNumberGenerator.GetBytes(32));
            insert.AddParam("@created", DateTime.UtcNow);
            await insert.ExecuteNonQueryAsync(ct);
        }

        return await SelectKeyAsync(conn, userId, ct)
            ?? throw new InvalidOperationException($"Failed to provision encryption key for user {userId}.");
    }

    private static async Task<byte[]?> SelectKeyAsync(DbConnection conn, long userId, CancellationToken ct)
    {
        await using var cmd = conn.Command(
            "SELECT KeyMaterial FROM dbo.UserEncryptionKey WHERE UserId = @id;");
        cmd.AddParam("@id", userId);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : (byte[])result;
    }
}
