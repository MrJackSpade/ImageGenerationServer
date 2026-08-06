using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Application.Rendering;

/// <summary>
/// One slot's lifecycle. <see cref="Running"/> means ONE thing — the GPU is generating this image right now — and it
/// is entered only on the backend's own report that the prompt is executing. Everything short of that (waiting for our
/// scheduler, being prepared, submitted but sitting in the backend's queue) is <see cref="Queued"/>, because from the
/// user's side those are all "waiting". The result landing — or the backend losing the prompt — makes it terminal.
/// </summary>
public enum SlotState
{
    /// <summary>Not on the GPU. Waiting for the fair scheduler, being prepared by the worker, or already handed to the
    /// backend and waiting in ITS queue — three ways of waiting, one status.</summary>
    Queued,
    /// <summary>The backend reports this exact prompt as EXECUTING: it is on the GPU. At most one slot per instance
    /// can hold this (one worker, one prompt at a time), and only <c>RenderOrchestrator.ObserveExecuting</c> writes
    /// it. It is never persisted — see <see cref="ImageGen.Domain.Entities.JobSlotState"/>.</summary>
    Running,
    /// <summary>Completed (an image was produced, or an edit intentionally declined).</summary>
    Done,
    /// <summary>Failed: something went wrong. NOT cancellation — see <see cref="Cancelled"/>.</summary>
    Error,
    /// <summary>Stopped on the user's request. Terminal like <see cref="Error"/>, but nothing went wrong: the user
    /// asked for this, and reporting it as a failure would misrepresent a deliberate stop.</summary>
    Cancelled,
}

/// <summary>
/// In-memory render slot: one image = one backend prompt = one slot of a <see cref="RenderJob"/>. Identity fields are
/// set at enqueue; the mutable render/result fields are written and read only under the orchestrator's lock. This is
/// the live working copy, mirrored to durable storage write-through on every transition.
/// </summary>
public sealed class RenderSlot
{
    /// <summary>Back-reference to the owning job.</summary>
    public required RenderJob Job { get; init; }
    /// <summary>Position in the job's image array.</summary>
    public required int Index { get; init; }
    /// <summary>The generate spec, or null for an edit slot.</summary>
    public GenerateSpec? Gen { get; init; }
    /// <summary>The edit spec, or null for a generate slot.</summary>
    public EditSpec? Edit { get; init; }
    /// <summary>True when this slot is an edit.</summary>
    public bool IsEdit => Edit is not null;

    /// <summary>True when this is a BACKGROUND (idle-time) slot: it becomes schedulable only once the queue has been
    /// foreground-idle for the configured delay, and a foreground submission preempts it (halting and requeuing it,
    /// never failing it). Persisted (dbo.JobSlot.IsBackground) so it survives a restart and re-gates the slot.</summary>
    public bool IsBackground { get; init; }

    /// <summary>The edit spec, guaranteed non-null. Throws if this is a generate slot — the discriminant
    /// (<see cref="IsEdit"/>) already tells the caller which spec is present, so reaching the wrong one is a bug.</summary>
    [AllowMagicStrings("exception message")]
    public EditSpec RequireEdit() => Edit ?? throw new InvalidOperationException("Slot is not an edit slot.");

    /// <summary>The generate spec, guaranteed non-null. Throws if this is an edit slot.</summary>
    [AllowMagicStrings("exception message")]
    public GenerateSpec RequireGen() => Gen ?? throw new InvalidOperationException("Slot is not a generate slot.");

    /// <summary>Current lifecycle state.</summary>
    public SlotState State = SlotState.Queued;
    /// <summary>Set when a cancel has been requested for the running slot.</summary>
    public bool CancelRequested;
    /// <summary>Set when a foreground submission has preempted this running BACKGROUND slot. Unlike
    /// <see cref="CancelRequested"/> it is NON-terminal: the worker stops the in-flight render, clears the slot's
    /// submission state, and returns it to the queue to render fresh on the next idle window — it is never marked
    /// Cancelled/Error. Only ever set on a background slot.</summary>
    public bool PreemptRequested;
    /// <summary>The backend's upstream prompt id — internal; the liveness key, never exposed.</summary>
    public string? ComfyPromptId;
    /// <summary>The produced image id, once it lands.</summary>
    public string? ImageId;
    /// <summary>Produced image width.</summary>
    public int Width;
    /// <summary>Produced image height.</summary>
    public int Height;
    /// <summary>The edit outcome — present iff this is an edit slot (mirrors <see cref="IsEdit"/> / <see cref="Edit"/>),
    /// null for a generate. A generate always produces a fresh image, so "did it change" and the pHash distance are
    /// meaningless for it; the old flat "null = not an edit" <c>ChangeScore</c> is replaced by this group's presence
    /// (audit #125 A1).</summary>
    public EditResult? EditResult;
    /// <summary>Failure reason when State == Error.</summary>
    public string? Error;
    /// <summary>A non-fatal, user-facing notice set at enqueue when an input was normalized to a valid value.</summary>
    public string? Notice;
    /// <summary>The prompt actually rendered (markers stripped + any random artist).</summary>
    public string? EffectivePrompt;
    /// <summary>The prompt VERBATIM in marker form ("#tag, @artist"), random injections included — the string that,
    /// resubmitted, remakes this image. Finalizing it yields <see cref="EffectivePrompt"/> and <see cref="Marks"/>.</summary>
    public string? RawPrompt;
    /// <summary>The NEGATIVE verbatim in marker form; null when none was submitted (the model's default stands alone).</summary>
    public string? RawNegativePrompt;
    /// <summary>{ canonicalName -&gt; "tag"|"artist" } for the produced image.</summary>
    public Dictionary<string, string>? Marks;
    /// <summary>When the render started (submit time; excludes queue wait).</summary>
    [AllowNullable("null = render not yet started; default(DateTimeOffset) would falsely read as started in year 1")]
    public DateTimeOffset? GenStartedAt;
    /// <summary>Expected render seconds for the model on this machine (the ETA), or null the first time.</summary>
    [AllowNullable("null = no ETA yet (first render of this model on this machine); 0.0 would be a real (instant) estimate")]
    public double? ExpectedGenSeconds;

    /// <summary>The ETA parameter signature (resolved resolution / steps / frames) captured at submit — stored with the
    /// timing sample and used to param-match this render's ETA. Null until the prompt is submitted.</summary>
    [AllowNullable("null = prompt not yet submitted, so no signature captured; distinct from any default signature")]
    public ImageGen.Domain.Repositories.EtaSignature? EtaSignature;

    /// <summary>Consecutive reconcile passes in which the backend did not list this slot's prompt while no result had
    /// landed — the liveness debounce before declaring the prompt lost.</summary>
    public int MissedLivenessChecks;

    /// <summary>True once the slot has resolved (Done, Error or Cancelled).</summary>
    public bool Terminal => State is SlotState.Done or SlotState.Error or SlotState.Cancelled;

    /// <summary>True once the backend has accepted this slot's prompt — submitted, though not necessarily executing
    /// yet. What separates "there is a render out there to interrupt" from "the worker is still building the prompt".</summary>
    public bool Submitted => ComfyPromptId is not null;

    /// <summary>The workflow configuration id for this slot.</summary>
    public string Model => IsEdit ? RequireEdit().Workflow : RequireGen().Workflow;
}

/// <summary>
/// In-memory render job: the unit a user submits together (a lone generate/edit = a 1-slot job; a batch = an N-slot
/// job). A live projection of the backend's state — the owning instance advances its slots and finalizes the job once
/// every slot is terminal, after which it leaves the active feed. Mirrored to durable storage write-through.
/// </summary>
public sealed class RenderJob
{
    /// <summary>Unique job id.</summary>
    public required string JobId { get; init; }
    /// <summary>The authenticated user who owns this job.</summary>
    public required long Owner { get; init; }
    /// <summary>Owning instance — only it reconciles/finalizes this job.</summary>
    public required string MachineName { get; init; }
    /// <summary>When the job was enqueued.</summary>
    public required DateTimeOffset CreatedAt { get; init; }
    /// <summary>The job's ordered slots.</summary>
    public List<RenderSlot> Slots { get; } = [];

    /// <summary>Set when finalized (all slots terminal).</summary>
    [AllowNullable("null = not finalized (slots still non-terminal); default(DateTimeOffset) would falsely read as finished in year 1")]
    public DateTimeOffset? FinishedAt;

    /// <summary>Number of slots/images in the job.</summary>
    public int Total => Slots.Count;
    /// <summary>Images resolved (done or failed).</summary>
    public int Progress => Slots.Count(s => s.Terminal);
    /// <summary>Images that actually exist.</summary>
    public int Produced => Slots.Count(s => s.ImageId is not null);
    /// <summary>True once every slot is terminal.</summary>
    public bool AllTerminal => Slots.All(s => s.Terminal);
    /// <summary>True iff one of this job's slots is on the GPU right now. At most one job per instance can be — the
    /// worker submits and polls a single slot at a time — so this is what "running" may be reported from, and the
    /// only thing it may be reported from. A job with finished slots and waiting ones is WAITING, not running.</summary>
    public bool IsRendering => Slots.Exists(s => s.State == SlotState.Running);

    /// <summary>Whether the job is an edit (read from slot 0; a batch is homogeneous in practice).</summary>
    public bool IsEdit => Slots.Count > 0 && Slots[0].IsEdit;
    /// <summary>Whether the job is background (idle-time) work (read from slot 0; a batch is homogeneous).</summary>
    public bool IsBackground => Slots.Count > 0 && Slots[0].IsBackground;
    /// <summary>The job's configuration id (from slot 0).</summary>
    public string Model => Slots.Count > 0 ? Slots[0].Model : "";
    /// <summary>The job's summary prompt/instruction (from slot 0).</summary>
    public string Prompt => Slots.Count == 0 ? "" : (Slots[0].IsEdit ? Slots[0].RequireEdit().Instruction : Slots[0].RequireGen().Prompt);

    /// <summary>The positional image-id array the client diffs: <c>imageIds[i]</c> is slot i's produced image (or null
    /// until it lands / if it failed).</summary>
    public List<string?> ImageIds() => Slots.OrderBy(s => s.Index).Select(s => s.ImageId).ToList();
}

/// <summary>The mutable outcome of an edit render: whether the model changed the image, and the pHash distance from the
/// source (null until scored). Hung on a <see cref="RenderSlot"/> exactly when it is an edit slot.</summary>
public sealed class EditResult
{
    /// <summary>False when the model declined (no new image).</summary>
    public bool Changed = true;
    /// <summary>pHash distance from the source; null until computed.</summary>
    [AllowNullable("null = distance not computed yet; 0.0 is a real identical-image score")]
    public double? ChangeScore;
}
