//TODO: CHECK FOR FALLBACKS
namespace ImageGen.Domain.Entities;

/// <summary>
/// A registered user, identified by a unique username and authenticated by a locally-stored password
/// hash. Owns all history and bookmarks.
/// <para>The user's favourited workflows, hidden workflows and custom workflow tags used to be three JSON blobs
/// here. They are RELATIONS — user × workflow, and user × workflow × tag — and are stored as such now; see
/// <see cref="UserWorkflowPrefs"/>. They are deliberately not loaded with the user either: every authenticated
/// request reads this record, and they are wanted on the settings path only.</para>
/// </summary>
public sealed class User
{
    /// <summary>Database surrogate key. 0 for a not-yet-persisted user.</summary>
    public long Id { get; init; }

    /// <summary>Unique login name (case-insensitive uniqueness enforced by the DB).</summary>
    public required string Username { get; init; }

    /// <summary>Self-contained PBKDF2 hash string (algorithm + iterations + salt + hash).</summary>
    public required string PasswordHash { get; init; }

    public required string DisplayName { get; init; }

    public required DateTime CreatedAtUtc { get; init; }

    /// <summary>Opaque JSON of the user's composer state (draft prompt, model, aspect, random-artist toggle, and the
    /// random-prompt temperature), so the composer follows them across devices. Stored and returned verbatim; the
    /// server never parses it. Null = unset.</summary>
    public string? ComposerPrefs { get; init; }

    /// <summary>Opaque JSON of the user's editor state (active mode/tab, selected edit workflow(s), inpaint workflow,
    /// a flat by-name param-override map shared across workflows, and brush size), so the whole editor follows them
    /// across devices — the edit-page analogue of <see cref="ComposerPrefs"/>. Stored and returned verbatim (the
    /// server never parses it), <b>encrypted</b> at rest with the user cipher. Null = unset (editor uses defaults).</summary>
    public string? EditPrefs { get; init; }

    /// <summary>Opaque JSON of the bookmarks page's state — which sections the user has folded away — so the page
    /// looks the same on every device. Stored and returned verbatim (the server never parses it), <b>encrypted</b> at
    /// rest with the user cipher, because the keys carry the user's own category names. Null = nothing folded.</summary>
    public string? BookmarkPrefs { get; init; }

    /// <summary>The user's generation mask as a JSON array of tag-type names (<c>["character","copyright","meta"]</c>):
    /// which tag types the model may emit when it generates a random prompt. Unlike the blobs above the server PARSES
    /// this one (the render worker sends it to the tag model), so it is validated on write. Not sensitive — stored
    /// plain. Null = unset, which resolves to the default (artists off).</summary>
    public string? GenerationTagTypes { get; init; }

    /// <summary>Bearer API key (a bare GUID) for non-browser callers — presenting it as the <c>X-Api-Key</c> /
    /// <c>Authorization: Bearer</c> header authenticates the request as this user. A secret: stored as-is (lookup is
    /// by equality) and never serialised into a user-facing response. Null = no key (cookie login only).</summary>
    public string? ApiKey { get; init; }
}
