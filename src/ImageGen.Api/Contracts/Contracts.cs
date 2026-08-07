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

/// <summary>Set a user's cover image for a LoRA (POST /api/lora/display).</summary>
public sealed record LoraDisplayRequest
{
    public required string Lora { get; init; }
    public required string Id { get; init; }
}

/// <summary>Set a user's portrait image for a tag (POST /api/tag/display).</summary>
public sealed record TagDisplayRequest
{
    public required string Tag { get; init; }
    public required string Id { get; init; }
}

/// <summary>Poll the CivitAI-cache state of the named LoRA files (POST /forge/loras/meta). A BODY, not a query string:
/// the caller asks about every file currently on the page — many long subfolder-qualified names — which would overrun
/// the URL, exactly like <see cref="MediaTypesRequest"/>.</summary>
public sealed record LoraMetaQueryRequest
{
    public IReadOnlyList<string>? Names { get; init; }
}

/// <summary>Re-fetch CivitAI data for the named LoRA files (POST /forge/loras/refresh), or every LoRA on the box when
/// the list is null/empty. Drops the cache for them and re-queues population.</summary>
public sealed record LoraRefreshRequest
{
    public IReadOnlyList<string>? Names { get; init; }
}

/// <summary>Set a user's LoRA preferences (POST /api/lora/settings): a trigger-word override (blank = use the CivitAI
/// default) and whether those words auto-attach to the prompt.</summary>
public sealed record LoraSettingsRequest
{
    public required string Lora { get; init; }
    public string? Triggers { get; init; }
    public bool AutoAttach { get; init; }
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
/// <param name="PinBookmarks">When true, the caller's matching bookmarked tags/artists are pinned to the top of the
/// results (deduped against the ranked suggestions). Absent/false = the results are exactly the ranked suggestions, as
/// before the toggle existed — so the per-keystroke bookmark load only happens for users who turned it on.</param>
public sealed record TagQueryRequest(
    string? Q = null, string? Kind = null, int Limit = 10, string? Ctx = null, bool PinBookmarks = false);

/// <summary>
/// A history page query. In a BODY for the same reason as <see cref="TagQueryRequest"/>: <see cref="Search"/> is
/// prompt content by another name and <see cref="Tag"/> is a tag token, and neither belongs in a URL. (An artist is
/// not protected — an artist token on its own carries nothing embarrassing — but it rides along here anyway rather
/// than splitting one query across two transports.)
/// </summary>
/// <param name="UnviewedOnly">Restrict to images this user has never opened. Applied in the query, so paging and the
/// total describe the filtered set — a client that filtered a returned page instead would report the wrong count and
/// stall its scroll on any page that happened to be entirely viewed.</param>
/// <param name="Workflow">The workflow filter, carried straight to <c>HistoryQuery.Model</c>. Absent/null = no filter
/// (the whole history); any present value — the empty string included — filters to that exact <c>ModelId</c>. The
/// client must send these distinctly: <c>null</c> for "All", <c>""</c> for the legacy empty-ModelId "Anima" group.
/// Never coerce "" to null on the way in — that overload was #188 (the option listed a count, then returned
/// everything).</param>
public sealed record HistoryQueryRequest(
    int Page = 1, int PageSize = 40, string? Artist = null, string? Tag = null, string? Workflow = null,
    string? Search = null, bool UnviewedOnly = false);

/// <summary>The ids the media-type lookup should answer about. In a BODY, not the query string: the caller asks about
/// every gateway image currently on the page at once — hundreds of 32-char ids — and that URL runs well past the ~8 KB
/// request line Kestrel accepts, so a GET would be aborted at the connection (ERR_CONNECTION_ABORTED) before any
/// handler ran — the more reliably the more thumbnails have loaded. A body has no such ceiling.</summary>
public sealed record MediaTypesRequest
{
    public IReadOnlyList<string>? Ids { get; init; }
}

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

/// <summary>Whether the '#'/'@' autocomplete pins the user's matching bookmarked tags/artists to the top. A plain
/// per-user boolean; required, so an absent field is a malformed request rather than a silent "off".</summary>
public sealed record PinBookmarksRequest
{
    public required bool PinBookmarks { get; init; }
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
