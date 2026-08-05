using ImageGen.Application.Security;
using ImageGen.Domain.CodeAnalysis;
using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;
using ImageGen.Infrastructure.Database;

namespace ImageGen.Infrastructure.Repositories;

/// <summary>Per-user tag portrait images (dbo.TagDisplay). Mirrors <see cref="ArtistDisplayRepository"/> /
/// <see cref="LoraDisplayRepository"/>: the searchable TagName column is deterministically encrypted.</summary>
[AllowMagicStrings("SQL query text and its bound @parameter-name tokens")]
public sealed class TagDisplayRepository(IDbConnectionFactory connectionFactory, IUserCipher cipher) : ITagDisplayRepository
{
    private const string Columns = "Id, UserId, TagName, GatewayImageId, SetAtUtc";

    private readonly IDbConnectionFactory _connectionFactory = connectionFactory;
    private readonly IUserCipher _cipher = cipher;

    public async Task<TagDisplay?> GetAsync(long userId, string tagName, CancellationToken ct)
    {
        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var cmd = conn.Command(
            $"SELECT {Columns} FROM dbo.TagDisplay WHERE UserId = @userId AND TagName = @name;");
        cmd.AddParam("@userId", userId);
        cmd.AddParam("@name", await _cipher.DeterministicAsync(userId, tagName, ct));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return new TagDisplay
        {
            Id = reader.GetInt64(0),
            UserId = reader.GetInt64(1),
            TagName = tagName,
            GatewayImageId = reader.GetString(3),
            SetAtUtc = DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc),
        };
    }

    public async Task<IReadOnlyDictionary<string, string>> GetManyAsync(
        long userId, IReadOnlyCollection<string> tagNames, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (tagNames.Count == 0)
            return result;

        var names = tagNames.ToList();
        var ps = new string[names.Count];
        for (var i = 0; i < names.Count; i++)
            ps[i] = "@a" + i;

        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var cmd = conn.Command(
            $"SELECT TagName, GatewayImageId FROM dbo.TagDisplay WHERE UserId = @userId AND TagName IN ({string.Join(',', ps)});");
        cmd.AddParam("@userId", userId);
        for (var i = 0; i < names.Count; i++)
            cmd.AddParam(ps[i], await _cipher.DeterministicAsync(userId, names[i], ct));

        var raw = new List<TagNameImageRow>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                raw.Add(new TagNameImageRow(reader.GetString(0), reader.GetString(1)));
        foreach (var row in raw)
            result[await _cipher.DecryptDeterministicAsync(userId, row.Name, ct)] = row.ImageId;
        return result;
    }

    private readonly record struct TagNameImageRow(string Name, string ImageId);

    public async Task SetAsync(TagDisplay d, CancellationToken ct)
    {
        var name = await _cipher.DeterministicAsync(d.UserId, d.TagName, ct);
        await using var conn = await _connectionFactory.OpenAsync(ct);

        int updated;
        await using (var cmd = conn.Command(
            "UPDATE dbo.TagDisplay SET GatewayImageId = @img, SetAtUtc = @at WHERE UserId = @userId AND TagName = @name;"))
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
                "INSERT INTO dbo.TagDisplay (UserId, TagName, GatewayImageId, SetAtUtc) VALUES (@userId, @name, @img, @at);");
            cmd.AddParam("@userId", d.UserId);
            cmd.AddParam("@name", name);
            cmd.AddParam("@img", d.GatewayImageId);
            cmd.AddParam("@at", d.SetAtUtc);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task DeleteAsync(long userId, string tagName, CancellationToken ct)
    {
        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var cmd = conn.Command(
            "DELETE FROM dbo.TagDisplay WHERE UserId = @userId AND TagName = @name;");
        cmd.AddParam("@userId", userId);
        cmd.AddParam("@name", await _cipher.DeterministicAsync(userId, tagName, ct));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
