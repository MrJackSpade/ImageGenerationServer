using ImageGen.Domain;
using ImageGen.Domain.CodeAnalysis;
using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;
using ImageGen.Infrastructure.Database;
using System.Data.Common;

namespace ImageGen.Infrastructure.Repositories;

/// <summary>
/// Stores image bytes in dbo.ImageBlob, keyed by a minted GUID, and serves them back. Stateless (a fresh
/// connection per call), so it's registered as a singleton — the singleton JobQueue resolves it from the root
/// provider to persist each generated image.
/// </summary>
[AllowMagicStrings("SQL query text and its bound @parameter-name tokens")]
public sealed class ImageBlobRepository(IDbConnectionFactory connectionFactory) : IImageBlobRepository
{
    private readonly IDbConnectionFactory _connectionFactory = connectionFactory;

    public async Task<string> AddAsync(NewImageBlob blob, CancellationToken ct)
    {
        string id = Guid.NewGuid().ToString(GuidFormats.NoDashes);   // globally unique; never derived from a ComfyUI filename
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command(@"
INSERT INTO dbo.ImageBlob (ImageId, Bytes, ContentType, Width, Height, ByteSize, Kind)
VALUES (@id, @bytes, @ct, @w, @h, @size, @kind);");
        _ = cmd.AddParam("@id", id);
        _ = cmd.AddLargeParam("@bytes", blob.Bytes);
        _ = cmd.AddParam("@ct", blob.ContentType);
        _ = cmd.AddParam("@w", (object?)blob.Width ?? DBNull.Value);
        _ = cmd.AddParam("@h", (object?)blob.Height ?? DBNull.Value);
        _ = cmd.AddParam("@size", blob.Bytes.Length);
        _ = cmd.AddParam("@kind", (byte)blob.Kind);
        _ = await cmd.ExecuteNonQueryAsync(ct);
        return id;
    }

    public async Task<ImageBlob?> GetAsync(string imageId, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command(
            "SELECT ImageId, Bytes, ContentType, Width, Height, ByteSize, Kind, CreatedAtUtc, PaletteJson FROM dbo.ImageBlob WHERE ImageId = @id;");
        _ = cmd.AddParam("@id", imageId);
        await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new ImageBlob
        {
            ImageId = reader.GetString(0),
            Bytes = reader.GetFieldValue<byte[]>(1),
            ContentType = reader.GetString(2),
            Width = reader.AsNullableInt32(3),
            Height = reader.AsNullableInt32(4),
            ByteSize = reader.AsInt32(5),
            Kind = (ImageBlobKind)reader.AsByte(6),
            CreatedAtUtc = DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Utc),
            PaletteJson = reader.IsDBNull(8) ? null : reader.GetString(8),
        };
    }

    public async Task SetPaletteAsync(string imageId, string paletteJson, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command("UPDATE dbo.ImageBlob SET PaletteJson = @p WHERE ImageId = @id;");
        _ = cmd.AddParam("@id", imageId);
        _ = cmd.AddLargeParam("@p", paletteJson);
        _ = await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetPaletteAsync(string imageId, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command("SELECT PaletteJson FROM dbo.ImageBlob WHERE ImageId = @id;");
        _ = cmd.AddParam("@id", imageId);
        object? v = await cmd.ExecuteScalarAsync(ct);
        return v is null or DBNull ? null : (string)v;
    }

    public async Task SetFrequenciesAsync(string imageId, string frequenciesJson, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command("UPDATE dbo.ImageBlob SET FrequenciesJson = @f WHERE ImageId = @id;");
        _ = cmd.AddParam("@id", imageId);
        _ = cmd.AddLargeParam("@f", frequenciesJson);
        _ = await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetFrequenciesAsync(string imageId, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command("SELECT FrequenciesJson FROM dbo.ImageBlob WHERE ImageId = @id;");
        _ = cmd.AddParam("@id", imageId);
        object? v = await cmd.ExecuteScalarAsync(ct);
        return v is null or DBNull ? null : (string)v;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetContentTypesAsync(IReadOnlyCollection<string> imageIds, CancellationToken ct)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        if (imageIds.Count == 0)
        {
            return result;
        }

        List<string> ids = [.. imageIds];
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);

        // Chunked so the id list can be any length: the caller asks about every image on the page at once, and one
        // IN (...) with a parameter per id would blow SQL Server's 2100-parameter ceiling on a large page. One
        // connection, reused per chunk.
        const int chunkSize = 1000;
        for (int start = 0; start < ids.Count; start += chunkSize)
        {
            List<string> chunk = ids.GetRange(start, Math.Min(chunkSize, ids.Count - start));
            string[] ps = new string[chunk.Count];
            for (int i = 0; i < chunk.Count; i++)
            {
                ps[i] = "@i" + i;
            }

            await using DbCommand cmd = conn.Command(
                $"SELECT ImageId, ContentType FROM dbo.ImageBlob WHERE ImageId IN ({string.Join(',', ps)});");
            for (int i = 0; i < chunk.Count; i++)
            {
                _ = cmd.AddParam(ps[i], chunk[i]);
            }

            await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                result[reader.GetString(0)] = reader.GetString(1);
            }
        }

        return result;
    }
}