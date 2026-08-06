using ImageGen.Application.Security;
using ImageGen.Domain.CodeAnalysis;
using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;
using ImageGen.Infrastructure.Database;
using System.Data.Common;

namespace ImageGen.Infrastructure.Repositories;

[AllowMagicStrings("SQL query text and its bound @parameter-name tokens")]
public sealed class ArtistDisplayRepository(IDbConnectionFactory connectionFactory, IUserCipher cipher) : IArtistDisplayRepository
{
    private static class Sql
    {
        public const string Columns = "Id, UserId, ArtistName, GatewayImageId, SetAtUtc";
    }

    private readonly IDbConnectionFactory _connectionFactory = connectionFactory;
    private readonly IUserCipher _cipher = cipher;

    public async Task<ArtistDisplay?> GetAsync(long userId, string artistName, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command(
            $"SELECT {Sql.Columns} FROM dbo.ArtistDisplay WHERE UserId = @userId AND ArtistName = @name;");
        _ = cmd.AddParam("@userId", userId);
        _ = cmd.AddParam("@name", await _cipher.DeterministicAsync(userId, artistName, ct));
        await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }
        // ArtistName is the deterministic ciphertext; hand back the plaintext the caller queried with.
        return new ArtistDisplay
        {
            Id = reader.GetInt64(0),
            UserId = reader.GetInt64(1),
            ArtistName = artistName,
            GatewayImageId = reader.GetString(3),
            SetAtUtc = DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc),
        };
    }

    public async Task<IReadOnlyDictionary<string, string>> GetManyAsync(
        long userId, IReadOnlyCollection<string> artistNames, CancellationToken ct)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        if (artistNames.Count == 0)
        {
            return result;
        }

        List<string> names = artistNames.ToList();
        string[] ps = new string[names.Count];
        for (int i = 0; i < names.Count; i++)
        {
            ps[i] = "@a" + i;
        }

        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command(
            $"SELECT ArtistName, GatewayImageId FROM dbo.ArtistDisplay WHERE UserId = @userId AND ArtistName IN ({string.Join(',', ps)});");
        _ = cmd.AddParam("@userId", userId);
        for (int i = 0; i < names.Count; i++)
        {
            _ = cmd.AddParam(ps[i], await _cipher.DeterministicAsync(userId, names[i], ct));
        }

        List<ArtistNameImageRow> raw = [];
        await using (DbDataReader reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                raw.Add(new ArtistNameImageRow(reader.GetString(0), reader.GetString(1)));
            }
        }

        foreach (ArtistNameImageRow row in raw)
        {
            result[await _cipher.DecryptDeterministicAsync(userId, row.Name, ct)] = row.ImageId;
        }

        return result;
    }

    /// <summary>A raw (still-encrypted artist name, image id) row buffered before deferred decryption.</summary>
    private readonly record struct ArtistNameImageRow(string Name, string ImageId);

    public async Task SetAsync(ArtistDisplay d, CancellationToken ct)
    {
        // Upsert: a user setting their own pick isn't concurrent, so update-then-insert is fine.
        //
        // The branch lives in C#, not SQL: SQLite has neither `IF` nor `@@ROWCOUNT` inside one batch, so the rowcount
        // comes back from ExecuteNonQuery and the branch is here. Same two statements, same order, one extra round
        // trip only on the first-ever set for an artist.
        string name = await _cipher.DeterministicAsync(d.UserId, d.ArtistName, ct);
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);

        int updated;
        await using (DbCommand cmd = conn.Command(
            "UPDATE dbo.ArtistDisplay SET GatewayImageId = @img, SetAtUtc = @at " +
            "WHERE UserId = @userId AND ArtistName = @name;"))
        {
            _ = cmd.AddParam("@userId", d.UserId);
            _ = cmd.AddParam("@name", name);
            _ = cmd.AddParam("@img", d.GatewayImageId);
            _ = cmd.AddParam("@at", d.SetAtUtc);
            updated = await cmd.ExecuteNonQueryAsync(ct);
        }

        if (updated == 0)
        {
            await using DbCommand cmd = conn.Command(
                "INSERT INTO dbo.ArtistDisplay (UserId, ArtistName, GatewayImageId, SetAtUtc) " +
                "VALUES (@userId, @name, @img, @at);");
            _ = cmd.AddParam("@userId", d.UserId);
            _ = cmd.AddParam("@name", name);
            _ = cmd.AddParam("@img", d.GatewayImageId);
            _ = cmd.AddParam("@at", d.SetAtUtc);
            _ = await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task DeleteAsync(long userId, string artistName, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command(
            "DELETE FROM dbo.ArtistDisplay WHERE UserId = @userId AND ArtistName = @name;");
        _ = cmd.AddParam("@userId", userId);
        _ = cmd.AddParam("@name", await _cipher.DeterministicAsync(userId, artistName, ct));
        _ = await cmd.ExecuteNonQueryAsync(ct);
    }
}
