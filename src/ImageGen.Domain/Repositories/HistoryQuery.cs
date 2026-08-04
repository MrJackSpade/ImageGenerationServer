//TODO: CHECK FOR FALLBACKS
namespace ImageGen.Domain.Repositories;

/// <summary>
/// A request for one page of a user's generation history, newest first. When <see cref="Artist"/> or
/// <see cref="Tag"/> is supplied, only entries whose marks include that token are returned; <see cref="Model"/>
/// filters to a single workflow configuration. The optional filters are independent and may be combined.
/// </summary>
/// <param name="UserId">The owning user whose history is queried.</param>
/// <param name="Page">1-based page number.</param>
/// <param name="PageSize">Maximum entries per page.</param>
/// <param name="Artist">When set, restrict to entries marked with this artist token.</param>
/// <param name="Tag">When set, restrict to entries marked with this tag token.</param>
/// <param name="Model">When set, restrict to entries produced by this configuration id.</param>
/// <param name="Search">
/// When set, restrict to entries whose prompt contains EVERY whitespace-separated term of it (case-insensitive
/// substring, underscores folded to spaces). Unlike the others this one cannot be a SQL predicate — the prompt is
/// randomized-encrypted at rest — so it is applied after decryption; see <c>PromptSearch</c>.
/// </param>
/// <param name="UnviewedOnly">
/// When true, restrict to entries this user has never opened. Unviewed is the ABSENCE of an <c>ImageView</c> row, so
/// this is an anti-join, not a flag test. It belongs in the query rather than being applied to a page that has
/// already come back: the grid pages in as you scroll, so filtering afterwards would give short pages, a wrong total,
/// and a scroll that stalls whenever a full page happens to be entirely viewed.
/// </param>
public sealed record HistoryQuery(
    long UserId,
    int Page,
    int PageSize,
    string? Artist = null,
    string? Tag = null,
    string? Model = null,
    string? Search = null,
    bool UnviewedOnly = false);
