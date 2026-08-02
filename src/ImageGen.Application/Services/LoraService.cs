using ImageGen.Domain.Repositories;
using ImageGen.Domain.Entities;

namespace ImageGen.Application.Services;

/// <summary>
/// Per-user LoRA cover images: the picture that represents a LoRA in the picker grid. A cover is the user's manual
/// pick (<see cref="ILoraDisplayRepository"/>) — one of their own generations. Everything is per-user, so one
/// user's pick is never visible to another. Mirrors <see cref="ArtistService"/>'s display-image handling; there is
/// no latest-generation fallback (a LoRA without a set cover simply shows a placeholder in the picker).
/// </summary>
public sealed class LoraService(ILoraDisplayRepository displays, IHistoryRepository history)
{
    private readonly ILoraDisplayRepository _displays = displays;
    private readonly IHistoryRepository _history = history;

    /// <summary>The cover image ids the user has set for the given LoRA names — only those with a pick.</summary>
    public Task<IReadOnlyDictionary<string, string>> GetCoversAsync(
        long userId, IReadOnlyCollection<string> loraNames, CancellationToken ct) =>
        _displays.GetManyAsync(userId, loraNames, ct);

    /// <summary>Set the user's cover image for a LoRA. Returns false if the image isn't in the user's history.</summary>
    public async Task<bool> SetAsync(long userId, string loraName, string gatewayImageId, DateTime nowUtc, CancellationToken ct)
    {
        var entry = await _history.GetByGatewayImageIdAsync(userId, gatewayImageId, ct);
        if (entry is null)
            return false;

        await _displays.SetAsync(new LoraDisplay
        {
            UserId = userId,
            LoraName = loraName,
            GatewayImageId = gatewayImageId,
            SetAtUtc = nowUtc,
        }, ct);
        return true;
    }

    /// <summary>Clear the user's cover image for a LoRA.</summary>
    public Task ClearAsync(long userId, string loraName, CancellationToken ct) =>
        _displays.DeleteAsync(userId, loraName, ct);
}
