using ImageGen.Domain.Entities;

namespace ImageGen.Application.Civitai;

/// <summary>
/// The machine's CivitAI-metadata view of its LoRA files. Returns cached metadata and, for files not yet cached (and
/// only when CivitAI lookups are enabled), hashes each and looks it up, caching the result.
/// <para>The first call for a new file HASHES it (reads the whole file), so this belongs on the LoRA manager page —
/// which the user expects to spend a moment populating — not on the composer's hot path.</para>
/// </summary>
public interface ILoraMetaCatalog
{
    Task<IReadOnlyDictionary<string, LoraMeta>> EnsureAndGetAsync(IReadOnlyList<string> loraNames, CancellationToken ct);
}
