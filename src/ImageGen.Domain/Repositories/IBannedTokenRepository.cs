//TODO: CHECK FOR FALLBACKS
using ImageGen.Domain.Entities;

namespace ImageGen.Domain.Repositories;

public interface IBannedTokenRepository
{
    /// <summary>Every banned token for a user, across all models (for the settings manager).</summary>
    Task<IReadOnlyList<BannedToken>> GetAllAsync(long userId, CancellationToken ct);

    /// <summary>Banned tokens for one model (for the detail view and the generate request).</summary>
    Task<IReadOnlyList<BannedToken>> GetForModelAsync(long userId, string modelId, CancellationToken ct);

    /// <summary>Insert a ban. Returns false if (UserId, ModelId, Name, Kind) already exists.</summary>
    Task<bool> AddAsync(BannedToken ban, CancellationToken ct);

    /// <summary>Remove a ban by its unique key. Returns false if no matching ban existed.</summary>
    Task<bool> RemoveAsync(BannedTokenKey key, CancellationToken ct);
}
