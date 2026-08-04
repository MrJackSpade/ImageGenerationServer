using System.Data.Common;
using System.Text;
using ImageGen.Application.Prompting;
using ImageGen.Application.Security;
using ImageGen.Domain;
using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;
using ImageGen.Infrastructure.Database;
using Microsoft.Data.SqlClient;

namespace ImageGen.Infrastructure.Repositories;

public sealed class HistoryRepository(IDbConnectionFactory connectionFactory, IUserCipher cipher, ISqlDialect dialect) : IHistoryRepository
{
    private const string MarkTable = "dbo.HistoryMark";
    private const string MarkParent = "HistoryEntryId";
    private const string LoraTable = "dbo.HistoryLora";
    private const string LoraParent = "HistoryEntryId";

    /// <summary>Positional: MapEntry reads by ordinal, so append — never insert — a column here.</summary>
    private const string EntryColumns =
        "Id, UserId, GatewayImageId, Prompt, ModelFriendly, ModelId, Aspect, CreatedAtUtc, RawPrompt, RawNegativePrompt, OriginalPrompt";

    private readonly IDbConnectionFactory _connectionFactory = connectionFactory;
    /// <summary>Supplies the few SQL fragments the two engines spell differently.</summary>
    private readonly ISqlDialect _dialect = dialect;
    private readonly IUserCipher _cipher = cipher;

    public async Task<PagedResult<HistoryEntry>> GetPageAsync(HistoryQuery query, CancellationToken ct)
    {
        var (userId, _, _, artist, tag, model, search, unviewedOnly) = query;
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);

        // Tokens are deterministically encrypted at rest, so an equality filter must compare against the ciphertext.
        var artistEnc = string.IsNullOrWhiteSpace(artist) ? null : await _cipher.DeterministicAsync(userId, artist, ct);
        var tagEnc = string.IsNullOrWhiteSpace(tag) ? null : await _cipher.DeterministicAsync(userId, tag, ct);

        var where = new StringBuilder("WHERE h.UserId = @userId");
        // An artist page shows what THAT artist's style looks like, so an image made with two or more artists
        // belongs to none of them — a blend is not evidence of either, and claimed by every one of their pages it
        // pollutes all of them. Carrying the mark is therefore not enough: no OTHER artist mark may be present.
        // (Deterministic encryption means equal plaintext is equal ciphertext, so comparing ciphertext here really
        // does mean "a different artist".) The image stays reachable through the gallery, history and search.
        if (artistEnc is not null)
            where.Append(" AND EXISTS (SELECT 1 FROM dbo.HistoryMark m WHERE m.HistoryEntryId = h.Id "
                + "AND m.Kind = 1 AND m.Token = @artist)"
                + " AND NOT EXISTS (SELECT 1 FROM dbo.HistoryMark mo WHERE mo.HistoryEntryId = h.Id "
                + "AND mo.Kind = 1 AND mo.Token <> @artist)");
        if (tagEnc is not null)
            where.Append(" AND EXISTS (SELECT 1 FROM dbo.HistoryMark t WHERE t.HistoryEntryId = h.Id "
                + "AND t.Kind = 0 AND t.Token = @tag)");
        var modelFilter = string.IsNullOrWhiteSpace(model) ? null : model;   // ModelId is stored plain — direct equality
        if (modelFilter is not null)
            where.Append(" AND h.ModelId = @model");
        // Unviewed is the absence of a view row (the table records only what HAS been opened), so this is an
        // anti-join and takes no parameter. Same predicate MarkAllViewedAsync uses to find the backlog.
        if (unviewedOnly)
            where.Append(" AND NOT EXISTS (SELECT 1 FROM dbo.ImageView v "
                + "WHERE v.UserId = h.UserId AND v.GatewayImageId = h.GatewayImageId)");

        await using var conn = await _connectionFactory.OpenAsync(ct);

        var terms = PromptSearch.Terms(search);
        var (rows, total) = terms.Length == 0
            ? await OffsetPageAsync(conn, where.ToString(), userId, artistEnc, tagEnc, modelFilter, page, pageSize, ct)
            : await SearchPageAsync(conn, where.ToString(), userId, artistEnc, tagEnc, modelFilter, terms, page, pageSize, ct);

        var ids = rows.Select(r => r.Id).ToList();
        var marks = await MarkIo.LoadAsync(conn, MarkTable, MarkParent, ids, userId, _cipher, ct);
        var loras = await LoraIo.LoadAsync(conn, LoraTable, LoraParent, ids, userId, _cipher, ct);
        var items = new List<HistoryEntry>(rows.Count);
        foreach (var e in rows)
            items.Add(await WithChildrenAsync(e, marks, loras, ct));
        return new PagedResult<HistoryEntry>(items, total, page, pageSize);
    }

    /// <summary>One page taken in SQL: the database counts and skips, and only the page's rows come back.</summary>
    private async Task<(List<HistoryEntry> Rows, int Total)> OffsetPageAsync(
        DbConnection conn, string where, long userId, string? artistEnc, string? tagEnc, string? modelFilter,
        int page, int pageSize, CancellationToken ct)
    {
        int total;
        await using (var countCmd = conn.Command($"SELECT COUNT(*) FROM dbo.HistoryEntry h {where};"))
        {
            AddFilterParams(countCmd, userId, artistEnc, tagEnc, modelFilter);
            total = await countCmd.ScalarInt32Async(ct);
        }

        var rows = new List<HistoryEntry>();
        var pageSql = $@"SELECT {Prefixed(EntryColumns, "h")} FROM dbo.HistoryEntry h {where}
ORDER BY h.CreatedAtUtc DESC, h.Id DESC
{_dialect.Paginate("@skip", "@take")};";
        await using (var pageCmd = conn.Command(pageSql))
        {
            AddFilterParams(pageCmd, userId, artistEnc, tagEnc, modelFilter);
            pageCmd.AddParam("@skip", (page - 1) * pageSize);
            pageCmd.AddParam("@take", pageSize);
            await using var reader = await pageCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                rows.Add(MapEntry(reader));
        }

        return (rows, total);
    }

    /// <summary>
    /// One page of a prompt SEARCH. The prompt columns are randomized-encrypted, so no SQL predicate can read them:
    /// every row the other filters allow is fetched in order, decrypted, and matched here — which is also why Total is
    /// the number of MATCHES rather than a COUNT(*), and why the paging (skip/take) happens after the match, not in
    /// the query. Decryption is local AES with a cached per-user key, so the cost is the read, not the crypto.
    /// </summary>
    private async Task<(List<HistoryEntry> Rows, int Total)> SearchPageAsync(
        DbConnection conn, string where, long userId, string? artistEnc, string? tagEnc, string? modelFilter,
        string[] terms, int page, int pageSize, CancellationToken ct)
    {
        var candidates = new List<HistoryEntry>();
        var sql = $@"SELECT {Prefixed(EntryColumns, "h")} FROM dbo.HistoryEntry h {where}
ORDER BY h.CreatedAtUtc DESC, h.Id DESC;";
        await using (var cmd = conn.Command(sql))
        {
            AddFilterParams(cmd, userId, artistEnc, tagEnc, modelFilter);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                candidates.Add(MapEntry(reader));
        }

        // Matched rows are kept as they came off the reader (still ciphertext); WithMarksAsync decrypts the page.
        var matches = new List<HistoryEntry>();
        foreach (var e in candidates)
        {
            var prompt = await _cipher.DecryptAsync(userId, e.Prompt, ct);
            var raw = await _cipher.DecryptNullableAsync(userId, e.RawPrompt, ct);
            if (PromptSearch.Matches(terms, prompt, raw))
                matches.Add(e);
        }

        var rows = matches.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return (rows, matches.Count);
    }

    public async Task<HistoryEntry?> GetByGatewayImageIdAsync(long userId, string gatewayImageId, CancellationToken ct)
    {
        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var cmd = conn.Command(
            $"SELECT {EntryColumns} FROM dbo.HistoryEntry WHERE UserId = @userId AND GatewayImageId = @img;");
        cmd.AddParam("@userId", userId);
        cmd.AddParam("@img", gatewayImageId);

        HistoryEntry? entry;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            entry = await reader.ReadAsync(ct) ? MapEntry(reader) : null;
        if (entry is null)
            return null;

        var marks = await MarkIo.LoadAsync(conn, MarkTable, MarkParent, [entry.Id], userId, _cipher, ct);
        var loras = await LoraIo.LoadAsync(conn, LoraTable, LoraParent, [entry.Id], userId, _cipher, ct);
        return await WithChildrenAsync(entry, marks, loras, ct);
    }

    public Task<IReadOnlyDictionary<string, string>> GetLatestImageIdsForArtistsAsync(
        long userId, IReadOnlyCollection<string> artistNames, CancellationToken ct) =>
        // Single-artist only: this feeds the artist hero and the bookmarks artist cards, so without it @monet's card
        // could be a picture that's half @picasso while @monet's own grid (GetPageAsync) excludes it.
        GetLatestImageIdsByKindAsync(userId, artistNames, TokenKind.Artist, singleTokenOnly: true, ct);

    public Task<IReadOnlyDictionary<string, string>> GetLatestImageIdsForTagsAsync(
        long userId, IReadOnlyCollection<string> tagNames, CancellationToken ct) =>
        // Additive: an image legitimately carries many tags at once, and each of them counts it as their latest — so
        // unlike artists, a second tag on the image is not disqualifying.
        GetLatestImageIdsByKindAsync(userId, tagNames, TokenKind.Tag, singleTokenOnly: false, ct);

    /// <summary>
    /// The newest generation per token of one kind, scoped to the user, for the bookmark card and hero display-image
    /// fallback. When <paramref name="singleTokenOnly"/> is set, images that also carry another token of the same kind
    /// are excluded — the artist rule, where a blend of two styles represents neither. Tokens are deterministically
    /// encrypted, so the IN-list compares ciphertext and the returned Token is decrypted back.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> GetLatestImageIdsByKindAsync(
        long userId, IReadOnlyCollection<string> names, TokenKind kind, bool singleTokenOnly, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0)
            return result;

        var list = names.ToList();
        var ps = new string[list.Count];
        for (var i = 0; i < list.Count; i++)
            ps[i] = "@a" + i;

        // Kind is a trusted enum value, inlined like the other Kind comparisons in this file (never user input).
        var k = (int)kind;
        var soleToken = singleTokenOnly
            ? $@"
    AND NOT EXISTS (SELECT 1 FROM dbo.HistoryMark mo
                    WHERE mo.HistoryEntryId = m.HistoryEntryId AND mo.Kind = {k} AND mo.Token <> m.Token)"
            : string.Empty;

        // Newest generation per token via ROW_NUMBER, scoped to this user.
        var sql = $@"
SELECT Token, GatewayImageId FROM (
  SELECT m.Token, h.GatewayImageId,
         ROW_NUMBER() OVER (PARTITION BY m.Token ORDER BY h.CreatedAtUtc DESC, h.Id DESC) AS rn
  FROM dbo.HistoryMark m
  JOIN dbo.HistoryEntry h ON h.Id = m.HistoryEntryId
  WHERE h.UserId = @userId AND m.Kind = {k} AND m.Token IN ({string.Join(',', ps)}){soleToken}
) t WHERE rn = 1;";

        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var cmd = conn.Command(sql);
        cmd.AddParam("@userId", userId);
        for (var i = 0; i < list.Count; i++)
            cmd.AddParam(ps[i], await _cipher.DeterministicAsync(userId, list[i], ct));

        var raw = new List<TokenImageRow>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                raw.Add(new TokenImageRow(reader.GetString(0), reader.GetString(1)));
        foreach (var row in raw)
            result[await _cipher.DecryptDeterministicAsync(userId, row.Token, ct)] = row.ImageId;
        return result;
    }

    public async Task<IReadOnlyList<HistoryWorkflowUse>> GetUsedWorkflowsAsync(long userId, CancellationToken ct)
    {
        // ModelId/ModelFriendly are stored plain (they name a shared configuration, not the user's content), so this
        // groups in SQL. A workflow that was renamed has both spellings in the history under one id: rn=1 takes the
        // name from the user's most recent generation with it, and the count still covers every row.
        const string sql = @"
SELECT ModelId, ModelFriendly, Uses FROM (
  SELECT ModelId, ModelFriendly,
         COUNT(*) OVER (PARTITION BY ModelId) AS Uses,
         ROW_NUMBER() OVER (PARTITION BY ModelId ORDER BY CreatedAtUtc DESC, Id DESC) AS rn
  FROM dbo.HistoryEntry WHERE UserId = @userId
) t WHERE rn = 1
ORDER BY Uses DESC, ModelFriendly ASC;";

        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var cmd = conn.Command(sql);
        cmd.AddParam("@userId", userId);

        var rows = new List<HistoryWorkflowUse>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(new HistoryWorkflowUse(reader.GetString(0), reader.GetString(1), reader.AsInt32(2)));
        return rows;
    }

    /// <summary>
    /// The entries either side of this one in the user's history, ordered by <c>(CreatedAtUtc, Id)</c> so that entries
    /// sharing a timestamp still have a stable order.
    /// <para>SQLite has no local variables, so the anchor row's <c>(CreatedAtUtc, Id)</c> cannot be stashed in a
    /// <c>DECLARE</c> and reused within one batch: it is fetched into C# and passed back as parameters — three round
    /// trips instead of one, for a two-arrow navigation control. A missing anchor returns early with
    /// <c>(null, null)</c>.</para>
    /// </summary>
    public async Task<HistoryNeighbors> GetNeighborsAsync(
        long userId, string gatewayImageId, CancellationToken ct)
    {
        await using var conn = await _connectionFactory.OpenAsync(ct);

        DateTime anchorCreated;
        long anchorId;
        await using (var cmd = conn.Command(
            "SELECT CreatedAtUtc, Id FROM dbo.HistoryEntry WHERE UserId = @userId AND GatewayImageId = @img;"))
        {
            cmd.AddParam("@userId", userId);
            cmd.AddParam("@img", gatewayImageId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return new HistoryNeighbors(null, null);
            anchorCreated = reader.GetDateTime(0);
            anchorId = reader.GetInt64(1);
        }

        // The single-row limit goes through the dialect, and must stay a SERVER-side limit either way: without it the
        // engine sorts the user's whole history to hand back one id.
        var newer = await NeighborAsync(conn, userId, anchorCreated, anchorId, newerSide: true, ct);
        var older = await NeighborAsync(conn, userId, anchorCreated, anchorId, newerSide: false, ct);
        return new HistoryNeighbors(newer, older);
    }

    /// <summary>One side of <see cref="GetNeighborsAsync"/>: the nearest entry after (or before) the anchor, or null.</summary>
    private async Task<string?> NeighborAsync(
        DbConnection conn, long userId, DateTime anchorCreated, long anchorId, bool newerSide, CancellationToken ct)
    {
        var (cmp, dir) = newerSide ? (">", "ASC") : ("<", "DESC");
        await using var cmd = conn.Command(
            $"SELECT {_dialect.TopPrefix("@take")}GatewayImageId FROM dbo.HistoryEntry WHERE UserId = @userId " +
            $"  AND (CreatedAtUtc {cmp} @c OR (CreatedAtUtc = @c AND Id {cmp} @i)) " +
            $"ORDER BY CreatedAtUtc {dir}, Id {dir}{_dialect.TopSuffix("@take")};");
        cmd.AddParam("@take", 1);
        cmd.AddParam("@userId", userId);
        cmd.AddParam("@c", anchorCreated);
        cmd.AddParam("@i", anchorId);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task<bool> AddAsync(HistoryEntry entry, CancellationToken ct)
    {
        // Provision the key BEFORE the transaction: the cipher writes on its own connection the first time a
        // user encrypts anything, and SQLite allows one writer -- doing it inside would deadlock against us.
        await _cipher.EnsureKeyAsync(entry.UserId, ct);
        await using var conn = await _connectionFactory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var inserted = await InsertEntryAsync(conn, tx, entry, ct);
        await tx.CommitAsync(ct);
        return inserted;
    }

    private async Task<bool> InsertEntryAsync(
        DbConnection conn, DbTransaction tx, HistoryEntry e, CancellationToken ct)
    {
        // A NULL identity means the (UserId, GatewayImageId) row was already there, which is what makes a repeated
        // write a no-op rather than a duplicate. See ISqlDialect.InsertedIdentityOrNull.
        var sql = $@"
INSERT INTO dbo.HistoryEntry
    (UserId, GatewayImageId, Prompt, RawPrompt, RawNegativePrompt, OriginalPrompt, ModelFriendly, ModelId, Aspect, CreatedAtUtc)
SELECT @userId, @img, @prompt, @rawPrompt, @rawNegative, @original, @modelFriendly, @modelId, @aspect, @created
WHERE NOT EXISTS (SELECT 1 FROM dbo.HistoryEntry WHERE UserId = @userId AND GatewayImageId = @img);
{_dialect.InsertedIdentityOrNull}";

        long? newId;
        await using (var cmd = conn.Command(sql, tx))
        {
            cmd.AddParam("@userId", e.UserId);
            cmd.AddParam("@img", e.GatewayImageId);
            cmd.AddParam("@prompt", await _cipher.EncryptAsync(e.UserId, e.Prompt, ct));
            cmd.AddParam("@rawPrompt", (object?)await _cipher.EncryptNullableAsync(e.UserId, e.RawPrompt, ct) ?? DBNull.Value);
            cmd.AddParam("@rawNegative", (object?)await _cipher.EncryptNullableAsync(e.UserId, e.RawNegativePrompt, ct) ?? DBNull.Value);
            cmd.AddParam("@original", (object?)await _cipher.EncryptNullableAsync(e.UserId, e.OriginalPrompt, ct) ?? DBNull.Value);
            cmd.AddParam("@modelFriendly", e.ModelFriendly);
            cmd.AddParam("@modelId", e.ModelId);
            cmd.AddParam("@aspect", e.Aspect);
            cmd.AddParam("@created", e.CreatedAtUtc);
            newId = await cmd.ScalarNullableInt64Async(ct);
        }

        if (newId is null)
            return false;   // duplicate — (UserId, GatewayImageId) already present

        await MarkIo.InsertAsync(conn, tx, MarkTable, MarkParent, newId.Value, e.Marks, e.UserId, _cipher, ct);
        await LoraIo.InsertAsync(conn, tx, LoraTable, LoraParent, newId.Value, e.Loras, e.UserId, _cipher, ct);
        return true;
    }

    private static void AddFilterParams(DbCommand cmd, long userId, string? artistEnc, string? tagEnc, string? model = null)
    {
        cmd.AddParam("@userId", userId);
        if (artistEnc is not null)
            cmd.AddParam("@artist", artistEnc);
        if (tagEnc is not null)
            cmd.AddParam("@tag", tagEnc);
        if (model is not null)
            cmd.AddParam("@model", model);
    }

    /// <summary>A raw (still-encrypted token, image id) row buffered before deferred decryption.</summary>
    private readonly record struct TokenImageRow(string Token, string ImageId);

    private static string Prefixed(string columns, string alias) =>
        string.Join(", ", columns.Split(", ").Select(c => $"{alias}.{c}"));

    private static HistoryEntry MapEntry(DbDataReader r) => new()
    {
        Id = r.GetInt64(0),
        UserId = r.GetInt64(1),
        GatewayImageId = r.GetString(2),
        Prompt = r.GetString(3),   // still ciphertext here; decrypted in WithMarksAsync
        ModelFriendly = r.GetString(4),
        ModelId = r.GetString(5),
        Aspect = r.GetString(6),
        CreatedAtUtc = DateTime.SpecifyKind(r.GetDateTime(7), DateTimeKind.Utc),
        RawPrompt = r.IsDBNull(8) ? null : r.GetString(8),           // ciphertext; null on rows older than the column
        RawNegativePrompt = r.IsDBNull(9) ? null : r.GetString(9),   // ciphertext; null when no negative was submitted
        OriginalPrompt = r.IsDBNull(10) ? null : r.GetString(10),    // ciphertext; null on rows the client never sent one for
    };

    private async Task<HistoryEntry> WithChildrenAsync(
        HistoryEntry e, IReadOnlyDictionary<long, List<Mark>> marks,
        IReadOnlyDictionary<long, List<HistoryLora>> loras, CancellationToken ct) => new()
    {
        Id = e.Id,
        UserId = e.UserId,
        GatewayImageId = e.GatewayImageId,
        Prompt = await _cipher.DecryptAsync(e.UserId, e.Prompt, ct),
        RawPrompt = await _cipher.DecryptNullableAsync(e.UserId, e.RawPrompt, ct),
        RawNegativePrompt = await _cipher.DecryptNullableAsync(e.UserId, e.RawNegativePrompt, ct),
        OriginalPrompt = await _cipher.DecryptNullableAsync(e.UserId, e.OriginalPrompt, ct),
        ModelFriendly = e.ModelFriendly,
        ModelId = e.ModelId,
        Aspect = e.Aspect,
        CreatedAtUtc = e.CreatedAtUtc,
        Marks = marks.TryGetValue(e.Id, out var list) ? list : [],
        Loras = loras.TryGetValue(e.Id, out var loraList) ? loraList : [],
    };
}
