//TODO: CHECK FOR FALLBACKS
using System.Net.WebSockets;
using System.Text.Json;
using ImageGen.Domain.Repositories;

namespace ImageGen.Application.Rendering;

/// <summary>A submitted prompt: the backend id to poll, plus the ETA signature captured from the merged render
/// params (resolved resolution / steps / frames) so the orchestrator can param-match its ETA and store it with the
/// timing sample. The signature is built where the merged params live (the Comfy adapter), not re-derived in core.</summary>
public readonly record struct SubmitResult(string PromptId, EtaSignature Eta);

/// <summary>
/// The render backend port the application depends on. It is the async submit/poll/cancel surface the orchestrator
/// drives, plus the legacy view fetch and the live-progress socket the API proxies. All ComfyUI HTTP plumbing,
/// graph building, and capability probing live behind it in the Comfy adapter.
/// </summary>
public interface IComfyClient
{
    /// <summary>Build the generate graph for a configuration and POST it; return the backend prompt id (no polling).
    /// <paramref name="loras"/> is the user's LoRA stack (null/empty for none), chained through <c>LoraLoader</c>
    /// (model + CLIP) on top of any preset LoRA; each name is validated against the backend's LoRA list.</summary>
    Task<SubmitResult> SubmitGenerateAsync(string prompt, string? negativePrompt, string? configId, string? aspect,
        IReadOnlyDictionary<string, JsonElement>? overrides, IReadOnlyList<LoraSelection>? loras, CancellationToken ct);

    /// <summary>Upload the source (and any references/mask/last-frame), build the edit graph, and POST it; return the
    /// backend prompt id (no polling). <paramref name="negativePrompt"/> is the optional UI negative appended to the
    /// edit model's default (null/blank = just the default); ignored by editors that use no negative conditioning.</summary>
    Task<SubmitResult> SubmitEditAsync(byte[] sourcePng, string instruction, string? negativePrompt, string? configId,
        IReadOnlyList<byte[]>? references, IReadOnlyDictionary<string, JsonElement>? overrides,
        byte[]? maskPng, byte[]? lastFramePng, CancellationToken ct);

    /// <summary>One non-looping poll of a prompt's result: the produced image if ready, null if not yet, or throws
    /// <see cref="RenderValidationException"/> if the backend reported an error.</summary>
    Task<GeneratedImage?> PollResultAsync(string promptId, CancellationToken ct);

    /// <summary>What the backend currently holds — the prompts it is EXECUTING and the ones queued behind them — or
    /// null when it didn't answer (distinct from an empty queue). The executing set is the only thing that makes a
    /// slot "running"; the union is the liveness signal for failing an orphaned slot.</summary>
    Task<BackendQueue?> GetQueueAsync(CancellationToken ct);

    /// <summary>Interrupt whatever prompt is currently rendering. Best-effort.</summary>
    Task InterruptAsync(CancellationToken ct);

    /// <summary>Ask the backend to unload every loaded model and release its cached VRAM. Applied between prompts, so
    /// a render already in flight is not disturbed. Throws when the backend refuses.</summary>
    Task FreeMemoryAsync(CancellationToken ct);

    /// <summary>Pre-queue parameter normalization (no backend call): snap any out-of-range input onto a valid value,
    /// returning the corrected overrides + a user-facing notice (both null when nothing changed).</summary>
    QueueNormalizationResult NormalizeForQueue(string? configId, RenderKind kind, IReadOnlyDictionary<string, JsonElement>? overrides);

    /// <summary>Fetch raw bytes for a legacy image id (a backend view-ref minted before DB storage), for the
    /// DB-first/legacy-fallback image path. Throws when the backend doesn't have it.</summary>
    Task<byte[]> FetchLegacyImageAsync(string imageId, CancellationToken ct);

    /// <summary>Connect to the backend's live-progress WebSocket under this client's id, so the API can proxy
    /// progress/preview frames to the browser.</summary>
    Task<WebSocket> ConnectProgressSocketAsync(CancellationToken ct);
}
