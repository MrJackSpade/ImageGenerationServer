using ImageGen.Domain.CodeAnalysis;
using ImageGen.Domain.Repositories;
using ImageGen.Infrastructure.Database;
using System.Data.Common;

namespace ImageGen.Infrastructure.Repositories;

/// <summary>
/// Stores the native-resolution lossless frames of a pixel-art clip in dbo.ImageFrame, keyed by the produced image
/// id. Stateless (a fresh connection per call), registered as a singleton like <see cref="ImageBlobRepository"/> so
/// the singleton JobQueue can persist frames from the root provider.
/// </summary>
[AllowMagicStrings("SQL query text and its bound @parameter-name tokens")]
public sealed class ImageFrameRepository(IDbConnectionFactory connectionFactory) : IImageFrameRepository
{
    private readonly IDbConnectionFactory _connectionFactory = connectionFactory;

    public async Task AddFramesAsync(string imageId, IReadOnlyList<byte[]> frames, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        // Replace any prior set (a re-store of the same id) so this is idempotent.
        await using (DbCommand del = conn.Command("DELETE FROM dbo.ImageFrame WHERE ImageId = @id;"))
        {
            _ = del.AddParam("@id", imageId);
            _ = await del.ExecuteNonQueryAsync(ct);
        }

        for (int i = 0; i < frames.Count; i++)
        {
            await using DbCommand cmd = conn.Command(
                "INSERT INTO dbo.ImageFrame (ImageId, FrameIndex, Bytes) VALUES (@id, @idx, @b);");
            _ = cmd.AddParam("@id", imageId);
            _ = cmd.AddParam("@idx", i);
            _ = cmd.AddLargeParam("@b", frames[i]);
            _ = await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<int> GetFrameCountAsync(string imageId, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command("SELECT COUNT(*) FROM dbo.ImageFrame WHERE ImageId = @id;");
        _ = cmd.AddParam("@id", imageId);
        return await cmd.ScalarInt32Async(ct);
    }

    public async Task<IReadOnlyList<byte[]>> GetFramesAsync(string imageId, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command(
            "SELECT Bytes FROM dbo.ImageFrame WHERE ImageId = @id ORDER BY FrameIndex;");
        _ = cmd.AddParam("@id", imageId);
        List<byte[]> frames = [];
        await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            frames.Add(reader.GetFieldValue<byte[]>(0));
        }

        return frames;
    }
}
