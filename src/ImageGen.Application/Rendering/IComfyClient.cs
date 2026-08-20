using ImageGen.Domain;
using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;
using System.Net.WebSockets;
using System.Text.Json;

namespace ImageGen.Application.Rendering;

/// <summary>A submitted prompt: the backend id to poll, plus the ETA signature captured from the merged render
/// params (resolved resolution / steps / frames) so the orchestrator can param-match its ETA and store it with the
/// timing sample, the exact positive prompt embedded in the submitted graph, and the resolved model-file manifest.
/// All are built where the merged params, bindings, and workflow prompt template live (the Comfy adapter), not
/// re-derived in core.</summary>
public readonly record struct SubmitResult(
    string PromptId,
    EtaSignature Eta,
    string ModelPrompt,
    RenderModelManifest? ModelManifest = null,
    RenderDimensions? Dimensions = null);

/// <summary>One reference the orchestrator resolved for an edit, ready to upload: the raw bytes, the media
/// <see cref="Kind"/> derived from the stored blob's content type, and that <see cref="ContentType"/> itself. The
/// adapter uploads each under a kind-appropriate filename WITH its real content type and routes it to the workflow's
/// matching graph input.</summary>
public readonly record struct ReferenceUpload(byte[] Bytes, ReferenceKind Kind, string ContentType);

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
        IReadOnlyList<ReferenceUpload>? references, IReadOnlyDictionary<string, JsonElement>? overrides,
        byte[]? maskPng, byte[]? lastFramePng, CancellationToken ct);

    /// <summary>One non-looping poll of a prompt's result. Not-ready and renderer-unavailable are distinct so a
    /// transport outage is never used as evidence that an accepted prompt vanished. Throws
    /// <see cref="RenderValidationException"/> if the backend reported a terminal execution error.</summary>
    Task<RenderPollResult> PollResultAsync(string promptId, CancellationToken ct);

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

    /// <summary>Fetch raw bytes for a legacy image id (a backend view-ref minted before DB storage), distinguishing a
    /// definitive missing artifact from a renderer that could not answer.</summary>
    Task<LegacyImageFetchResult> FetchLegacyImageAsync(string imageId, CancellationToken ct);

    /// <summary>Connect to the backend's live-progress WebSocket under this client's id, so the API can proxy
    /// progress/preview frames to the browser.</summary>
    Task<WebSocket> ConnectProgressSocketAsync(CancellationToken ct);

    /// <summary>Flush the cached present-files capability snapshot so the next read re-probes ComfyUI — for the LoRA
    /// refresh action, when the user knows files on disk changed. Cheap; the actual re-probe runs on the sync worker.</summary>
    void InvalidatePresentFiles();
}
