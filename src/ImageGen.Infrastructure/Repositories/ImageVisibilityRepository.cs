using ImageGen.Domain.CodeAnalysis;
using ImageGen.Domain.Repositories;
using ImageGen.Infrastructure.Database;
using System.Data.Common;

namespace ImageGen.Infrastructure.Repositories;

/// <summary>
/// <see cref="IImageVisibilityRepository"/> over the two tables that record who an image belongs to —
/// <c>dbo.HistoryEntry</c> and <c>dbo.JobSlot</c> joined to <c>dbo.Job</c>. Both queries are a membership test only:
/// nothing about the image is read, so an id the caller has no claim on discloses nothing but the refusal.
/// </summary>
[AllowMagicStrings("SQL query text and its bound @parameter-name tokens")]
public sealed class ImageVisibilityRepository(IDbConnectionFactory connectionFactory) : IImageVisibilityRepository
{
    /// <summary>Ids per statement. Each id binds one parameter, reused by both halves of the UNION, so a chunk stays
    /// well inside SQL Server's 2100-parameter ceiling however many cards a page asks about.</summary>
    private const int ChunkSize = 1000;

    private readonly IDbConnectionFactory _connectionFactory = connectionFactory;

    public async Task<bool> IsReadableAsync(long userId, string imageId, CancellationToken ct)
    {
        const string sql = @"
SELECT 1 FROM dbo.HistoryEntry WHERE UserId = @userId AND GatewayImageId = @img
UNION ALL
SELECT 1 FROM dbo.JobSlot s JOIN dbo.Job j ON j.JobId = s.JobId WHERE j.UserId = @userId AND s.ImageId = @img;";

        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command(sql);
        _ = cmd.AddParam("@userId", userId);
        _ = cmd.AddParam("@img", imageId);

        await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct);
    }

    public async Task<IReadOnlySet<string>> ReadableAsync(
        long userId, IReadOnlyCollection<string> imageIds, CancellationToken ct)
    {
        HashSet<string> readable = new(StringComparer.Ordinal);
        List<string> ids = [.. imageIds.Distinct(StringComparer.Ordinal)];
        if (ids.Count == 0)
        {
            return readable;
        }

        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        for (int start = 0; start < ids.Count; start += ChunkSize)
        {
            List<string> chunk = ids.GetRange(start, Math.Min(ChunkSize, ids.Count - start));
            string[] ps = new string[chunk.Count];
            for (int i = 0; i < chunk.Count; i++)
            {
                ps[i] = "@i" + i;
            }

            string list = string.Join(',', ps);
            await using DbCommand cmd = conn.Command(
                $"SELECT GatewayImageId FROM dbo.HistoryEntry WHERE UserId = @userId AND GatewayImageId IN ({list}) " +
                "UNION " +
                "SELECT s.ImageId FROM dbo.JobSlot s JOIN dbo.Job j ON j.JobId = s.JobId " +
                $"WHERE j.UserId = @userId AND s.ImageId IN ({list});");
            _ = cmd.AddParam("@userId", userId);
            for (int i = 0; i < chunk.Count; i++)
            {
                _ = cmd.AddParam(ps[i], chunk[i]);
            }

            await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                _ = readable.Add(reader.GetString(0));
            }
        }

        return readable;
    }
}
