namespace ImageGen.Api.Contracts;

/// <summary>
/// Wire shapes mirror the SPA's existing localStorage records so the front-end's rendering code is
/// unchanged. Timestamps are millisecond epochs; marks is a token-&gt;("tag"|"artist") map.
/// </summary>
public sealed record HistoryRecordContract
{
    public required long Ts { get; init; }
    public required string Id { get; init; }
    public required string Prompt { get; init; }
    public required string Model { get; init; }
    public required string ModelId { get; init; }
    public required string Aspect { get; init; }
    public Dictionary<string, string>? Marks { get; init; }

    /// <summary>Whether this user has OPENED this image. The grids outline what they haven't. Default false, which is
    /// the safe direction: a caller that forgets to fill it shows an outline rather than hiding an unread image.</summary>
    public bool Viewed { get; init; }
}

/// <summary>
/// Registers a just-submitted ForgeGateway job so the server can record its result independently of the
/// browser that started it. The gateway supplies the image id / effective prompt / marks on completion;
/// this carries only what the gateway doesn't know (friendly model name, catalog id, aspect).
/// </summary>
public sealed record PendingJobContract
{
    public required string JobId { get; init; }
    public required string Prompt { get; init; }
    public required string Model { get; init; }
    public required string ModelId { get; init; }
    public required string Aspect { get; init; }
}

/// <summary>One of a user's in-flight gateway jobs, returned by GET /api/pending for cross-device progress.</summary>
public sealed record PendingJobView
{
    public required string JobId { get; init; }
    public required long Ts { get; init; }
    public required string Prompt { get; init; }
    public required string Model { get; init; }
    public required string ModelId { get; init; }
    public required string Aspect { get; init; }
}

public sealed record ImageBookmarkContract
{
    public required long Ts { get; init; }
    public required string Id { get; init; }
    public required string Prompt { get; init; }
    public required string Model { get; init; }
    public required string ModelId { get; init; }
    public required string Aspect { get; init; }
    public Dictionary<string, string>? Marks { get; init; }
    public required long SavedAt { get; init; }
}

/// <summary>Set a user's display image for an artist (POST /api/artist/display).</summary>
public sealed record ArtistDisplayRequest
{
    public required string Artist { get; init; }
    public required string Id { get; init; }
}

public sealed record TokenBookmarkRequest
{
    public required string Name { get; init; }
    public required string Kind { get; init; }
}

public sealed record PinTokenRequest
{
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public required bool Pinned { get; init; }
}

public sealed record BookmarksResponse
{
    public required IReadOnlyList<string> Artists { get; init; }
    public required IReadOnlyList<string> Tags { get; init; }
    public required IReadOnlyList<ImageBookmarkContract> Images { get; init; }
}

/// <summary>Populates the category long-press dialog: every category the user has, plus the ones the queried item is in.</summary>
public sealed record CategoriesResponse
{
    public required IReadOnlyList<string> All { get; init; }
    public required IReadOnlyList<string> Selected { get; init; }
}

/// <summary>Set an artist/tag bookmark's whole category set (creating the bookmark if it doesn't exist yet).</summary>
public sealed record SetTokenCategoriesRequest
{
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public required IReadOnlyList<string> Categories { get; init; }
}

/// <summary>Set an image bookmark's whole category set. Carries the full record so an un-saved image is created on assign.</summary>
public sealed record SetImageCategoriesRequest
{
    public required ImageBookmarkContract Image { get; init; }
    public required IReadOnlyList<string> Categories { get; init; }
}

public sealed record BanRequest
{
    public required string ModelId { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
}

/// <summary>The composer's per-user state, an opaque JSON string the server stores verbatim. Null/blank clears it.</summary>
public sealed record ComposerPrefsRequest
{
    public string? ComposerPrefs { get; init; }
}

/// <summary>The editor's per-user state, an opaque JSON string the server stores verbatim. Null/blank clears it.</summary>
public sealed record EditPrefsRequest
{
    public string? EditPrefs { get; init; }
}

/// <summary>
/// A tag/artist autocomplete query. In a BODY, never a query string: <see cref="Ctx"/> is the prompt being typed and
/// <see cref="Q"/> the fragment under the caret, and this fires per keystroke — as a URL it would write the prompt
/// into the browser's own history and address-bar autocomplete as it was typed.
/// </summary>
/// <param name="Q">The fragment being completed.</param>
/// <param name="Kind">"artist" for '@' completion; anything else means tags.</param>
/// <param name="Limit">How many suggestions to return (clamped server-side).</param>
/// <param name="Ctx">The rest of the prompt's '#' tags, for model ranking. Null when there is no context.</param>
public sealed record TagQueryRequest(string? Q, string? Kind, int? Limit, string? Ctx);

/// <summary>
/// A history page query. In a BODY for the same reason as <see cref="TagQueryRequest"/>: <see cref="Search"/> is
/// prompt content by another name and <see cref="Tag"/> is a tag token, and neither belongs in a URL. (An artist is
/// not protected — an artist token on its own carries nothing embarrassing — but it rides along here anyway rather
/// than splitting one query across two transports.)
/// </summary>
/// <param name="UnviewedOnly">Restrict to images this user has never opened. Applied in the query, so paging and the
/// total describe the filtered set — a client that filtered a returned page instead would report the wrong count and
/// stall its scroll on any page that happened to be entirely viewed.</param>
public sealed record HistoryQueryRequest(
    int? Page, int? PageSize, string? Artist, string? Tag, string? Workflow, string? Search, bool? UnviewedOnly);

/// <summary>The bookmarks page's per-user state, an opaque JSON string the server stores verbatim. Null/blank clears it.</summary>
public sealed record BookmarkPrefsRequest
{
    public string? BookmarkPrefs { get; init; }
}

/// <summary>The workflows the user has starred. A real list, not a JSON string: these are rows now (user × workflow),
/// so the wire carries the relation rather than a blob the server would have to parse to find out what it holds. An
/// empty list is a valid choice — star nothing.</summary>
public sealed record FavoriteWorkflowsRequest
{
    public IReadOnlyList<string>? FavoriteWorkflowIds { get; init; }
}

/// <summary>The user's own labels per workflow ({ "workflowId": ["tag", …] }). A real map: user × workflow × tag is a
/// relation, and the labels (the user's words) are encrypted per row. An empty map clears them.</summary>
public sealed record WorkflowTagsRequest
{
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? CustomWorkflowTags { get; init; }
}

/// <summary>The workflows the user has hidden from the UI picker. A real list, for the same reason as
/// <see cref="FavoriteWorkflowsRequest"/>. An empty list means "hide nothing".</summary>
public sealed record HiddenWorkflowsRequest
{
    public IReadOnlyList<string>? HiddenWorkflowIds { get; init; }
}

/// <summary>The workflows the user has hidden from the API workflow list — independent of the UI-picker set in
/// <see cref="HiddenWorkflowsRequest"/>. An empty list means "hide nothing".</summary>
public sealed record HiddenApiWorkflowsRequest
{
    public IReadOnlyList<string>? HiddenApiWorkflowIds { get; init; }
}

/// <summary>The generation mask: the tag types the model may emit when it generates a random prompt. A real list (not
/// an opaque blob — the server parses and validates it); an empty list is the valid "none of them" choice.</summary>
public sealed record GenerationTagTypesRequest
{
    public IReadOnlyList<string>? GenerationTagTypes { get; init; }
}

/// <summary>A model's bans plus its id, for the Settings manager's grouped list.</summary>
public sealed record ModelBansGroup
{
    public required string ModelId { get; init; }
    public required IReadOnlyList<string> Artists { get; init; }
    public required IReadOnlyList<string> Tags { get; init; }
}

public sealed record HistoryPageResponse
{
    public required IReadOnlyList<HistoryRecordContract> Items { get; init; }
    public required int Total { get; init; }
    public required int Page { get; init; }
    public required int PageSize { get; init; }
}

/// <summary>
/// The compose page's Recent strip, already sized: every image it should show, newest first. There is no paging and no
/// window size on the wire — the server worked out how far the window had to stretch to cover the current-or-last
/// batch, and this is the answer. The client renders the list.
/// </summary>
public sealed record RecentsResponse
{
    public required IReadOnlyList<HistoryRecordContract> Items { get; init; }
}
