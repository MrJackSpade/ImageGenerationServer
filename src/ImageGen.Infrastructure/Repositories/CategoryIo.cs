using ImageGen.Application.Security;
using ImageGen.Domain.CodeAnalysis;
using ImageGen.Infrastructure.Database;
using System.Data.Common;

namespace ImageGen.Infrastructure.Repositories;

/// <summary>
/// Shared read/replace of the parallel bookmark-category child tables (TokenBookmarkCategory,
/// ImageBookmarkCategory), which have the same shape (parent id, Category) and differ only by table and
/// parent-column name. Like <see cref="MarkIo"/>, these tables carry no UserId of their own, so the owning
/// <paramref name="userId"/> is threaded in from the parent (TokenBookmark/ImageBookmark) to deterministically
/// encrypt the searchable Category column. Category names keep the user's display casing; membership sets are
/// deduplicated case-insensitively.
/// </summary>
[AllowMagicStrings("SQL query text and its bound @parameter-name tokens")]
internal static class CategoryIo
{
    /// <summary>Load categories for a set of parent ids, grouped by parent id, decrypting each name.</summary>
    public static async Task<Dictionary<long, List<string>>> LoadAsync(
        DbConnection conn, string table, string parentColumn, IReadOnlyList<long> parentIds,
        long userId, IUserCipher cipher, CancellationToken ct)
    {
        Dictionary<long, List<string>> byParent = new Dictionary<long, List<string>>();
        if (parentIds.Count == 0)
            return byParent;

        string[] names = new string[parentIds.Count];
        for (int i = 0; i < parentIds.Count; i++)
            names[i] = "@p" + i;

        string sql = $"SELECT {parentColumn}, Category FROM {table} WHERE {parentColumn} IN ({string.Join(',', names)});";

        // Read raw first, then decrypt: keeps the forward-only reader on this connection closed before the cipher
        // touches its own connection (only on a cold key-cache miss).
        List<(long ParentId, string Category)> raw = new List<(long ParentId, string Category)>();
        await using (DbCommand cmd = conn.Command(sql))
        {
            for (int i = 0; i < parentIds.Count; i++)
                cmd.AddParam(names[i], parentIds[i]);
            await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                raw.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        foreach ((long parentId, string? category) in raw)
        {
            string name = await cipher.DecryptDeterministicAsync(userId, category, ct);
            if (!byParent.TryGetValue(parentId, out List<string>? list))
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
        await using (DbCommand del = conn.Command($"DELETE FROM {table} WHERE {parentColumn} = @parent;", tx))
        {
            del.AddParam("@parent", parentId);
            await del.ExecuteNonQueryAsync(ct);
        }

        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string sql = $"INSERT INTO {table} ({parentColumn}, Category) VALUES (@parent, @category);";
        foreach (string raw in categories)
        {
            string? name = raw?.Trim();
            if (string.IsNullOrEmpty(name) || !seen.Add(name))
                continue;
            await using DbCommand cmd = conn.Command(sql, tx);
            cmd.AddParam("@parent", parentId);
            cmd.AddParam("@category", await cipher.DeterministicAsync(userId, name, ct));
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
