using ImageGen.Application.Security;
using ImageGen.Domain.CodeAnalysis;
using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;
using ImageGen.Infrastructure.Database;
using System.Data.Common;

namespace ImageGen.Infrastructure.Repositories;

/// <summary>Per-user LoRA preferences (dbo.LoraUserSetting). LoraName is deterministically encrypted, like
/// <see cref="LoraDisplayRepository"/>.</summary>
[AllowMagicStrings("SQL query text and its bound @parameter-name tokens")]
public sealed class LoraUserSettingRepository(IDbConnectionFactory connectionFactory, IUserCipher cipher) : ILoraUserSettingRepository
{
    private readonly IDbConnectionFactory _connectionFactory = connectionFactory;
    private readonly IUserCipher _cipher = cipher;

    public async Task<IReadOnlyDictionary<string, LoraUserSetting>> GetManyAsync(
        long userId, IReadOnlyCollection<string> loraNames, CancellationToken ct)
    {
        Dictionary<string, LoraUserSetting> result = new(StringComparer.OrdinalIgnoreCase);
        if (loraNames.Count == 0)
        {
            return result;
        }

        List<string> names = [.. loraNames];
        string[] ps = new string[names.Count];
        for (int i = 0; i < names.Count; i++)
        {
            ps[i] = "@a" + i;
        }

        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command(
            $"SELECT LoraName, TriggerWords, AutoAttach FROM dbo.LoraUserSetting WHERE UserId = @userId AND LoraName IN ({string.Join(',', ps)});");
        _ = cmd.AddParam("@userId", userId);
        // Deterministic encryption is stable, so map each ciphertext back to the plaintext we queried with — no decrypt round.
        Dictionary<string, string> byCipher = new(StringComparer.Ordinal);
        for (int i = 0; i < names.Count; i++)
        {
            string enc = await _cipher.DeterministicAsync(userId, names[i], ct);
            _ = cmd.AddParam(ps[i], enc);
            byCipher[enc] = names[i];
        }

        List<(string Enc, string? Tw, bool Aa)> raw = [];
        await using (DbDataReader reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                raw.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.AsBool(2)));
            }
        }

        foreach ((string? enc, string? tw, bool aa) in raw)
        {
            string plain = byCipher.TryGetValue(enc, out string? p) ? p : await _cipher.DecryptDeterministicAsync(userId, enc, ct);
            result[plain] = new LoraUserSetting { UserId = userId, LoraName = plain, TriggerWords = tw, AutoAttach = aa };
        }

        return result;
    }

    public async Task SetAsync(LoraUserSetting s, CancellationToken ct)
    {
        string name = await _cipher.DeterministicAsync(s.UserId, s.LoraName, ct);
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);

        int updated;
        await using (DbCommand cmd = conn.Command(
            "UPDATE dbo.LoraUserSetting SET TriggerWords = @tw, AutoAttach = @aa WHERE UserId = @userId AND LoraName = @name;"))
        {
            _ = cmd.AddParam("@userId", s.UserId);
            _ = cmd.AddParam("@name", name);
            _ = cmd.AddParam("@tw", (object?)s.TriggerWords ?? DBNull.Value);
            _ = cmd.AddParam("@aa", s.AutoAttach);
            updated = await cmd.ExecuteNonQueryAsync(ct);
        }

        if (updated == 0)
        {
            await using DbCommand cmd = conn.Command(
                "INSERT INTO dbo.LoraUserSetting (UserId, LoraName, TriggerWords, AutoAttach) VALUES (@userId, @name, @tw, @aa);");
            _ = cmd.AddParam("@userId", s.UserId);
            _ = cmd.AddParam("@name", name);
            _ = cmd.AddParam("@tw", (object?)s.TriggerWords ?? DBNull.Value);
            _ = cmd.AddParam("@aa", s.AutoAttach);
            _ = await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}