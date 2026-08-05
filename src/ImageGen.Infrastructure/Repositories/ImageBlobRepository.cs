using System.Data.Common;
using ImageGen.Domain.CodeAnalysis;
using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;
using ImageGen.Infrastructure.Database;
using Microsoft.Data.SqlClient;

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

    /// <summary>Guid.ToString format: 32 hex digits, no hyphens.</summary>
    private const string GuidFormat = "N";

    public async Task<string> AddAsync(NewImageBlob blob, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString(GuidFormat);   // globally unique; never derived from a ComfyUI filename
        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var cmd = conn.Command(@"
INSERT INTO dbo.ImageBlob (ImageId, Bytes, ContentType, Width, Height, ByteSize, Kind)
VALUES (@id, @bytes, @ct, @w, @h, @size, @kind);");
        cmd.AddParam("@id", id);
        cmd.AddLargeParam("@bytes", blob.Bytes);
        cmd.AddParam("@ct", blob.ContentType);
        cmd.AddParam("@w", (object?)blob.Width ?? DBNull.Value);
        cmd.AddParam("@h", (object?)blob.Height ?? DBNull.Value);
        cmd.AddParam("@size", blob.Bytes.Length);
        cmd.AddParam("@kind", (byte)blob.Kind);
        await cmd.ExecuteNonQueryAsync(ct);
        return id;
    }

    public async Task<ImageBlob?> GetAsync(string imageId, CancellationToken ct)
    {
        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var cmd = conn.Command(
            "SELECT ImageId, Bytes, ContentType, Width, Height, ByteSize, Kind, CreatedAtUtc, PaletteJson FROM dbo.ImageBlob WHERE ImageId = @id;");
        cmd.AddParam("@id", imageId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
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
        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var cmd = conn.Command("UPDATE dbo.ImageBlob SET PaletteJson = @p WHERE ImageId = @id;");
        cmd.AddParam("@id", imageId);
        cmd.AddLargeParam("@p", paletteJson);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetPaletteAsync(string imageId, CancellationToken ct)
    {
        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var cmd = conn.Command("SELECT PaletteJson FROM dbo.ImageBlob WHERE ImageId = @id;");
        cmd.AddParam("@id", imageId);
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null or DBNull ? null : (string)v;
    }

    public async Task SetFrequenciesAsync(string imageId, string frequenciesJson, CancellationToken ct)
    {
        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var cmd = conn.Command("UPDATE dbo.ImageBlob SET FrequenciesJson = @f WHERE ImageId = @id;");
        cmd.AddParam("@id", imageId);
        cmd.AddLargeParam("@f", frequenciesJson);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetFrequenciesAsync(string imageId, CancellationToken ct)
    {
        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var cmd = conn.Command("SELECT FrequenciesJson FROM dbo.ImageBlob WHERE ImageId = @id;");
        cmd.AddParam("@id", imageId);
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null or DBNull ? null : (string)v;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetContentTypesAsync(IReadOnlyCollection<string> imageIds, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (imageIds.Count == 0)
            return result;

        var ids = imageIds.ToList();
        await using var conn = await _connectionFactory.OpenAsync(ct);

        // Chunked so the id list can be any length: the caller asks about every image on the page at once, and one
        // IN (...) with a parameter per id would blow SQL Server's 2100-parameter ceiling on a large page. One
        // connection, reused per chunk.
        const int chunkSize = 1000;
        for (var start = 0; start < ids.Count; start += chunkSize)
        {
            var chunk = ids.GetRange(start, Math.Min(chunkSize, ids.Count - start));
            var ps = new string[chunk.Count];
            for (var i = 0; i < chunk.Count; i++)
                ps[i] = "@i" + i;

            await using var cmd = conn.Command(
                $"SELECT ImageId, ContentType FROM dbo.ImageBlob WHERE ImageId IN ({string.Join(',', ps)});");
            for (var i = 0; i < chunk.Count; i++)
                cmd.AddParam(ps[i], chunk[i]);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result[reader.GetString(0)] = reader.GetString(1);
        }
        return result;
    }
}
