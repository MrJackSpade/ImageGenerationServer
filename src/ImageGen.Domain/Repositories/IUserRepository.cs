//TODO: CHECK FOR FALLBACKS
using ImageGen.Domain.Entities;

namespace ImageGen.Domain.Repositories;

public interface IUserRepository
{
    Task<User?> GetByIdAsync(long id, CancellationToken ct);

    Task<User?> GetByUsernameAsync(string username, CancellationToken ct);

    /// <summary>
    /// Whether any account exists at all. False means a fresh install, where a sign-in form is a dead end: there
    /// is nothing to sign in to and no way to tell that apart from a forgotten password.
    /// </summary>
    Task<bool> AnyExistAsync(CancellationToken ct);

    /// <summary>Look up a user by their bearer API key (the AppUser.ApiKey GUID). Null if no user has that key.</summary>
    Task<User?> GetByApiKeyAsync(string apiKey, CancellationToken ct);

    /// <summary>
    /// Insert a new user. Returns the persisted user (with its surrogate <see cref="User.Id"/>), or
    /// null if the username is already taken.
    /// </summary>
    Task<User?> CreateAsync(User user, CancellationToken ct);

    /// <summary>Set (or clear, when null) a user's opaque composer-state JSON blob.</summary>
    Task UpdateComposerPrefsAsync(long userId, string? prefsJson, CancellationToken ct);

    /// <summary>Set (or clear, when null) a user's opaque editor-state JSON blob (encrypted at rest).</summary>
    Task UpdateEditPrefsAsync(long userId, string? prefsJson, CancellationToken ct);

    /// <summary>Set the bookmarks page's opaque state blob (which sections are folded), or clear it with null.</summary>
    Task UpdateBookmarkPrefsAsync(long userId, string? prefsJson, CancellationToken ct);

    /// <summary>The user's workflow relations — starred, hidden, and their own labels. Read on its own rather than
    /// with the user: every authenticated request loads a user, and almost none of them want these.</summary>
    Task<UserWorkflowPrefs> GetWorkflowPrefsAsync(long userId, CancellationToken ct);

    /// <summary>Replace the set of workflows this user has starred.</summary>
    Task SetFavoriteWorkflowsAsync(long userId, IReadOnlyList<string> workflowIds, CancellationToken ct);

    /// <summary>Replace the user's per-workflow labels. The labels are the user's own words and are encrypted.</summary>
    Task SetWorkflowTagsAsync(long userId, IReadOnlyDictionary<string, IReadOnlyList<string>> tags, CancellationToken ct);

    /// <summary>Replace the set of workflows this user has hidden from the UI picker.</summary>
    Task SetHiddenWorkflowsAsync(long userId, IReadOnlyList<string> workflowIds, CancellationToken ct);

    /// <summary>Replace the set of workflows this user has hidden from the API workflow list.</summary>
    Task SetHiddenApiWorkflowsAsync(long userId, IReadOnlyList<string> workflowIds, CancellationToken ct);

    /// <summary>Set (or clear, when null = unset/default) a user's generation mask: the JSON array of tag-type names
    /// the tag model may generate (stored plain, validated by the caller).</summary>
    Task UpdateGenerationTagTypesAsync(long userId, string? typesJson, CancellationToken ct);
}
