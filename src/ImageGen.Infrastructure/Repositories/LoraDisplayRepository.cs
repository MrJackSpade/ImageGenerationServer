using ImageGen.Application.Security;
using ImageGen.Domain.CodeAnalysis;
using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;
using ImageGen.Infrastructure.Database;

namespace ImageGen.Infrastructure.Repositories;

/// <summary>Per-user LoRA cover images (dbo.LoraDisplay). Mirrors <see cref="ArtistDisplayRepository"/>: the searchable
/// LoraName column is deterministically encrypted, so equality and IN (...) still work over it.</summary>
[AllowMagicStrings("SQL query text and its bound @parameter-name tokens")]
public sealed class LoraDisplayRepository(IDbConnectionFactory connectionFactory, IUserCipher cipher) : ILoraDisplayRepository
{
    private const string Columns = "Id, UserId, LoraName, GatewayImageId, SetAtUtc";

    private readonly IDbConnectionFactory _connectionFactory = connectionFactory;
    private readonly IUserCipher _cipher = cipher;

    public async Task<LoraDisplay?> GetAsync(long userId, string loraName, CancellationToken ct)
    {
        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var cmd = conn.Command(
            $"SELECT {Columns} FROM dbo.LoraDisplay WHERE UserId = @userId AND LoraName = @name;");
        cmd.AddParam("@userId", userId);
        cmd.AddParam("@name", await _cipher.DeterministicAsync(userId, loraName, ct));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        // LoraName is the deterministic ciphertext; hand back the plaintext the caller queried with.
        return new LoraDisplay
        {
            Id = reader.GetInt64(0),
            UserId = reader.GetInt64(1),
            LoraName = loraName,
            GatewayImageId = reader.GetString(3),
            SetAtUtc = DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc),
        };
    }

    public async Task<IReadOnlyDictionary<string, string>> GetManyAsync(
        long userId, IReadOnlyCollection<string> loraNames, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (loraNames.Count == 0)
            return result;

        var names = loraNames.ToList();
        var ps = new string[names.Count];
        for (var i = 0; i < names.Count; i++)
            ps[i] = "@a" + i;

        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var cmd = conn.Command(
            $"SELECT LoraName, GatewayImageId FROM dbo.LoraDisplay WHERE UserId = @userId AND LoraName IN ({string.Join(',', ps)});");
        cmd.AddParam("@userId", userId);
        for (var i = 0; i < names.Count; i++)
            cmd.AddParam(ps[i], await _cipher.DeterministicAsync(userId, names[i], ct));

        var raw = new List<LoraNameImageRow>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                raw.Add(new LoraNameImageRow(reader.GetString(0), reader.GetString(1)));
        foreach (var row in raw)
            result[await _cipher.DecryptDeterministicAsync(userId, row.Name, ct)] = row.ImageId;
        return result;
    }

    /// <summary>A raw (still-encrypted LoRA name, image id) row buffered before deferred decryption.</summary>
    private readonly record struct LoraNameImageRow(string Name, string ImageId);

    public async Task SetAsync(LoraDisplay d, CancellationToken ct)
    {
        // Upsert: a user setting their own pick isn't concurrent, so update-then-insert is fine (same shape as
        // ArtistDisplayRepository — one extra round trip only on the first-ever set for a LoRA).
        var name = await _cipher.DeterministicAsync(d.UserId, d.LoraName, ct);
        await using var conn = await _connectionFactory.OpenAsync(ct);

        int updated;
        await using (var cmd = conn.Command(
            "UPDATE dbo.LoraDisplay SET GatewayImageId = @img, SetAtUtc = @at " +
            "WHERE UserId = @userId AND LoraName = @name;"))
        {
            cmd.AddParam("@userId", d.UserId);
            cmd.AddParam("@name", name);
            cmd.AddParam("@img", d.GatewayImageId);
            cmd.AddParam("@at", d.SetAtUtc);
            updated = await cmd.ExecuteNonQueryAsync(ct);
        }

        if (updated == 0)
        {
            await using var cmd = conn.Command(
                "INSERT INTO dbo.LoraDisplay (UserId, LoraName, GatewayImageId, SetAtUtc) " +
                "VALUES (@userId, @name, @img, @at);");
            cmd.AddParam("@userId", d.UserId);
            cmd.AddParam("@name", name);
            cmd.AddParam("@img", d.GatewayImageId);
            cmd.AddParam("@at", d.SetAtUtc);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task DeleteAsync(long userId, string loraName, CancellationToken ct)
    {
        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var cmd = conn.Command(
            "DELETE FROM dbo.LoraDisplay WHERE UserId = @userId AND LoraName = @name;");
        cmd.AddParam("@userId", userId);
        cmd.AddParam("@name", await _cipher.DeterministicAsync(userId, loraName, ct));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
