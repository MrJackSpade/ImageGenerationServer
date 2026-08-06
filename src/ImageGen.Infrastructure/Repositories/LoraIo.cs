using ImageGen.Application.Security;
using ImageGen.Domain.CodeAnalysis;
using ImageGen.Domain.Entities;
using ImageGen.Infrastructure.Database;
using System.Data.Common;

namespace ImageGen.Infrastructure.Repositories;

/// <summary>
/// Read/write of the <c>HistoryLora</c> child table (parent id, Name, Weight) — the parallel to <see cref="MarkIo"/>
/// for the LoRA stack an image was generated with. The table carries no UserId of its own, so the owning
/// <paramref name="userId"/> is threaded in from the parent to deterministically encrypt the searchable Name column
/// (so "which images used LoRA X" can compare ciphertext, as the mark/artist filters do). Weight is stored plaintext.
/// </summary>
[AllowMagicStrings("SQL query text and its bound @parameter-name tokens")]
internal static class LoraIo
{
    public static async Task InsertAsync(
        DbConnection conn, DbTransaction tx, string table, string parentColumn, long parentId,
        IReadOnlyList<HistoryLora> loras, long userId, IUserCipher cipher, CancellationToken ct)
    {
        if (loras.Count == 0)
        {
            return;
        }

        string sql = $"INSERT INTO {table} ({parentColumn}, Name, Weight) VALUES (@parent, @name, @weight);";
        foreach (HistoryLora lora in loras)
        {
            await using DbCommand cmd = conn.Command(sql, tx);
            _ = cmd.AddParam("@parent", parentId);
            _ = cmd.AddParam("@name", await cipher.DeterministicAsync(userId, lora.Name, ct));
            _ = cmd.AddParam("@weight", lora.Weight);
            _ = await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>Load the LoRA stack for a set of parent ids, grouped by parent id (apply order preserved by Id), decrypting each Name.</summary>
    public static async Task<Dictionary<long, List<HistoryLora>>> LoadAsync(
        DbConnection conn, string table, string parentColumn, IReadOnlyList<long> parentIds,
        long userId, IUserCipher cipher, CancellationToken ct)
    {
        Dictionary<long, List<HistoryLora>> byParent = [];
        if (parentIds.Count == 0)
        {
            return byParent;
        }

        string[] names = new string[parentIds.Count];
        for (int i = 0; i < parentIds.Count; i++)
        {
            names[i] = "@p" + i;
        }

        string sql = $"SELECT {parentColumn}, Name, Weight FROM {table} "
                + $"WHERE {parentColumn} IN ({string.Join(',', names)}) ORDER BY Id;";

        // Read raw first, then decrypt: keeps the forward-only reader closed before the cipher touches its own
        // connection (only on a cold key-cache miss). Same shape as MarkIo.LoadAsync.
        List<LoraRow> raw = [];
        await using (DbCommand cmd = conn.Command(sql))
        {
            for (int i = 0; i < parentIds.Count; i++)
            {
                _ = cmd.AddParam(names[i], parentIds[i]);
            }

            await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                raw.Add(new LoraRow(reader.GetInt64(0), reader.GetString(1), reader.AsDouble(2)));
            }
        }

        foreach (LoraRow row in raw)
        {
            HistoryLora lora = new(await cipher.DecryptDeterministicAsync(userId, row.Name, ct), row.Weight);
            if (!byParent.TryGetValue(row.ParentId, out List<HistoryLora>? list))
            {
                byParent[row.ParentId] = list = [];
            }

            list.Add(lora);
        }

        return byParent;
    }

    /// <summary>A raw LoRA row buffered with its still-encrypted name before deferred decryption.</summary>
    private readonly record struct LoraRow(long ParentId, string Name, double Weight);
}
