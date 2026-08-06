using ImageGen.Domain.CodeAnalysis;
using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;
using ImageGen.Infrastructure.Database;
using System.Data.Common;
using System.Text.Json;

namespace ImageGen.Infrastructure.Repositories;

/// <summary>The machine-level CivitAI cache for LoRA files (dbo.LoraMeta). LoraName is the plain filename — a shared
/// machine asset, not per-user content — so nothing here is encrypted.</summary>
[AllowMagicStrings("SQL query text and its bound @parameter-name tokens")]
public sealed class LoraMetaRepository(IDbConnectionFactory connectionFactory) : ILoraMetaRepository
{
    private static class Sql
    {
        public const string Columns = "LoraName, Sha256, TrainedWords, ModelName, PreviewUrl, FetchedAtUtc";
    }

    private readonly IDbConnectionFactory _connectionFactory = connectionFactory;

    public async Task<IReadOnlyDictionary<string, LoraMeta>> GetManyAsync(IReadOnlyCollection<string> loraNames, CancellationToken ct)
    {
        Dictionary<string, LoraMeta> result = new(StringComparer.OrdinalIgnoreCase);
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
        await using DbCommand cmd = conn.Command($"SELECT {Sql.Columns} FROM dbo.LoraMeta WHERE LoraName IN ({string.Join(',', ps)});");
        for (int i = 0; i < names.Count; i++)
        {
            _ = cmd.AddParam(ps[i], names[i]);
        }

        await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string name = reader.GetString(0);
            List<string> words = reader.IsDBNull(2)
                ? []
                : JsonSerializer.Deserialize<List<string>>(reader.GetString(2))
                    ?? throw new InvalidOperationException(
                        $"LoraMeta '{name}' has TrainedWords stored as the JSON literal null; the column is only ever written a serialized array.");
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
        string words = JsonSerializer.Serialize(meta.TrainedWords);
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);

        int updated;
        await using (DbCommand cmd = conn.Command(
            "UPDATE dbo.LoraMeta SET Sha256 = @sha, TrainedWords = @words, ModelName = @model, PreviewUrl = @preview, FetchedAtUtc = @at WHERE LoraName = @name;"))
        {
            AddAll(cmd, meta, words);
            updated = await cmd.ExecuteNonQueryAsync(ct);
        }

        if (updated == 0)
        {
            await using DbCommand cmd = conn.Command(
                "INSERT INTO dbo.LoraMeta (LoraName, Sha256, TrainedWords, ModelName, PreviewUrl, FetchedAtUtc) VALUES (@name, @sha, @words, @model, @preview, @at);");
            AddAll(cmd, meta, words);
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
        await using DbCommand cmd = conn.Command($"DELETE FROM dbo.LoraMeta WHERE LoraName IN ({string.Join(',', ps)});");
        for (int i = 0; i < names.Count; i++)
        {
            _ = cmd.AddParam(ps[i], names[i]);
        }

        _ = await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void AddAll(System.Data.Common.DbCommand cmd, LoraMeta meta, string words)
    {
        _ = cmd.AddParam("@name", meta.LoraName);
        _ = cmd.AddParam("@sha", (object?)meta.Sha256 ?? DBNull.Value);
        _ = cmd.AddParam("@words", words);
        _ = cmd.AddParam("@model", (object?)meta.ModelName ?? DBNull.Value);
        _ = cmd.AddParam("@preview", (object?)meta.PreviewUrl ?? DBNull.Value);
        _ = cmd.AddParam("@at", meta.FetchedAtUtc);
    }
}
