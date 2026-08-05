using System.Data.Common;
using ImageGen.Application.Security;
using ImageGen.Domain;
using ImageGen.Domain.CodeAnalysis;
using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;
using ImageGen.Infrastructure.Database;
using Microsoft.Data.SqlClient;

namespace ImageGen.Infrastructure.Repositories;

[AllowMagicStrings("SQL query text and its bound @parameter-name tokens")]
public sealed class BookmarkRepository(IDbConnectionFactory connectionFactory, IUserCipher cipher, ISqlDialect dialect) : IBookmarkRepository
{
    private const string MarkTable = "dbo.ImageBookmarkMark";
    private const string MarkParent = "ImageBookmarkId";

    private const string TokenCatTable = "dbo.TokenBookmarkCategory";
    private const string TokenCatParent = "TokenBookmarkId";
    private const string ImageCatTable = "dbo.ImageBookmarkCategory";
    private const string ImageCatParent = "ImageBookmarkId";

    private const string ImageColumns =
        "Id, UserId, GatewayImageId, Prompt, ModelFriendly, ModelId, Aspect, OriginalCreatedAtUtc, SavedAtUtc";

    private readonly IDbConnectionFactory _connectionFactory = connectionFactory;
    /// <summary>Supplies the few SQL fragments the two engines spell differently.</summary>
    private readonly ISqlDialect _dialect = dialect;
    private readonly IUserCipher _cipher = cipher;

    #region token bookmarks (starred artists/tags)

    public async Task<IReadOnlyList<TokenBookmark>> GetTokensAsync(long userId, CancellationToken ct)
    {
        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var cmd = conn.Command(
            "SELECT Id, UserId, Name, Kind, SavedAtUtc, PinnedAtUtc FROM dbo.TokenBookmark WHERE UserId = @userId "
            + "ORDER BY SavedAtUtc DESC, Id DESC;");
        cmd.AddParam("@userId", userId);

        var raw = new List<TokenBookmarkRow>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                raw.Add(new TokenBookmarkRow(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
                    (TokenKind)reader.AsByte(3), DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc),
                    reader.IsDBNull(5) ? null : DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc)));

        var cats = await CategoryIo.LoadAsync(
            conn, TokenCatTable, TokenCatParent, raw.Select(r => r.Id).ToList(), userId, _cipher, ct);

        var list = new List<TokenBookmark>(raw.Count);
        foreach (var r in raw)
            list.Add(new TokenBookmark
            {
                Id = r.Id,
                UserId = r.UserId,
                Name = await _cipher.DecryptDeterministicAsync(r.UserId, r.Name, ct),
                Kind = r.Kind,
                SavedAtUtc = r.Saved,
                PinnedAtUtc = r.Pinned,
                Categories = cats.TryGetValue(r.Id, out var cl) ? cl : [],
            });
        return list;
    }

    /// <summary>A raw token-bookmark row buffered with its still-encrypted name before deferred decryption.</summary>
    private readonly record struct TokenBookmarkRow(
        long Id, long UserId, string Name, TokenKind Kind, DateTime Saved, DateTime? Pinned);

    public async Task<bool> IsImageBookmarkedAsync(long userId, string gatewayImageId, CancellationToken ct)
    {
        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var cmd = conn.Command(
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.ImageBookmark WHERE UserId = @userId AND GatewayImageId = @img) "
            + "THEN 1 ELSE 0 END;");
        cmd.AddParam("@userId", userId);
        cmd.AddParam("@img", gatewayImageId);
        return await cmd.ScalarInt32Async(ct) == 1;
    }

    public async Task<bool> AddTokenAsync(TokenBookmark bookmark, CancellationToken ct)
    {
        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var cmd = await BuildInsertTokenCommandAsync(conn, null, bookmark, ct);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> RemoveTokenAsync(long userId, string name, TokenKind kind, CancellationToken ct)
    {
        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var cmd = conn.Command(
            "DELETE FROM dbo.TokenBookmark WHERE UserId = @userId AND Name = @name AND Kind = @kind;");
        cmd.AddParam("@userId", userId);
        cmd.AddParam("@name", await _cipher.DeterministicAsync(userId, name, ct));
        cmd.AddParam("@kind", (byte)kind);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> SetTokenPinnedAsync(
        long userId, string name, TokenKind kind, DateTime? pinnedAtUtc, CancellationToken ct)
    {
        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var cmd = conn.Command(
            "UPDATE dbo.TokenBookmark SET PinnedAtUtc = @pinned "
            + "WHERE UserId = @userId AND Name = @name AND Kind = @kind;");
        cmd.AddParam("@userId", userId);
        cmd.AddParam("@name", await _cipher.DeterministicAsync(userId, name, ct));
        cmd.AddParam("@kind", (byte)kind);
        cmd.AddParam("@pinned", (object?)pinnedAtUtc ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    #endregion

    #region image bookmarks (starred image copies, with their marks)

    public async Task<IReadOnlyList<ImageBookmark>> GetImagesAsync(long userId, CancellationToken ct)
    {
        await using var conn = await _connectionFactory.OpenAsync(ct);

        var rows = new List<ImageBookmark>();
        var ids = new List<long>();
        await using (var cmd = conn.Command(
            $"SELECT {ImageColumns} FROM dbo.ImageBookmark WHERE UserId = @userId ORDER BY SavedAtUtc DESC, Id DESC;"))
        {
            cmd.AddParam("@userId", userId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var image = MapImage(reader);
                rows.Add(image);
                ids.Add(image.Id);
            }
        }

        var marks = await MarkIo.LoadAsync(conn, MarkTable, MarkParent, ids, userId, _cipher, ct);
        var cats = await CategoryIo.LoadAsync(conn, ImageCatTable, ImageCatParent, ids, userId, _cipher, ct);
        var result = new List<ImageBookmark>(rows.Count);
        foreach (var i in rows)
            result.Add(await WithMarksAsync(i, marks, cats, ct));
        return result;
    }

    public async Task<bool> AddImageAsync(ImageBookmark bookmark, CancellationToken ct)
    {
        // Provision the key BEFORE the transaction: the cipher writes on its own connection the first time a
        // user encrypts anything, and SQLite allows one writer -- doing it inside would deadlock against us.
        await _cipher.EnsureKeyAsync(bookmark.UserId, ct);
        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var inserted = await InsertImageAsync(conn, tx, bookmark, ct);
        await tx.CommitAsync(ct);
        return inserted;
    }

    public async Task<bool> RemoveImageAsync(long userId, string gatewayImageId, CancellationToken ct)
    {
        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var cmd = conn.Command(
            "DELETE FROM dbo.ImageBookmark WHERE UserId = @userId AND GatewayImageId = @img;");
        cmd.AddParam("@userId", userId);
        cmd.AddParam("@img", gatewayImageId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private async Task<bool> InsertImageAsync(
        DbConnection conn, DbTransaction tx, ImageBookmark b, CancellationToken ct)
    {
        // A NULL identity means this image was already bookmarked, which is how re-starring is a no-op instead of a
        // second row. See ISqlDialect.InsertedIdentityOrNull.
        var sql = $@"
INSERT INTO dbo.ImageBookmark
    (UserId, GatewayImageId, Prompt, ModelFriendly, ModelId, Aspect, OriginalCreatedAtUtc, SavedAtUtc)
SELECT @userId, @img, @prompt, @modelFriendly, @modelId, @aspect, @original, @saved
WHERE NOT EXISTS (SELECT 1 FROM dbo.ImageBookmark WHERE UserId = @userId AND GatewayImageId = @img);
{_dialect.InsertedIdentityOrNull}";

        long? newId;
        await using (var cmd = conn.Command(sql, tx))
        {
            cmd.AddParam("@userId", b.UserId);
            cmd.AddParam("@img", b.GatewayImageId);
            cmd.AddParam("@prompt", await _cipher.EncryptAsync(b.UserId, b.Prompt, ct));
            cmd.AddParam("@modelFriendly", b.ModelFriendly);
            cmd.AddParam("@modelId", b.ModelId);
            cmd.AddParam("@aspect", b.Aspect);
            cmd.AddParam("@original", b.OriginalCreatedAtUtc);
            cmd.AddParam("@saved", b.SavedAtUtc);
            newId = await cmd.ScalarNullableInt64Async(ct);
        }

        if (newId is null)
            return false;

        await MarkIo.InsertAsync(conn, tx, MarkTable, MarkParent, newId.Value, b.Marks, b.UserId, _cipher, ct);
        return true;
    }

    private async Task<DbCommand> BuildInsertTokenCommandAsync(
        DbConnection conn, DbTransaction? tx, TokenBookmark b, CancellationToken ct)
    {
        const string sql = @"
INSERT INTO dbo.TokenBookmark (UserId, Name, Kind, SavedAtUtc)
SELECT @userId, @name, @kind, @saved
WHERE NOT EXISTS (SELECT 1 FROM dbo.TokenBookmark WHERE UserId = @userId AND Name = @name AND Kind = @kind);";
        var cmd = tx is null ? conn.Command(sql) : conn.Command(sql, tx);
        cmd.AddParam("@userId", b.UserId);
        cmd.AddParam("@name", await _cipher.DeterministicAsync(b.UserId, b.Name, ct));
        cmd.AddParam("@kind", (byte)b.Kind);
        cmd.AddParam("@saved", b.SavedAtUtc);
        return cmd;
    }

    private static ImageBookmark MapImage(DbDataReader r) => new()
    {
        Id = r.GetInt64(0),
        UserId = r.GetInt64(1),
        GatewayImageId = r.GetString(2),
        Prompt = r.GetString(3),   // ciphertext here; decrypted in WithMarksAsync
        ModelFriendly = r.GetString(4),
        ModelId = r.GetString(5),
        Aspect = r.GetString(6),
        OriginalCreatedAtUtc = DateTime.SpecifyKind(r.GetDateTime(7), DateTimeKind.Utc),
        SavedAtUtc = DateTime.SpecifyKind(r.GetDateTime(8), DateTimeKind.Utc),
    };

    private async Task<ImageBookmark> WithMarksAsync(
        ImageBookmark b, IReadOnlyDictionary<long, List<Mark>> marks,
        IReadOnlyDictionary<long, List<string>> categories, CancellationToken ct) => new()
    {
        Id = b.Id,
        UserId = b.UserId,
        GatewayImageId = b.GatewayImageId,
        Prompt = await _cipher.DecryptAsync(b.UserId, b.Prompt, ct),
        ModelFriendly = b.ModelFriendly,
        ModelId = b.ModelId,
        Aspect = b.Aspect,
        OriginalCreatedAtUtc = b.OriginalCreatedAtUtc,
        SavedAtUtc = b.SavedAtUtc,
        Marks = marks.TryGetValue(b.Id, out var list) ? list : [],
        Categories = categories.TryGetValue(b.Id, out var cl) ? cl : [],
    };

    #endregion

    #region bookmark categories

    public async Task<IReadOnlyList<string>> GetAllCategoriesAsync(long userId, CancellationToken ct)
    {
        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var cmd = conn.Command(
            "SELECT c.Category FROM dbo.TokenBookmarkCategory c "
            + "JOIN dbo.TokenBookmark b ON b.Id = c.TokenBookmarkId WHERE b.UserId = @userId "
            + "UNION "
            + "SELECT c.Category FROM dbo.ImageBookmarkCategory c "
            + "JOIN dbo.ImageBookmark b ON b.Id = c.ImageBookmarkId WHERE b.UserId = @userId;");
        cmd.AddParam("@userId", userId);

        var encrypted = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                encrypted.Add(reader.GetString(0));

        // Distinct ciphertext collapses exact-match names; casing variants are then folded case-insensitively,
        // keeping the first spelling seen. Sorted for a stable checklist.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        foreach (var enc in encrypted)
        {
            var name = await _cipher.DecryptDeterministicAsync(userId, enc, ct);
            if (seen.Add(name))
                names.Add(name);
        }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    public async Task<IReadOnlyList<string>> GetTokenCategoriesAsync(
        long userId, string name, TokenKind kind, CancellationToken ct)
    {
        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var cmd = conn.Command(
            "SELECT c.Category FROM dbo.TokenBookmarkCategory c "
            + "JOIN dbo.TokenBookmark b ON b.Id = c.TokenBookmarkId "
            + "WHERE b.UserId = @userId AND b.Name = @name AND b.Kind = @kind;");
        cmd.AddParam("@userId", userId);
        cmd.AddParam("@name", await _cipher.DeterministicAsync(userId, name, ct));
        cmd.AddParam("@kind", (byte)kind);
        return await ReadCategoriesAsync(cmd, userId, ct);
    }

    public async Task<IReadOnlyList<string>> GetImageCategoriesAsync(
        long userId, string gatewayImageId, CancellationToken ct)
    {
        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var cmd = conn.Command(
            "SELECT c.Category FROM dbo.ImageBookmarkCategory c "
            + "JOIN dbo.ImageBookmark b ON b.Id = c.ImageBookmarkId "
            + "WHERE b.UserId = @userId AND b.GatewayImageId = @img;");
        cmd.AddParam("@userId", userId);
        cmd.AddParam("@img", gatewayImageId);
        return await ReadCategoriesAsync(cmd, userId, ct);
    }

    private async Task<IReadOnlyList<string>> ReadCategoriesAsync(DbCommand cmd, long userId, CancellationToken ct)
    {
        var encrypted = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                encrypted.Add(reader.GetString(0));

        var names = new List<string>(encrypted.Count);
        foreach (var enc in encrypted)
            names.Add(await _cipher.DecryptDeterministicAsync(userId, enc, ct));
        return names;
    }

    public async Task SetTokenCategoriesAsync(
        TokenBookmark bookmark, IReadOnlyList<string> categories, CancellationToken ct)
    {
        // Provision the key BEFORE the transaction: the cipher writes on its own connection the first time a
        // user encrypts anything, and SQLite allows one writer -- doing it inside would deadlock against us.
        await _cipher.EnsureKeyAsync(bookmark.UserId, ct);
        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Ensure the bookmark row exists (a long-press on an un-starred chip implies starring it), then read its id.
        await using (var ins = await BuildInsertTokenCommandAsync(conn, tx, bookmark, ct))
            await ins.ExecuteNonQueryAsync(ct);

        long id;
        await using (var sel = conn.Command(
            "SELECT Id FROM dbo.TokenBookmark WHERE UserId = @userId AND Name = @name AND Kind = @kind;", tx))
        {
            sel.AddParam("@userId", bookmark.UserId);
            sel.AddParam("@name", await _cipher.DeterministicAsync(bookmark.UserId, bookmark.Name, ct));
            sel.AddParam("@kind", (byte)bookmark.Kind);
            id = await sel.ScalarNullableInt64Async(ct)
                ?? throw new InvalidOperationException("Token bookmark row is missing immediately after being ensured.");
        }

        await CategoryIo.ReplaceAsync(conn, tx, TokenCatTable, TokenCatParent, id, categories, bookmark.UserId, _cipher, ct);
        await tx.CommitAsync(ct);
    }

    public async Task SetImageCategoriesAsync(
        ImageBookmark bookmark, IReadOnlyList<string> categories, CancellationToken ct)
    {
        // Provision the key BEFORE the transaction: the cipher writes on its own connection the first time a
        // user encrypts anything, and SQLite allows one writer -- doing it inside would deadlock against us.
        await _cipher.EnsureKeyAsync(bookmark.UserId, ct);
        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Ensure the image bookmark exists (no-op if it already does), then read its id.
        await InsertImageAsync(conn, tx, bookmark, ct);

        long id;
        await using (var sel = conn.Command(
            "SELECT Id FROM dbo.ImageBookmark WHERE UserId = @userId AND GatewayImageId = @img;", tx))
        {
            sel.AddParam("@userId", bookmark.UserId);
            sel.AddParam("@img", bookmark.GatewayImageId);
            id = await sel.ScalarNullableInt64Async(ct)
                ?? throw new InvalidOperationException("Image bookmark row is missing immediately after being ensured.");
        }

        await CategoryIo.ReplaceAsync(conn, tx, ImageCatTable, ImageCatParent, id, categories, bookmark.UserId, _cipher, ct);
        await tx.CommitAsync(ct);
    }

    #endregion
}
