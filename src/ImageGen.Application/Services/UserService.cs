using ImageGen.Application.Security;
using ImageGen.Application.Tags;
using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;

namespace ImageGen.Application.Services;

/// <summary>Registration and credential verification for local accounts.</summary>
public sealed class UserService(IUserRepository users, TimeProvider clock)
{
    private readonly IUserRepository _users = users;
    private readonly TimeProvider _clock = clock;

    public Task<User?> GetByIdAsync(long id, CancellationToken ct) => _users.GetByIdAsync(id, ct);

    /// <summary>Whether this box has any account at all. False means a fresh install with nothing to sign in to.</summary>
    public Task<bool> AnyExistAsync(CancellationToken ct) => _users.AnyExistAsync(ct);

    /// <summary>Set (or clear, when blank) the user's opaque composer-state JSON blob.</summary>
    public Task SetComposerPrefsAsync(long userId, string? prefsJson, CancellationToken ct) =>
        _users.UpdateComposerPrefsAsync(userId, string.IsNullOrWhiteSpace(prefsJson) ? null : prefsJson, ct);

    /// <summary>Set (or clear, when blank) the user's opaque editor-state JSON blob.</summary>
    public Task SetEditPrefsAsync(long userId, string? prefsJson, CancellationToken ct) =>
        _users.UpdateEditPrefsAsync(userId, string.IsNullOrWhiteSpace(prefsJson) ? null : prefsJson, ct);

    /// <summary>Set (or clear, when blank) the bookmarks page's opaque state JSON blob.</summary>
    public Task SetBookmarkPrefsAsync(long userId, string? prefsJson, CancellationToken ct) =>
        _users.UpdateBookmarkPrefsAsync(userId, string.IsNullOrWhiteSpace(prefsJson) ? null : prefsJson, ct);

    /// <summary>The user's workflow relations: starred, hidden, and their own per-workflow labels.</summary>
    public Task<UserWorkflowPrefs> GetWorkflowPrefsAsync(long userId, CancellationToken ct) =>
        _users.GetWorkflowPrefsAsync(userId, ct);

    /// <summary>Replace the set of workflows the user has starred. An empty list is a real choice (star nothing).</summary>
    public Task SetFavoriteWorkflowsAsync(long userId, IReadOnlyList<string>? workflowIds, CancellationToken ct) =>
        _users.SetFavoriteWorkflowsAsync(userId, workflowIds ?? [], ct);

    /// <summary>Replace the user's per-workflow tag delta: the labels they added on top of the base tags, and the base
    /// tags they removed. Both halves are encrypted at rest. An empty map on either half is a real choice.</summary>
    public Task SetWorkflowTagsAsync(
        long userId,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? added,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? removed,
        CancellationToken ct) =>
        _users.SetWorkflowTagsAsync(
            userId,
            added ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
            removed ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
            ct);

    /// <summary>Replace the set of workflows the user has hidden from the UI picker.</summary>
    public Task SetHiddenWorkflowsAsync(long userId, IReadOnlyList<string>? workflowIds, CancellationToken ct) =>
        _users.SetHiddenWorkflowsAsync(userId, workflowIds ?? [], ct);

    /// <summary>Replace the set of workflows the user has hidden from the API workflow list.</summary>
    public Task SetHiddenApiWorkflowsAsync(long userId, IReadOnlyList<string>? workflowIds, CancellationToken ct) =>
        _users.SetHiddenApiWorkflowsAsync(userId, workflowIds ?? [], ct);

    /// <summary>Set the user's generation mask — which tag types the model may generate. Returns null on success, or
    /// the reason the selection was rejected (nothing is written then). Stored EXPLICITLY: an empty selection is a real
    /// choice ("none of them"), so it is saved as <c>[]</c> rather than collapsing to null, which means the default.</summary>
    public async Task<string?> SetGenerationTagTypesAsync(long userId, IReadOnlyList<string>? types, CancellationToken ct)
    {
        if (!GenerationTagTypes.TryNormalize(types, out IReadOnlyList<string>? normalized, out string? error))
        {
            return error;
        }

        await _users.UpdateGenerationTagTypesAsync(userId, GenerationTagTypes.Serialize(normalized), ct);
        return null;
    }

    /// <summary>Set whether the '#'/'@' autocomplete pins the user's matching bookmarked tags/artists to the top.</summary>
    public Task SetPinBookmarkSuggestionsAsync(long userId, bool pin, CancellationToken ct) =>
        _users.UpdatePinBookmarkSuggestionsAsync(userId, pin, ct);

    /// <summary>Set (or clear, when blank) the user's opaque per-workflow parameter-visibility override blob.</summary>
    public Task SetParamVisibilityPrefsAsync(long userId, string? prefsJson, CancellationToken ct) =>
        _users.UpdateParamVisibilityPrefsAsync(userId, string.IsNullOrWhiteSpace(prefsJson) ? null : prefsJson, ct);

    /// <summary>Create a new account. Returns null if the username is already taken.</summary>
    public Task<User?> RegisterAsync(string username, string password, string displayName, CancellationToken ct)
    {
        User user = new()
        {
            Username = username,
            PasswordHash = PasswordHasher.Hash(password),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? username : displayName,
            CreatedAtUtc = _clock.GetUtcNow().UtcDateTime,
        };
        return _users.CreateAsync(user, ct);
    }

    /// <summary>Verify credentials. Returns the user on success, null on unknown user or bad password.</summary>
    public async Task<User?> AuthenticateAsync(string username, string password, CancellationToken ct)
    {
        User? user = await _users.GetByUsernameAsync(username, ct);
        if (user is null || !PasswordHasher.Verify(password, user.PasswordHash))
        {
            return null;
        }

        return user;
    }
}
