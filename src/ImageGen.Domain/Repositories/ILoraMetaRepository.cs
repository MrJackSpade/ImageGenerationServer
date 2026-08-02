using ImageGen.Domain.Entities;

namespace ImageGen.Domain.Repositories;

/// <summary>The machine-level CivitAI cache for LoRA files (keyed by the plain subfolder-qualified filename).</summary>
public interface ILoraMetaRepository
{
    /// <summary>Cached CivitAI metadata for the given LoRA names — only those already fetched.</summary>
    Task<IReadOnlyDictionary<string, LoraMeta>> GetManyAsync(IReadOnlyCollection<string> loraNames, CancellationToken ct);

    /// <summary>Insert or replace the cached metadata for one LoRA file.</summary>
    Task UpsertAsync(LoraMeta meta, CancellationToken ct);
}
