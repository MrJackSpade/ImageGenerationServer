using ImageGen.Domain.CodeAnalysis;
using System.Text.Json;

namespace ImageGen.Application.Rendering;

/// <summary>Whether a render is a text-to-image generation or an image edit. Selects the workflow resolution path.</summary>
public enum RenderKind
{
    /// <summary>Text-to-image generation.</summary>
    Generate,
    /// <summary>Image (+ instruction) edit.</summary>
    Edit,
}

/// <summary>
/// A produced image: the raw bytes plus the ComfyUI output reference and, for a pixel-quantize generation, the
/// derived palette (a #RRGGBB JSON array), native-resolution lossless frame PNGs, and the fp engine's pooled label
/// frequencies (a JSON float array indexed by palette order — the second global the fp quantize depends on, carried
/// so a later single-frame run can replay BOTH and reproduce the batch result exactly). <see cref="Filename"/> empty
/// means no persistent ref. A <c>.webp</c> filename marks a video clip.
/// </summary>
public sealed record GeneratedImage(
    byte[] Png,
    string Model,
    string Filename = "",
    string Subfolder = "",
    string Type = "output",
    string? PaletteJson = null,
    IReadOnlyList<byte[]>? LosslessFrames = null,
    string? FrequenciesJson = null);

/// <summary>The result of a pre-queue parameter normalization: the corrected override set (null when nothing changed)
/// and a single user-facing notice (null when nothing changed).</summary>
/// <param name="Overrides">The overrides with any snapped values folded in, or null when unchanged.</param>
/// <param name="Notice">A newline-joined human-readable notice, or null when unchanged.</param>
public sealed record QueueNormalizationResult(IReadOnlyDictionary<string, JsonElement>? Overrides, string? Notice);

/// <summary>
/// A point-in-time count of one instance's render work — what the drain probe reports.
/// <para><see cref="InFlightSlots"/> and <see cref="ExecutingSlots"/> are deliberately different questions. A drain
/// must wait out everything IN FLIGHT: the worker holds a slot from the moment it is picked, and stopping while it is
/// building a prompt, or while the backend has that prompt queued, orphans the render just as surely as stopping
/// mid-render does. Executing is the narrower fact — the GPU is generating this image — and is what a user is shown
/// as "running". Both are 0 or 1: one worker, one prompt at a time.</para>
/// </summary>
public sealed record WorkloadSnapshot(int ActiveJobs, int InFlightSlots, int ExecutingSlots, int WaitingSlots);

/// <summary>
/// Everything still to be rendered on this instance, counted under one lock so the parts agree with each other —
/// the queue page's "what's left" header.
/// <para><see cref="RunningRemainingSeconds"/> is the slot on the GPU priced from its OWN measurement (it recorded
/// an expected time and a start instant when it was submitted), so it shrinks as the render proceeds. It is null
/// when nothing is in flight, or when the in-flight slot has no estimate to count down — in the latter case that
/// slot appears in <see cref="WaitingModels"/> instead, to be priced from the workflow average like any other.</para>
/// <para><see cref="WaitingModels"/> is one entry per image still to render, naming the workflow it needs. The
/// caller prices them, because the timing averages live in a repository this type has no business reaching.</para>
/// <para><see cref="ViewerJobs"/> is how many of <see cref="Jobs"/> belong to whoever asked — enough to know whether
/// "cancel mine" has anything to do. It is deliberately NOT accompanied by a per-viewer ETA: the scheduler is fair
/// round-robin per owner, so one user's wait is not the sum of their own slots.</para>
/// </summary>
public sealed record OutstandingSnapshot(
    int Jobs, int ViewerJobs, int Images,
    [property: AllowNullable("null = nothing in flight, or the in-flight slot has no estimate to count down; 0.0 would mean \"done now\"")] double? RunningRemainingSeconds,
    IReadOnlyList<string> WaitingModels);

/// <summary>Why a requeue did or did not happen. Each value is a distinct answer the caller turns into its own
/// result — a Requeue button that quietly does nothing is worse than one that says what stopped it.</summary>
public enum RequeueStatus
{
    /// <summary>A new job was enqueued for the images that were never made.</summary>
    Requeued,
    /// <summary>No job with that id.</summary>
    UnknownJob,
    /// <summary>The job belongs to someone else.</summary>
    NotOwner,
    /// <summary>The job hasn't finished. Cancel is the thing to reach for; requeue is for what's already over.</summary>
    StillActive,
    /// <summary>Every image was made — there is nothing to redo.</summary>
    NothingMissing,
    /// <summary>The stored request can no longer produce a render. <c>Reason</c> says what's gone.</summary>
    Unrunnable,
}

/// <summary>The outcome of a requeue: the new job's id and image count on success, or why not.</summary>
public sealed record RequeueOutcome(RequeueStatus Status, string? JobId = null, int Images = 0, string? Reason = null);

/// <summary>
/// What the render backend has RIGHT NOW, as it reports it: the prompts it is EXECUTING (on the GPU) and the ones
/// waiting in its own queue behind them. The two are kept apart because they answer different questions — only
/// <see cref="Executing"/> means "an image is being generated", while liveness ("does the backend still have this
/// prompt at all?") needs their union, which <see cref="Has"/> gives. A null result — not an empty one — is how "the
/// backend did not answer" is expressed, so unreachable is never mistaken for "the prompt vanished".
/// </summary>
public sealed record BackendQueue(IReadOnlySet<string> Executing, IReadOnlySet<string> Pending)
{
    /// <summary>True while the backend still holds this prompt, executing or merely queued.</summary>
    public bool Has(string promptId) => Executing.Contains(promptId) || Pending.Contains(promptId);
}

/// <summary>The result of one non-blocking renderer-history check. <see cref="RenderPollState.Unavailable"/> is deliberately
/// separate from <see cref="RenderPollState.NotReady"/>: an unavailable history endpoint says nothing about whether the prompt is
/// still queued or has completed, so the orchestrator must not use that poll as evidence that the prompt vanished.</summary>
public enum RenderPollState
{
    NotReady,
    Ready,
    Unavailable,
}

/// <summary>One typed renderer-history observation. Backend execution failures continue to throw
/// <see cref="RenderValidationException"/> because they are terminal prompt outcomes, not polling states.</summary>
public readonly record struct RenderPollResult(RenderPollState State, GeneratedImage? Image = null)
{
    public static RenderPollResult NotReady() => new(RenderPollState.NotReady);
    public static RenderPollResult Ready(GeneratedImage image) => new(RenderPollState.Ready, image);
    public static RenderPollResult Unavailable() => new(RenderPollState.Unavailable);
}

/// <summary>What the renderer said when asked for a pre-database legacy image. Only <see cref="LegacyImageFetchState.NotFound"/> is a
/// definitive absence; <see cref="LegacyImageFetchState.Unavailable"/> is a temporary inability to answer and accepted work must wait.</summary>
public enum LegacyImageFetchState
{
    Found,
    NotFound,
    Unavailable,
}

/// <summary>A typed legacy-image lookup, keeping a missing artifact distinct from an unreachable renderer.</summary>
public readonly record struct LegacyImageFetchResult(LegacyImageFetchState State, byte[]? Bytes = null)
{
    public static LegacyImageFetchResult Found(byte[] bytes) => new(LegacyImageFetchState.Found, bytes);
    public static LegacyImageFetchResult NotFound() => new(LegacyImageFetchState.NotFound);
    public static LegacyImageFetchResult Unavailable() => new(LegacyImageFetchState.Unavailable);
}

/// <summary>A definitive miss for an input image after upload, database, and legacy-renderer lookup.</summary>
public sealed class RenderInputNotFoundException(string imageId)
    : Exception($"Render input '{imageId}' was not found.");

/// <summary>
/// Thrown for expected, user-correctable render problems (e.g. an unknown workflow configuration). Callers turn it
/// into a clean message instead of an unhandled stack trace.
/// </summary>
public sealed class RenderValidationException : Exception
{
    public RenderValidationException(string message) : base(message) { }

    /// <summary>Wraps the precise inner failure (e.g. an <c>Ensure</c> guard) behind context-specific prose.</summary>
    public RenderValidationException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Thrown when work could not be ACCEPTED because it could not be written down. The front door turns it into a clean
/// "try again shortly" refusal rather than a 500.
/// <para>This is the only correct place to give up on the database. Everything past the door waits an outage out —
/// accepted work is never discarded for one — but a submission that cannot be recorded must not be accepted at all,
/// or it renders and the record of it dies with the process.</para>
/// </summary>
public sealed class RenderStorageException : Exception
{
    public RenderStorageException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>Build the submission-boundary failure shown by the UI. The full exception is still logged, while the
    /// response carries every exception type/message in the chain — enough to expose provider errors such as a missing
    /// database column without dumping server stack frames into the page.</summary>
    [AllowMagicStrings("human-readable error formatting")]
    public static RenderStorageException Submission(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        List<string> details = [];
        for (Exception? current = failure; current is not null; current = current.InnerException)
        {
            string type = current.GetType().FullName ?? current.GetType().Name;
            string message = string.IsNullOrWhiteSpace(current.Message) ? "(no message)" : current.Message.Trim();
            details.Add($"{type}: {message}");
        }

        return new RenderStorageException(
            "The generation was not started because its database record could not be saved. "
            + string.Join(" -> ", details), failure);
    }
}

/// <summary>
/// Render-pipeline options resolved from configuration at composition.
/// <para>There is deliberately no <c>LogPrompts</c> option: it would duplicate prompt-bearing diagnostics into the
/// PLAINTEXT app log, and "off by default" plus a file sink means prompts would be one config toggle from being
/// written to disk permanently. The same content already goes to the per-user ENCRYPTED log
/// (Logging:AuditUserPrompts), which is the channel that exists for it.</para>
/// </summary>
/// <param name="BackgroundIdleDelay">
/// How long the queue must be idle of FOREGROUND work before background (idle-time) slots become schedulable. Read
/// LIVE on every scheduling decision — it is a machine setting the settings page can change while the app runs — so it
/// is a delegate, not a captured value (the same live-read shape <c>SubmissionMemoryGate</c>'s floor uses).
/// </param>
public sealed record RenderOptions(Func<TimeSpan> BackgroundIdleDelay);
