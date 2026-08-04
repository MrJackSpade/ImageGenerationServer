//TODO: CHECK FOR FALLBACKS
namespace ImageGen.Infrastructure.Database;

/// <summary>
/// The handful of SQL fragments that genuinely have no shared spelling between SQL Server and SQLite.
///
/// <para>This is deliberately tiny. Of ~130 statements in this assembly, all but these are already portable — the
/// non-portable ones were rewritten in place (see <c>ImageDeletionRepository</c>, <c>HistoryRepository</c>,
/// <c>ArtistDisplayRepository</c>) rather than hidden behind a dialect member, because a rewrite is readable
/// afterwards and a per-provider branch is not. What is left is only where the two engines spell the same idea with
/// different words.</para>
/// </summary>
public interface ISqlDialect
{
    /// <summary>Row-limit clause appended to an already-ordered query: <c>OFFSET/FETCH</c> vs <c>LIMIT/OFFSET</c>.</summary>
    string Paginate(string skipParameter, string takeParameter);

    /// <summary>
    /// A "first N rows" limit. SQL Server puts <c>TOP (@n)</c> after <c>SELECT</c> and SQLite puts <c>LIMIT @n</c> at
    /// the end, so a caller needs both halves and one of them is always empty.
    /// </summary>
    string TopPrefix(string takeParameter);

    /// <inheritdoc cref="TopPrefix"/>
    string TopSuffix(string takeParameter);

    /// <summary>
    /// A trailing statement yielding the identity of the row a guarded <c>INSERT … WHERE NOT EXISTS</c> just created,
    /// or NULL when it matched an existing row and inserted nothing.
    ///
    /// <para>The NULL is load-bearing: it is how <c>UserRepository.CreateAsync</c>, <c>HistoryRepository</c> and
    /// <c>BookmarkRepository</c> tell "created" from "already existed". SQL Server's <c>SCOPE_IDENTITY()</c> is NULL
    /// for free. SQLite's <c>last_insert_rowid()</c> is <b>not</b> — it returns the PREVIOUS insert's id, so a naive
    /// translation silently reports a duplicate as a successful insert with someone else's id. Hence the
    /// <c>changes()</c> guard, pinned by <c>SqliteAttachSpikeTests</c>.</para>
    /// </summary>
    string InsertedIdentityOrNull { get; }

    /// <summary>Upsert for <c>dbo.Job</c>: <c>MERGE</c> vs <c>INSERT … ON CONFLICT DO UPDATE</c>.</summary>
    string UpsertJob { get; }

    /// <summary>Upsert for <c>dbo.JobSlot</c>, keyed on <c>(JobId, SlotIndex)</c>.</summary>
    string UpsertJobSlot { get; }
}
