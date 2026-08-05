using ImageGen.Domain.CodeAnalysis;
using ImageGen.Domain.Repositories;
using ImageGen.Infrastructure.Database;
using System.Data.Common;

namespace ImageGen.Infrastructure.Repositories;

/// <summary>
/// <see cref="IImageViewRepository"/> over <c>dbo.ImageView</c>.
/// <para>Image ids are stored PLAIN, unlike prompts and tokens. They are opaque blob handles that carry nothing about
/// the user — the same treatment <c>ImageBookmark.GatewayImageId</c> and <c>ArtistDisplay.GatewayImageId</c> already
/// get — and this table is looked up by id in batches, which deterministic ciphertext would make no safer and every
/// membership test slower.</para>
/// </summary>
[AllowMagicStrings("SQL query text and its bound @parameter-name tokens")]
public sealed class ImageViewRepository(IDbConnectionFactory connectionFactory) : IImageViewRepository
{
    private readonly IDbConnectionFactory _connectionFactory = connectionFactory;

    public async Task MarkViewedAsync(long userId, string gatewayImageId, DateTime nowUtc, CancellationToken ct)
    {
        // Idempotent by the primary key: opening an image a second time is not a new fact, and the FIRST view's
        // timestamp is the one worth keeping, so an existing row is left exactly as it is.
        const string sql = @"
INSERT INTO dbo.ImageView (UserId, GatewayImageId, ViewedAtUtc)
SELECT @userId, @img, @now
WHERE NOT EXISTS (SELECT 1 FROM dbo.ImageView WHERE UserId = @userId AND GatewayImageId = @img);";

        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command(sql);
        cmd.AddParam("@userId", userId);
        cmd.AddParam("@img", gatewayImageId);
        cmd.AddParam("@now", nowUtc);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlySet<string>> ViewedAsync(
        long userId, IReadOnlyCollection<string> gatewayImageIds, CancellationToken ct)
    {
        HashSet<string> viewed = new HashSet<string>(StringComparer.Ordinal);
        List<string> ids = gatewayImageIds.Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
            return viewed;

        string[] ps = new string[ids.Count];
        for (int i = 0; i < ids.Count; i++)
            ps[i] = "@i" + i;

        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command(
            $"SELECT GatewayImageId FROM dbo.ImageView WHERE UserId = @userId AND GatewayImageId IN ({string.Join(',', ps)});");
        cmd.AddParam("@userId", userId);
        for (int i = 0; i < ids.Count; i++)
            cmd.AddParam(ps[i], ids[i]);

        await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            viewed.Add(reader.GetString(0));
        return viewed;
    }

    public async Task<int> MarkAllViewedAsync(long userId, DateTime nowUtc, CancellationToken ct)
    {
        // Every image of THIS user's that has no view row yet. Scoped to their own history, so it can never mark an
        // image they don't own, and the NOT EXISTS keeps it idempotent and keeps first-view timestamps intact.
        const string sql = @"
INSERT INTO dbo.ImageView (UserId, GatewayImageId, ViewedAtUtc)
SELECT h.UserId, h.GatewayImageId, @now
FROM dbo.HistoryEntry h
WHERE h.UserId = @userId
  AND NOT EXISTS (SELECT 1 FROM dbo.ImageView v WHERE v.UserId = h.UserId AND v.GatewayImageId = h.GatewayImageId);";

        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command(sql);
        cmd.AddParam("@userId", userId);
        cmd.AddParam("@now", nowUtc);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
