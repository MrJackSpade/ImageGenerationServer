using ImageGen.Domain.Entities;

namespace ImageGen.Domain.Repositories;

/// <summary>Per-user LoRA preferences (trigger-word override + auto-attach).</summary>
public interface ILoraUserSettingRepository
{
    /// <summary>The user's settings for the given LoRA names — only those they've customized.</summary>
    Task<IReadOnlyDictionary<string, LoraUserSetting>> GetManyAsync(
        long userId, IReadOnlyCollection<string> loraNames, CancellationToken ct);

    /// <summary>Set (or replace) the user's preferences for a LoRA.</summary>
    Task SetAsync(LoraUserSetting setting, CancellationToken ct);
}
