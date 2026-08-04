using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;
using ImageGen.Infrastructure.Database;

namespace ImageGen.Infrastructure.Repositories;

/// <summary>
/// The machine-level cache of LoRA preview media (dbo.LoraPreview). Stores the bytes CivitAI's CDN returned for a
/// file so the browser is served them from here instead of hotlinking CivitAI. Stateless (a fresh connection per
/// call), so — like <see cref="ImageBlobRepository"/> — it is a singleton the background populator resolves directly.
/// LoraName is the plain filename, a shared machine asset, so nothing here is encrypted.
/// </summary>
public sealed class LoraPreviewRepository(IDbConnectionFactory connectionFactory) : ILoraPreviewRepository
{
    private readonly IDbConnectionFactory _connectionFactory = connectionFactory;

    public async Task<LoraPreviewBlob?> GetAsync(string loraName, CancellationToken ct)
    {
        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var cmd = conn.Command("SELECT Bytes, ContentType FROM dbo.LoraPreview WHERE LoraName = @name;");
        cmd.AddParam("@name", loraName);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return new LoraPreviewBlob(reader.GetFieldValue<byte[]>(0), reader.GetString(1));
    }

    public async Task<IReadOnlyDictionary<string, string>> GetContentTypesAsync(IReadOnlyCollection<string> loraNames, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (loraNames.Count == 0) return result;

        var names = loraNames.ToList();
        var ps = new string[names.Count];
        for (var i = 0; i < names.Count; i++) ps[i] = "@n" + i;

        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var cmd = conn.Command($"SELECT LoraName, ContentType FROM dbo.LoraPreview WHERE LoraName IN ({string.Join(',', ps)});");
        for (var i = 0; i < names.Count; i++) cmd.AddParam(ps[i], names[i]);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    public async Task UpsertAsync(string loraName, byte[] bytes, string contentType, DateTime nowUtc, CancellationToken ct)
    {
        await using var conn = await _connectionFactory.OpenAsync(ct);

        int updated;
        await using (var cmd = conn.Command(
            "UPDATE dbo.LoraPreview SET Bytes = @bytes, ContentType = @ct, FetchedAtUtc = @at WHERE LoraName = @name;"))
        {
            cmd.AddParam("@name", loraName);
            cmd.AddLargeParam("@bytes", bytes);
            cmd.AddParam("@ct", contentType);
            cmd.AddParam("@at", nowUtc);
            updated = await cmd.ExecuteNonQueryAsync(ct);
        }
        if (updated == 0)
        {
            await using var cmd = conn.Command(
                "INSERT INTO dbo.LoraPreview (LoraName, Bytes, ContentType, FetchedAtUtc) VALUES (@name, @bytes, @ct, @at);");
            cmd.AddParam("@name", loraName);
            cmd.AddLargeParam("@bytes", bytes);
            cmd.AddParam("@ct", contentType);
            cmd.AddParam("@at", nowUtc);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task DeleteAsync(IReadOnlyCollection<string> loraNames, CancellationToken ct)
    {
        if (loraNames.Count == 0) return;
        var names = loraNames.ToList();
        var ps = new string[names.Count];
        for (var i = 0; i < names.Count; i++) ps[i] = "@n" + i;

        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var cmd = conn.Command($"DELETE FROM dbo.LoraPreview WHERE LoraName IN ({string.Join(',', ps)});");
        for (var i = 0; i < names.Count; i++) cmd.AddParam(ps[i], names[i]);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
