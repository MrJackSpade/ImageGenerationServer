//TODO: CHECK FOR FALLBACKS
namespace ImageGen.Domain.Entities;

/// <summary>Job-level lifecycle. A job is <see cref="Active"/> while any slot is still non-terminal; once every slot
/// has resolved it is finalized and leaves the active feed — <see cref="Cancelled"/> if the user stopped it,
/// <see cref="Done"/> if anything was produced, else <see cref="Error"/>.
/// <para><see cref="Cancelled"/> outranks <see cref="Done"/> deliberately: a batch of ten stopped after three landed
/// ended because you stopped it, and that is the fact worth reporting alongside the count.</para>
/// <para>These persist as <c>tinyint</c>, so a new member is simply a value no existing row carries. Jobs cancelled
/// before this existed keep reading as <see cref="Error"/> — reclassifying them is a separate, deliberate decision
/// (their reason text does record "cancelled"), and per project convention a standalone tool, not app code.</para>
/// </summary>
public enum JobStatus : byte { Active = 0, Done = 1, Error = 2, Cancelled = 3 }

/// <summary>
/// One image slot's DURABLE lifecycle. A slot is <see cref="Queued"/> from enqueue until it resolves, whether or not
/// its prompt has been submitted (the <c>ComfyPromptId</c> beside it says that, and is what a restart resumes from).
/// <para><see cref="Cancelled"/> is a terminal state of its own, not a flavour of <see cref="Error"/>. The two were
/// one value with the difference living only in a human-readable reason string, so everything downstream reported a
/// deliberate stop as a failure. Nothing went wrong when a user cancels, and the row should not claim otherwise.</para>
/// <para><see cref="Running"/> is LEGACY and is never written any more. "Running" means the GPU is generating this
/// image right now — something only the live orchestrator can know and no row can keep true past the writing process's
/// life. Persisting it is exactly how crashed and orphaned jobs ended up claiming to render forever. The value stays
/// because rows written under the old rule still hold it; they read back as <see cref="Queued"/>.</para>
/// </summary>
public enum JobSlotState : byte { Queued = 0, Running = 1, Done = 2, Error = 3, Cancelled = 4 }

/// <summary>
/// The durable, write-through record of a render job (the former in-memory-only <c>JobQueue</c> state). One job owns N
/// ordered <see cref="JobSlotRecord"/>s — one slot per image (one ComfyUI prompt). The owning instance
/// (<see cref="MachineName"/>) reconciles it against ComfyUI and writes every state transition here; the in-memory
/// queue is a cache over these rows. A finalized job is readable by id from any instance (durable), but only the
/// owning instance advances or finalizes it (invariant #4).
/// </summary>
public sealed class JobRecord
{
    public required string JobId { get; init; }
    public required long UserId { get; init; }
    /// <summary>Owning instance — only it reconciles this job.</summary>
    public required string MachineName { get; init; }
    /// <summary>Display: the job's configuration id.</summary>
    public required string Model { get; set; }
    /// <summary>Display: the job's prompt/instruction.</summary>
    public required string Prompt { get; set; }
    /// <summary>Number of slots/images.</summary>
    public required int Total { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Active;
    public DateTime CreatedAtUtc { get; set; }
    /// <summary>Set when finalized (all slots terminal).</summary>
    public DateTime? FinishedAtUtc { get; set; }
    public List<JobSlotRecord> Slots { get; set; } = new();
}

/// <summary>
/// One image slot of a <see cref="JobRecord"/>. Mirrors dbo.JobSlot; carries everything the worker needs to (re)render
/// it — the SPEC, from <see cref="Workflow"/> down — and everything needed to write its HistoryEntry on completion
/// (<see cref="ImageId"/>, <see cref="EffectivePrompt"/>, <see cref="RawPrompt"/>, <see cref="Marks"/>).
/// <para>The spec used to be one encrypted JSON blob (<c>RequestJson</c>) holding eleven fields because two of them
/// are protected, which dragged four image FOREIGN KEYS behind an opaque wall — and a foreign key inside an encrypted
/// blob is not a foreign key: nothing can join it, count it, or garbage-collect against it. That is not hypothetical;
/// it is how 19,329 upload rows became unreachable. Encryption is a property of a FIELD, so the two text fields are
/// encrypted and the ids, flags and numbers beside them are not.</para>
/// <para>Typed columns also delete a whole failure class. <c>RequestJson</c> was a serialization contract, and a
/// renamed property deserialized SILENTLY into a null — an object with a hole in it, which is how one job sat Active
/// for five weeks. A renamed column fails at the database instead, loudly.</para>
/// </summary>
public sealed class JobSlotRecord
{
    public required string JobId { get; init; }
    public required int SlotIndex { get; init; }
    public bool IsEdit { get; set; }
    public JobSlotState State { get; set; } = JobSlotState.Queued;
    /// <summary>ComfyUI prompt id — internal; the liveness key, never exposed.</summary>
    public string? ComfyPromptId { get; set; }
    /// <summary>Produced image (dbo.ImageBlob id).</summary>
    public string? ImageId { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    /// <summary>Edits: false when the model declined (no new image).</summary>
    public bool Changed { get; set; } = true;
    /// <summary>Edits only (pHash distance).</summary>
    public double? ChangeScore { get; set; }
    public string? Error { get; set; }
    /// <summary>The finalized prompt this slot rendered.</summary>
    public string? EffectivePrompt { get; set; }
    /// <summary>The prompt verbatim in marker form (random injections included) — copied to the history row on
    /// completion, so a job resumed after a restart still records one.</summary>
    public string? RawPrompt { get; set; }
    /// <summary>The negative verbatim in marker form; null when none was submitted.</summary>
    public string? RawNegativePrompt { get; set; }
    /// <summary>The produced image's marks. A real child table (dbo.JobSlotMark), mirroring dbo.HistoryMark, rather
    /// than the encrypted { token -> kind } blob it replaces — which was the one copy of this data nothing could
    /// query, join or count.</summary>
    public List<Mark> Marks { get; set; } = [];
    public DateTime? GenStartedAtUtc { get; set; }
    public double? ExpectedGenSeconds { get; set; }

    /// <summary>The workflow configuration this slot renders. NOT protected — a workflow id names software.</summary>
    public string? Workflow { get; set; }
    /// <summary>The generate prompt, or an edit's instruction. User text: ENCRYPTED at rest.</summary>
    public string? Prompt { get; set; }
    /// <summary>The submitted negative, if any. User text: ENCRYPTED at rest.</summary>
    public string? NegativePrompt { get; set; }
    /// <summary>"square" | "landscape" | "portrait" (generates only).</summary>
    public string? Aspect { get; set; }
    /// <summary>Whether the worker should sample an artist for this slot. Null = the caller specified none.</summary>
    public bool? RandomArtist { get; set; }
    /// <summary>Whether the worker should generate the prompt from the tag model. Null = the caller specified none.</summary>
    public bool? RandomPrompt { get; set; }
    /// <summary>The random-prompt sampling temperature. Null = the caller specified none.</summary>
    public double? Temperature { get; set; }
    /// <summary>The generation mask for this slot as a JSON array of type NAMES. A value set, not a relation — the
    /// same shape (and the same plain storage) as <c>AppUser.GenerationTagTypes</c>. Null = the caller sent none.</summary>
    public string? TagTypesJson { get; set; }
    /// <summary>The workflow's exposed parameter values as a JSON map. An arbitrary bag keyed by parameter name, not
    /// a relation to anything, and not protected — stored plain so it is readable without a key.</summary>
    public string? OverridesJson { get; set; }
    /// <summary>The user's LoRA stack for this slot as a JSON array of <c>{name,weight}</c>. A value bag, not a
    /// relation — stored plain and per-slot like <see cref="OverridesJson"/>, so a batch resumed after a restart
    /// re-renders with its LoRAs intact. Null when the slot used none.</summary>
    public string? LorasJson { get; set; }
    /// <summary>Edits: the source image. A real, joinable id.</summary>
    public string? SourceImageId { get; set; }
    /// <summary>Inpaint: the mask image. A real, joinable id.</summary>
    public string? MaskImageId { get; set; }
    /// <summary>Image-to-video: the end frame. A real, joinable id.</summary>
    public string? LastFrameImageId { get; set; }
    /// <summary>Edits: the reference images, in order (dbo.JobSlotReference). Ordered because they are positional to
    /// the workflow.</summary>
    public List<string> ReferenceImageIds { get; set; } = [];
}
