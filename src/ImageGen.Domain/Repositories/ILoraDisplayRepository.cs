using ImageGen.Domain.Entities;

namespace ImageGen.Domain.Repositories;

public interface ILoraDisplayRepository
{
    /// <summary>The user's chosen cover image for a LoRA, or null if they haven't set one.</summary>
    Task<LoraDisplay?> GetAsync(long userId, string loraName, CancellationToken ct);

    /// <summary>The chosen cover image (gateway image id) per LoRA for the given names — only those set.</summary>
    Task<IReadOnlyDictionary<string, string>> GetManyAsync(
        long userId, IReadOnlyCollection<string> loraNames, CancellationToken ct);

    /// <summary>Set (or replace) the user's cover image for a LoRA.</summary>
    Task SetAsync(LoraDisplay display, CancellationToken ct);

    /// <summary>Clear the user's cover image for a LoRA.</summary>
    Task DeleteAsync(long userId, string loraName, CancellationToken ct);
}
