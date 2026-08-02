using System.Text.Json;
using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;
using ImageGen.Infrastructure.Database;

namespace ImageGen.Infrastructure.Repositories;

/// <summary>The machine-level CivitAI cache for LoRA files (dbo.LoraMeta). LoraName is the plain filename — a shared
/// machine asset, not per-user content — so nothing here is encrypted.</summary>
public sealed class LoraMetaRepository(IDbConnectionFactory connectionFactory) : ILoraMetaRepository
{
    private const string Columns = "LoraName, Sha256, TrainedWords, ModelName, PreviewUrl, FetchedAtUtc";
    private readonly IDbConnectionFactory _connectionFactory = connectionFactory;

    public async Task<IReadOnlyDictionary<string, LoraMeta>> GetManyAsync(IReadOnlyCollection<string> loraNames, CancellationToken ct)
    {
        var result = new Dictionary<string, LoraMeta>(StringComparer.OrdinalIgnoreCase);
        if (loraNames.Count == 0) return result;

        var names = loraNames.ToList();
        var ps = new string[names.Count];
        for (var i = 0; i < names.Count; i++) ps[i] = "@n" + i;

        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var cmd = conn.Command($"SELECT {Columns} FROM dbo.LoraMeta WHERE LoraName IN ({string.Join(',', ps)});");
        for (var i = 0; i < names.Count; i++) cmd.AddParam(ps[i], names[i]);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            var words = reader.IsDBNull(2) ? [] : (JsonSerializer.Deserialize<List<string>>(reader.GetString(2)) ?? []);
            result[name] = new LoraMeta(
                name,
                reader.IsDBNull(1) ? null : reader.GetString(1),
                words,
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc));
        }
        return result;
    }

    public async Task UpsertAsync(LoraMeta meta, CancellationToken ct)
    {
        var words = JsonSerializer.Serialize(meta.TrainedWords);
        await using var conn = await _connectionFactory.OpenAsync(ct);

        int updated;
        await using (var cmd = conn.Command(
            "UPDATE dbo.LoraMeta SET Sha256 = @sha, TrainedWords = @words, ModelName = @model, PreviewUrl = @preview, FetchedAtUtc = @at WHERE LoraName = @name;"))
        {
            AddAll(cmd, meta, words);
            updated = await cmd.ExecuteNonQueryAsync(ct);
        }
        if (updated == 0)
        {
            await using var cmd = conn.Command(
                "INSERT INTO dbo.LoraMeta (LoraName, Sha256, TrainedWords, ModelName, PreviewUrl, FetchedAtUtc) VALUES (@name, @sha, @words, @model, @preview, @at);");
            AddAll(cmd, meta, words);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static void AddAll(System.Data.Common.DbCommand cmd, LoraMeta meta, string words)
    {
        cmd.AddParam("@name", meta.LoraName);
        cmd.AddParam("@sha", (object?)meta.Sha256 ?? DBNull.Value);
        cmd.AddParam("@words", words);
        cmd.AddParam("@model", (object?)meta.ModelName ?? DBNull.Value);
        cmd.AddParam("@preview", (object?)meta.PreviewUrl ?? DBNull.Value);
        cmd.AddParam("@at", meta.FetchedAtUtc);
    }
}
