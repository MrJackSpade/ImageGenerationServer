using ImageGen.Application.Security;
using ImageGen.Domain;
using ImageGen.Domain.CodeAnalysis;
using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;
using ImageGen.Infrastructure.Database;
using System.Data.Common;

namespace ImageGen.Infrastructure.Repositories;

[AllowMagicStrings("SQL query text and its bound @parameter-name tokens")]
public sealed class BookmarkRepository(IDbConnectionFactory connectionFactory, IUserCipher cipher, ISqlDialect dialect) : IBookmarkRepository
{
    private static class Sql
    {
        public const string MarkTable = "dbo.ImageBookmarkMark";
        public const string MarkParent = "ImageBookmarkId";

        public const string TokenCatTable = "dbo.TokenBookmarkCategory";
        public const string TokenCatParent = "TokenBookmarkId";
        public const string ImageCatTable = "dbo.ImageBookmarkCategory";
        public const string ImageCatParent = "ImageBookmarkId";

        public const string ImageColumns =
            "Id, UserId, GatewayImageId, Prompt, ModelFriendly, ModelId, Aspect, OriginalCreatedAtUtc, SavedAtUtc";
    }

    private readonly IDbConnectionFactory _connectionFactory = connectionFactory;
    /// <summary>Supplies the few SQL fragments the two engines spell differently.</summary>
    private readonly ISqlDialect _dialect = dialect;
    private readonly IUserCipher _cipher = cipher;

    #region token bookmarks (starred artists/tags)

    public async Task<IReadOnlyList<TokenBookmark>> GetTokensAsync(long userId, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command(
            "SELECT Id, UserId, Name, Kind, SavedAtUtc, PinnedAtUtc FROM dbo.TokenBookmark WHERE UserId = @userId "
            + "ORDER BY SavedAtUtc DESC, Id DESC;");
        _ = cmd.AddParam("@userId", userId);

        List<TokenBookmarkRow> raw = [];
        await using (DbDataReader reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                raw.Add(new TokenBookmarkRow(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
                    (TokenKind)reader.AsByte(3), DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc),
                    reader.IsDBNull(5) ? null : DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc)));
            }
        }

        Dictionary<long, List<string>> cats = await CategoryIo.LoadAsync(
            conn, Sql.TokenCatTable, Sql.TokenCatParent, raw.Select(r => r.Id).ToList(), userId, _cipher, ct);

        List<TokenBookmark> list = new(raw.Count);
        foreach (TokenBookmarkRow r in raw)
        {
            list.Add(new TokenBookmark
            {
                Id = r.Id,
                UserId = r.UserId,
                Name = await _cipher.DecryptDeterministicAsync(r.UserId, r.Name, ct),
                Kind = r.Kind,
                SavedAtUtc = r.Saved,
                PinnedAtUtc = r.Pinned,
                Categories = cats.TryGetValue(r.Id, out List<string>? cl) ? cl : [],
            });
        }

        return list;
    }

    /// <summary>A raw token-bookmark row buffered with its still-encrypted name before deferred decryption.</summary>
    private readonly record struct TokenBookmarkRow(
        long Id, long UserId, string Name, TokenKind Kind, DateTime Saved,
        [property: AllowNullable("null = unpinned; mirrors the nullable dbo.TokenBookmark column. No default timestamp means \"not pinned\"")] DateTime? Pinned);

    public async Task<bool> IsImageBookmarkedAsync(long userId, string gatewayImageId, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command(
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.ImageBookmark WHERE UserId = @userId AND GatewayImageId = @img) "
            + "THEN 1 ELSE 0 END;");
        _ = cmd.AddParam("@userId", userId);
        _ = cmd.AddParam("@img", gatewayImageId);
        return await cmd.ScalarInt32Async(ct) == 1;
    }

    public async Task<bool> AddTokenAsync(TokenBookmark bookmark, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = await BuildInsertTokenCommandAsync(conn, null, bookmark, ct);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> RemoveTokenAsync(long userId, string name, TokenKind kind, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command(
            "DELETE FROM dbo.TokenBookmark WHERE UserId = @userId AND Name = @name AND Kind = @kind;");
        _ = cmd.AddParam("@userId", userId);
        _ = cmd.AddParam("@name", await _cipher.DeterministicAsync(userId, name, ct));
        _ = cmd.AddParam("@kind", (byte)kind);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> SetTokenPinnedAsync(
        long userId, string name, TokenKind kind, DateTime? pinnedAtUtc, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command(
            "UPDATE dbo.TokenBookmark SET PinnedAtUtc = @pinned "
            + "WHERE UserId = @userId AND Name = @name AND Kind = @kind;");
        _ = cmd.AddParam("@userId", userId);
        _ = cmd.AddParam("@name", await _cipher.DeterministicAsync(userId, name, ct));
        _ = cmd.AddParam("@kind", (byte)kind);
        _ = cmd.AddParam("@pinned", (object?)pinnedAtUtc ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    #endregion

    #region image bookmarks (starred image copies, with their marks)

    public async Task<IReadOnlyList<ImageBookmark>> GetImagesAsync(long userId, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);

        List<ImageBookmark> rows = [];
        List<long> ids = [];
        await using (DbCommand cmd = conn.Command(
            $"SELECT {Sql.ImageColumns} FROM dbo.ImageBookmark WHERE UserId = @userId ORDER BY SavedAtUtc DESC, Id DESC;"))
        {
            _ = cmd.AddParam("@userId", userId);
            await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                ImageBookmark image = MapImage(reader);
                rows.Add(image);
                ids.Add(image.Id);
            }
        }

        Dictionary<long, List<Mark>> marks = await MarkIo.LoadAsync(conn, Sql.MarkTable, Sql.MarkParent, ids, userId, _cipher, ct);
        Dictionary<long, List<string>> cats = await CategoryIo.LoadAsync(conn, Sql.ImageCatTable, Sql.ImageCatParent, ids, userId, _cipher, ct);
        List<ImageBookmark> result = new(rows.Count);
        foreach (ImageBookmark i in rows)
        {
            result.Add(await WithMarksAsync(i, marks, cats, ct));
        }

        return result;
    }

    public async Task<bool> AddImageAsync(ImageBookmark bookmark, CancellationToken ct)
    {
        // Provision the key BEFORE the transaction: the cipher writes on its own connection the first time a
        // user encrypts anything, and SQLite allows one writer -- doing it inside would deadlock against us.
        await _cipher.EnsureKeyAsync(bookmark.UserId, ct);
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbTransaction tx = await conn.BeginTransactionAsync(ct);
        bool inserted = await InsertImageAsync(conn, tx, bookmark, ct);
        await tx.CommitAsync(ct);
        return inserted;
    }

    public async Task<bool> RemoveImageAsync(long userId, string gatewayImageId, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command(
            "DELETE FROM dbo.ImageBookmark WHERE UserId = @userId AND GatewayImageId = @img;");
        _ = cmd.AddParam("@userId", userId);
        _ = cmd.AddParam("@img", gatewayImageId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private async Task<bool> InsertImageAsync(
        DbConnection conn, DbTransaction tx, ImageBookmark b, CancellationToken ct)
    {
        // A NULL identity means this image was already bookmarked, which is how re-starring is a no-op instead of a
        // second row. See ISqlDialect.InsertedIdentityOrNull.
        string sql = $@"
INSERT INTO dbo.ImageBookmark
    (UserId, GatewayImageId, Prompt, ModelFriendly, ModelId, Aspect, OriginalCreatedAtUtc, SavedAtUtc)
SELECT @userId, @img, @prompt, @modelFriendly, @modelId, @aspect, @original, @saved
WHERE NOT EXISTS (SELECT 1 FROM dbo.ImageBookmark WHERE UserId = @userId AND GatewayImageId = @img);
{_dialect.InsertedIdentityOrNull}";

        long? newId;
        await using (DbCommand cmd = conn.Command(sql, tx))
        {
            _ = cmd.AddParam("@userId", b.UserId);
            _ = cmd.AddParam("@img", b.GatewayImageId);
            _ = cmd.AddParam("@prompt", await _cipher.EncryptAsync(b.UserId, b.Prompt, ct));
            _ = cmd.AddParam("@modelFriendly", b.ModelFriendly);
            _ = cmd.AddParam("@modelId", b.ModelId);
            _ = cmd.AddParam("@aspect", b.Aspect);
            _ = cmd.AddParam("@original", b.OriginalCreatedAtUtc);
            _ = cmd.AddParam("@saved", b.SavedAtUtc);
            newId = await cmd.ScalarNullableInt64Async(ct);
        }

        if (newId is null)
        {
            return false;
        }

        await MarkIo.InsertAsync(conn, tx, Sql.MarkTable, Sql.MarkParent, newId.Value, b.Marks, b.UserId, _cipher, ct);
        return true;
    }

    private async Task<DbCommand> BuildInsertTokenCommandAsync(
        DbConnection conn, DbTransaction? tx, TokenBookmark b, CancellationToken ct)
    {
        const string sql = @"
INSERT INTO dbo.TokenBookmark (UserId, Name, Kind, SavedAtUtc)
SELECT @userId, @name, @kind, @saved
WHERE NOT EXISTS (SELECT 1 FROM dbo.TokenBookmark WHERE UserId = @userId AND Name = @name AND Kind = @kind);";
        DbCommand cmd = tx is null ? conn.Command(sql) : conn.Command(sql, tx);
        _ = cmd.AddParam("@userId", b.UserId);
        _ = cmd.AddParam("@name", await _cipher.DeterministicAsync(b.UserId, b.Name, ct));
        _ = cmd.AddParam("@kind", (byte)b.Kind);
        _ = cmd.AddParam("@saved", b.SavedAtUtc);
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
            Marks = marks.TryGetValue(b.Id, out List<Mark>? list) ? list : [],
            Categories = categories.TryGetValue(b.Id, out List<string>? cl) ? cl : [],
        };

    #endregion

    #region bookmark categories

    public async Task<IReadOnlyList<string>> GetAllCategoriesAsync(long userId, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command(
            "SELECT c.Category FROM dbo.TokenBookmarkCategory c "
            + "JOIN dbo.TokenBookmark b ON b.Id = c.TokenBookmarkId WHERE b.UserId = @userId "
            + "UNION "
            + "SELECT c.Category FROM dbo.ImageBookmarkCategory c "
            + "JOIN dbo.ImageBookmark b ON b.Id = c.ImageBookmarkId WHERE b.UserId = @userId;");
        _ = cmd.AddParam("@userId", userId);

        List<string> encrypted = [];
        await using (DbDataReader reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                encrypted.Add(reader.GetString(0));
            }
        }

        // Distinct ciphertext collapses exact-match names; casing variants are then folded case-insensitively,
        // keeping the first spelling seen. Sorted for a stable checklist.
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<string> names = [];
        foreach (string enc in encrypted)
        {
            string name = await _cipher.DecryptDeterministicAsync(userId, enc, ct);
            if (seen.Add(name))
            {
                names.Add(name);
            }
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    public async Task<IReadOnlyList<string>> GetTokenCategoriesAsync(
        long userId, string name, TokenKind kind, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command(
            "SELECT c.Category FROM dbo.TokenBookmarkCategory c "
            + "JOIN dbo.TokenBookmark b ON b.Id = c.TokenBookmarkId "
            + "WHERE b.UserId = @userId AND b.Name = @name AND b.Kind = @kind;");
        _ = cmd.AddParam("@userId", userId);
        _ = cmd.AddParam("@name", await _cipher.DeterministicAsync(userId, name, ct));
        _ = cmd.AddParam("@kind", (byte)kind);
        return await ReadCategoriesAsync(cmd, userId, ct);
    }

    public async Task<IReadOnlyList<string>> GetImageCategoriesAsync(
        long userId, string gatewayImageId, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command(
            "SELECT c.Category FROM dbo.ImageBookmarkCategory c "
            + "JOIN dbo.ImageBookmark b ON b.Id = c.ImageBookmarkId "
            + "WHERE b.UserId = @userId AND b.GatewayImageId = @img;");
        _ = cmd.AddParam("@userId", userId);
        _ = cmd.AddParam("@img", gatewayImageId);
        return await ReadCategoriesAsync(cmd, userId, ct);
    }

    private async Task<IReadOnlyList<string>> ReadCategoriesAsync(DbCommand cmd, long userId, CancellationToken ct)
    {
        List<string> encrypted = [];
        await using (DbDataReader reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                encrypted.Add(reader.GetString(0));
            }
        }

        List<string> names = new(encrypted.Count);
        foreach (string enc in encrypted)
        {
            names.Add(await _cipher.DecryptDeterministicAsync(userId, enc, ct));
        }

        return names;
    }

    public async Task SetTokenCategoriesAsync(
        TokenBookmark bookmark, IReadOnlyList<string> categories, CancellationToken ct)
    {
        // Provision the key BEFORE the transaction: the cipher writes on its own connection the first time a
        // user encrypts anything, and SQLite allows one writer -- doing it inside would deadlock against us.
        await _cipher.EnsureKeyAsync(bookmark.UserId, ct);
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbTransaction tx = await conn.BeginTransactionAsync(ct);

        // Ensure the bookmark row exists (a long-press on an un-starred chip implies starring it), then read its id.
        await using (DbCommand ins = await BuildInsertTokenCommandAsync(conn, tx, bookmark, ct))
        {
            _ = await ins.ExecuteNonQueryAsync(ct);
        }

        long id;
        await using (DbCommand sel = conn.Command(
            "SELECT Id FROM dbo.TokenBookmark WHERE UserId = @userId AND Name = @name AND Kind = @kind;", tx))
        {
            _ = sel.AddParam("@userId", bookmark.UserId);
            _ = sel.AddParam("@name", await _cipher.DeterministicAsync(bookmark.UserId, bookmark.Name, ct));
            _ = sel.AddParam("@kind", (byte)bookmark.Kind);
            id = await sel.ScalarNullableInt64Async(ct)
                ?? throw new InvalidOperationException("Token bookmark row is missing immediately after being ensured.");
        }

        await CategoryIo.ReplaceAsync(conn, tx, Sql.TokenCatTable, Sql.TokenCatParent, id, categories, bookmark.UserId, _cipher, ct);
        await tx.CommitAsync(ct);
    }

    public async Task SetImageCategoriesAsync(
        ImageBookmark bookmark, IReadOnlyList<string> categories, CancellationToken ct)
    {
        // Provision the key BEFORE the transaction: the cipher writes on its own connection the first time a
        // user encrypts anything, and SQLite allows one writer -- doing it inside would deadlock against us.
        await _cipher.EnsureKeyAsync(bookmark.UserId, ct);
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbTransaction tx = await conn.BeginTransactionAsync(ct);

        // Ensure the image bookmark exists (no-op if it already does), then read its id.
        _ = await InsertImageAsync(conn, tx, bookmark, ct);

        long id;
        await using (DbCommand sel = conn.Command(
            "SELECT Id FROM dbo.ImageBookmark WHERE UserId = @userId AND GatewayImageId = @img;", tx))
        {
            _ = sel.AddParam("@userId", bookmark.UserId);
            _ = sel.AddParam("@img", bookmark.GatewayImageId);
            id = await sel.ScalarNullableInt64Async(ct)
                ?? throw new InvalidOperationException("Image bookmark row is missing immediately after being ensured.");
        }

        await CategoryIo.ReplaceAsync(conn, tx, Sql.ImageCatTable, Sql.ImageCatParent, id, categories, bookmark.UserId, _cipher, ct);
        await tx.CommitAsync(ct);
    }

    #endregion
}
