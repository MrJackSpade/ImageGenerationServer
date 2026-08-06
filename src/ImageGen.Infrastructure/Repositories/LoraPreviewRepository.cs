using ImageGen.Domain.CodeAnalysis;
using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;
using ImageGen.Infrastructure.Database;
using System.Data.Common;

namespace ImageGen.Infrastructure.Repositories;

/// <summary>
/// The machine-level cache of LoRA preview media (dbo.LoraPreview). Stores the bytes CivitAI's CDN returned for a
/// file so the browser is served them from here instead of hotlinking CivitAI. Stateless (a fresh connection per
/// call), so — like <see cref="ImageBlobRepository"/> — it is a singleton the background populator resolves directly.
/// LoraName is the plain filename, a shared machine asset, so nothing here is encrypted.
/// </summary>
[AllowMagicStrings("SQL query text and its bound @parameter-name tokens")]
public sealed class LoraPreviewRepository(IDbConnectionFactory connectionFactory) : ILoraPreviewRepository
{
    private readonly IDbConnectionFactory _connectionFactory = connectionFactory;

    public async Task<LoraPreviewBlob?> GetAsync(string loraName, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command("SELECT Bytes, ContentType FROM dbo.LoraPreview WHERE LoraName = @name;");
        _ = cmd.AddParam("@name", loraName);
        await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new LoraPreviewBlob(reader.GetFieldValue<byte[]>(0), reader.GetString(1));
    }

    public async Task<IReadOnlyDictionary<string, string>> GetContentTypesAsync(IReadOnlyCollection<string> loraNames, CancellationToken ct)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        if (loraNames.Count == 0)
        {
            return result;
        }

        List<string> names = loraNames.ToList();
        string[] ps = new string[names.Count];
        for (int i = 0; i < names.Count; i++)
        {
            ps[i] = "@n" + i;
        }

        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command($"SELECT LoraName, ContentType FROM dbo.LoraPreview WHERE LoraName IN ({string.Join(',', ps)});");
        for (int i = 0; i < names.Count; i++)
        {
            _ = cmd.AddParam(ps[i], names[i]);
        }

        await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }

        return result;
    }

    public async Task UpsertAsync(string loraName, byte[] bytes, string contentType, DateTime nowUtc, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);

        int updated;
        await using (DbCommand cmd = conn.Command(
            "UPDATE dbo.LoraPreview SET Bytes = @bytes, ContentType = @ct, FetchedAtUtc = @at WHERE LoraName = @name;"))
        {
            _ = cmd.AddParam("@name", loraName);
            _ = cmd.AddLargeParam("@bytes", bytes);
            _ = cmd.AddParam("@ct", contentType);
            _ = cmd.AddParam("@at", nowUtc);
            updated = await cmd.ExecuteNonQueryAsync(ct);
        }

        if (updated == 0)
        {
            await using DbCommand cmd = conn.Command(
                "INSERT INTO dbo.LoraPreview (LoraName, Bytes, ContentType, FetchedAtUtc) VALUES (@name, @bytes, @ct, @at);");
            _ = cmd.AddParam("@name", loraName);
            _ = cmd.AddLargeParam("@bytes", bytes);
            _ = cmd.AddParam("@ct", contentType);
            _ = cmd.AddParam("@at", nowUtc);
            _ = await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task DeleteAsync(IReadOnlyCollection<string> loraNames, CancellationToken ct)
    {
        if (loraNames.Count == 0)
        {
            return;
        }

        List<string> names = loraNames.ToList();
        string[] ps = new string[names.Count];
        for (int i = 0; i < names.Count; i++)
        {
            ps[i] = "@n" + i;
        }

        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command($"DELETE FROM dbo.LoraPreview WHERE LoraName IN ({string.Join(',', ps)});");
        for (int i = 0; i < names.Count; i++)
        {
            _ = cmd.AddParam(ps[i], names[i]);
        }

        _ = await cmd.ExecuteNonQueryAsync(ct);
    }
}
