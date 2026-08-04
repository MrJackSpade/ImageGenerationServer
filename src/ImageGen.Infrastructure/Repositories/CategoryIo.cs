//TODO: CHECK FOR FALLBACKS
using System.Data.Common;
using ImageGen.Infrastructure.Database;
using ImageGen.Application.Security;
using Microsoft.Data.SqlClient;

namespace ImageGen.Infrastructure.Repositories;

/// <summary>
/// Shared read/replace of the parallel bookmark-category child tables (TokenBookmarkCategory,
/// ImageBookmarkCategory), which have the same shape (parent id, Category) and differ only by table and
/// parent-column name. Like <see cref="MarkIo"/>, these tables carry no UserId of their own, so the owning
/// <paramref name="userId"/> is threaded in from the parent (TokenBookmark/ImageBookmark) to deterministically
/// encrypt the searchable Category column. Category names keep the user's display casing; membership sets are
/// deduplicated case-insensitively.
/// </summary>
internal static class CategoryIo
{
    /// <summary>Load categories for a set of parent ids, grouped by parent id, decrypting each name.</summary>
    public static async Task<Dictionary<long, List<string>>> LoadAsync(
        DbConnection conn, string table, string parentColumn, IReadOnlyList<long> parentIds,
        long userId, IUserCipher cipher, CancellationToken ct)
    {
        var byParent = new Dictionary<long, List<string>>();
        if (parentIds.Count == 0)
            return byParent;

        var names = new string[parentIds.Count];
        for (var i = 0; i < parentIds.Count; i++)
            names[i] = "@p" + i;

        var sql = $"SELECT {parentColumn}, Category FROM {table} WHERE {parentColumn} IN ({string.Join(',', names)});";

        // Read raw first, then decrypt: keeps the forward-only reader on this connection closed before the cipher
        // touches its own connection (only on a cold key-cache miss).
        var raw = new List<(long ParentId, string Category)>();
        await using (var cmd = conn.Command(sql))
        {
            for (var i = 0; i < parentIds.Count; i++)
                cmd.AddParam(names[i], parentIds[i]);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                raw.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        foreach (var (parentId, category) in raw)
        {
            var name = await cipher.DecryptDeterministicAsync(userId, category, ct);
            if (!byParent.TryGetValue(parentId, out var list))
                byParent[parentId] = list = [];
            list.Add(name);
        }

        return byParent;
    }

    /// <summary>Replace a parent's whole category set with <paramref name="categories"/> (blanks/dupes dropped).</summary>
    public static async Task ReplaceAsync(
        DbConnection conn, DbTransaction tx, string table, string parentColumn, long parentId,
        IReadOnlyList<string> categories, long userId, IUserCipher cipher, CancellationToken ct)
    {
        await using (var del = conn.Command($"DELETE FROM {table} WHERE {parentColumn} = @parent;", tx))
        {
            del.AddParam("@parent", parentId);
            await del.ExecuteNonQueryAsync(ct);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sql = $"INSERT INTO {table} ({parentColumn}, Category) VALUES (@parent, @category);";
        foreach (var raw in categories)
        {
            var name = raw?.Trim();
            if (string.IsNullOrEmpty(name) || !seen.Add(name))
                continue;
            await using var cmd = conn.Command(sql, tx);
            cmd.AddParam("@parent", parentId);
            cmd.AddParam("@category", await cipher.DeterministicAsync(userId, name, ct));
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
