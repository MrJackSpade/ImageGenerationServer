using ImageGen.Domain;
using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;

namespace ImageGen.Application.Services;

public sealed class BanService(IBannedTokenRepository bans, TimeProvider clock)
{
    private readonly IBannedTokenRepository _bans = bans;
    private readonly TimeProvider _clock = clock;

    public Task<IReadOnlyList<BannedToken>> GetAllAsync(long userId, CancellationToken ct) =>
        _bans.GetAllAsync(userId, ct);

    public Task<IReadOnlyList<BannedToken>> GetForModelAsync(long userId, string modelId, CancellationToken ct) =>
        _bans.GetForModelAsync(userId, modelId, ct);

    public Task<bool> AddAsync(long userId, string modelId, string name, TokenKind kind, CancellationToken ct) =>
        _bans.AddAsync(
            new BannedToken { UserId = userId, ModelId = modelId, Name = name, Kind = kind, SavedAtUtc = Now() }, ct);

    public Task<bool> RemoveAsync(long userId, string modelId, string name, TokenKind kind, CancellationToken ct) =>
        _bans.RemoveAsync(new BannedTokenKey(userId, modelId, name, kind), ct);

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;
}
