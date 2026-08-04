using ImageGen.Application.Security;
using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;
using ImageGen.Infrastructure.Database;

namespace ImageGen.Infrastructure.Repositories;

/// <summary>Per-user LoRA preferences (dbo.LoraUserSetting). LoraName is deterministically encrypted, like
/// <see cref="LoraDisplayRepository"/>.</summary>
public sealed class LoraUserSettingRepository(IDbConnectionFactory connectionFactory, IUserCipher cipher) : ILoraUserSettingRepository
{
    private readonly IDbConnectionFactory _connectionFactory = connectionFactory;
    private readonly IUserCipher _cipher = cipher;

    public async Task<IReadOnlyDictionary<string, LoraUserSetting>> GetManyAsync(
        long userId, IReadOnlyCollection<string> loraNames, CancellationToken ct)
    {
        var result = new Dictionary<string, LoraUserSetting>(StringComparer.OrdinalIgnoreCase);
        if (loraNames.Count == 0) return result;

        var names = loraNames.ToList();
        var ps = new string[names.Count];
        for (var i = 0; i < names.Count; i++) ps[i] = "@a" + i;

        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var cmd = conn.Command(
            $"SELECT LoraName, TriggerWords, AutoAttach FROM dbo.LoraUserSetting WHERE UserId = @userId AND LoraName IN ({string.Join(',', ps)});");
        cmd.AddParam("@userId", userId);
        // Deterministic encryption is stable, so map each ciphertext back to the plaintext we queried with — no decrypt round.
        var byCipher = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < names.Count; i++)
        {
            var enc = await _cipher.DeterministicAsync(userId, names[i], ct);
            cmd.AddParam(ps[i], enc);
            byCipher[enc] = names[i];
        }

        var raw = new List<(string Enc, string? Tw, bool Aa)>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                raw.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.AsBool(2)));

        foreach (var (enc, tw, aa) in raw)
        {
            var plain = byCipher.TryGetValue(enc, out var p) ? p : await _cipher.DecryptDeterministicAsync(userId, enc, ct);
            result[plain] = new LoraUserSetting { UserId = userId, LoraName = plain, TriggerWords = tw, AutoAttach = aa };
        }
        return result;
    }

    public async Task SetAsync(LoraUserSetting s, CancellationToken ct)
    {
        var name = await _cipher.DeterministicAsync(s.UserId, s.LoraName, ct);
        await using var conn = await _connectionFactory.OpenAsync(ct);

        int updated;
        await using (var cmd = conn.Command(
            "UPDATE dbo.LoraUserSetting SET TriggerWords = @tw, AutoAttach = @aa WHERE UserId = @userId AND LoraName = @name;"))
        {
            cmd.AddParam("@userId", s.UserId);
            cmd.AddParam("@name", name);
            cmd.AddParam("@tw", (object?)s.TriggerWords ?? DBNull.Value);
            cmd.AddParam("@aa", s.AutoAttach);
            updated = await cmd.ExecuteNonQueryAsync(ct);
        }
        if (updated == 0)
        {
            await using var cmd = conn.Command(
                "INSERT INTO dbo.LoraUserSetting (UserId, LoraName, TriggerWords, AutoAttach) VALUES (@userId, @name, @tw, @aa);");
            cmd.AddParam("@userId", s.UserId);
            cmd.AddParam("@name", name);
            cmd.AddParam("@tw", (object?)s.TriggerWords ?? DBNull.Value);
            cmd.AddParam("@aa", s.AutoAttach);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
