using ImageGen.Application.Security;
using ImageGen.Domain;
using ImageGen.Domain.CodeAnalysis;
using ImageGen.Domain.Entities;
using ImageGen.Infrastructure.Database;
using System.Data.Common;

namespace ImageGen.Infrastructure.Repositories;

/// <summary>
/// Shared read/write of the parallel "mark" child tables (HistoryMark, ImageBookmarkMark), which
/// have the same shape (parent id, Token, Kind) and differ only by table and parent-column name.
/// The mark tables carry no UserId of their own, so the owning <paramref name="userId"/> is threaded in from the
/// parent (HistoryEntry/ImageBookmark) to deterministically encrypt the searchable Token column.
/// </summary>
[AllowMagicStrings("SQL query text and its bound @parameter-name tokens")]
internal static class MarkIo
{
    public static async Task InsertAsync(
        DbConnection conn, DbTransaction tx, string table, string parentColumn, long parentId,
        IReadOnlyList<Mark> marks, long userId, IUserCipher cipher, CancellationToken ct)
    {
        if (marks.Count == 0)
        {
            return;
        }

        string sql = $"INSERT INTO {table} ({parentColumn}, Token, Kind, Generated) VALUES (@parent, @token, @kind, @generated);";
        foreach (Mark mark in marks)
        {
            await using DbCommand cmd = conn.Command(sql, tx);
            _ = cmd.AddParam("@parent", parentId);
            _ = cmd.AddParam("@token", await cipher.DeterministicAsync(userId, mark.Token, ct));
            _ = cmd.AddParam("@kind", (byte)mark.Kind);
            _ = cmd.AddParam("@generated", mark.Generated ? 1 : 0);
            _ = await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>Load marks for a set of parent ids, grouped by parent id, decrypting each Token.</summary>
    public static async Task<Dictionary<long, List<Mark>>> LoadAsync(
        DbConnection conn, string table, string parentColumn, IReadOnlyList<long> parentIds,
        long userId, IUserCipher cipher, CancellationToken ct)
    {
        Dictionary<long, List<Mark>> byParent = [];
        if (parentIds.Count == 0)
        {
            return byParent;
        }

        string[] names = new string[parentIds.Count];
        for (int i = 0; i < parentIds.Count; i++)
        {
            names[i] = "@p" + i;
        }

        string sql = $"SELECT {parentColumn}, Token, Kind, Generated FROM {table} WHERE {parentColumn} IN ({string.Join(',', names)});";

        // Read raw first, then decrypt: keeps the forward-only reader on this connection closed before the cipher
        // touches its own connection (only on a cold key-cache miss).
        List<MarkRow> raw = [];
        await using (DbCommand cmd = conn.Command(sql))
        {
            for (int i = 0; i < parentIds.Count; i++)
            {
                _ = cmd.AddParam(names[i], parentIds[i]);
            }

            await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                raw.Add(new MarkRow(reader.GetInt64(0), reader.GetString(1), (TokenKind)reader.AsByte(2), reader.AsBool(3)));
            }
        }

        foreach (MarkRow row in raw)
        {
            Mark mark = new(await cipher.DecryptDeterministicAsync(userId, row.Token, ct), row.Kind, row.Generated);
            if (!byParent.TryGetValue(row.ParentId, out List<Mark>? list))
            {
                byParent[row.ParentId] = list = [];
            }

            list.Add(mark);
        }

        return byParent;
    }

    /// <summary>A raw mark row buffered with its still-encrypted token before deferred decryption.</summary>
    private readonly record struct MarkRow(long ParentId, string Token, TokenKind Kind, bool Generated);
}
