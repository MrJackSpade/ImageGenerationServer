using ImageGen.Application.Images;
using ImageGen.Application.Media;
using ImageGen.Application.Platform;
using ImageGen.Application.Prompting;
using ImageGen.Application.Prompting.Tags;
using ImageGen.Application.Tags;
using ImageGen.Application.Workflows;
using ImageGen.Domain;
using ImageGen.Domain.CodeAnalysis;
using ImageGen.Domain.Entities;
using ImageGen.Domain.Logging;
using ImageGen.Domain.Repositories;
using System.Text.Json;

namespace ImageGen.Application.Rendering;

/// <summary>
/// Fair, slot-based render job queue and the render worker. The backend is a single global FIFO, so one user's batch
/// of N would head-of-line everyone else; this queue restores fairness by scheduling individual image SLOTS
/// least-recently-served round-robin across users, one at a time, through <see cref="IComfyClient"/> (one GPU).
///
/// A <see cref="RenderJob"/> is the unit a user submits together. It is a LIVE PROJECTION of the backend's state: the
/// worker advances each slot and the job is FINALIZED (and leaves the active feed) only once every slot is terminal.
/// All state is WRITE-THROUGH to <see cref="IJobRepository"/>, so a job survives a restart (rehydrated on startup) and
/// a finalized job is recoverable by id. This instance owns (and alone reconciles) the jobs whose
/// <see cref="RenderJob.MachineName"/> is its own.
///
/// This is a plain singleton: its background loop is <see cref="RunAsync"/>, driven by a hosted-service adapter in the
/// web host so the core stays free of the generic host. Every collaborator is constructor-injected (no service
/// locator); the one request-scoped dependency (the history repository) is resolved per-write via
/// <see cref="IServiceScopeFactory"/>.
/// </summary>
[AllowMagicStrings("log and exception message templates and human-readable failure-reason strings")]
public sealed class RenderOrchestrator : IStepProgressSink, IRenderProgressRouteResolver
{
    /// <summary>How many consecutive polls the backend must fail to list a submitted prompt (while no result has
    /// landed) before it is declared LOST. Debounces the history-flush race; NOT a render deadline.</summary>
    private const int LivenessVanishThreshold = 3;

    /// <summary>Feather for the server-side masked-edit composite (the Kind=Edit route). These mirror the in-graph
    /// inpaint defaults (mask_grow 16, mask_blur 12) so the composite route and the sibling-inpaint route paste the
    /// masked region back with the same soft edge.</summary>
    private const int CompositeMaskGrowPx = 16;
    private const int CompositeMaskBlurPx = 12;

    /// <summary>The param-bag key the orchestrator injects a random seed under when a submission pins none.</summary>
    private static class Keys
    {
        public const string Seed = "seed";
    }

    /// <summary>Separator for naming a list of values in a user-facing string.</summary>
    private static class Format
    {
        public const string ListSeparator = ", ";
    }

    /// <summary>Marker/folding rules used only when a prose workflow opts into the tag generator without declaring its
    /// own booru-tagging contract. Generated names are stored in the same reload-safe marker dialect as existing
    /// tagging workflows, rendered as ordinary comma-separated natural-language tags, and artists lose their marker.</summary>
    private static readonly WorkflowTagging ProseTagGeneratorRules =
        new(Tags: true, Artists: true, KeepArtistMarker: false, UnderscoresToSpaces: true);

    /// <summary>The user-activity-log category tags this orchestrator writes under.</summary>
    private static class LogCategories
    {
        public const string Submit = "submit";
        public const string SubmitEdit = "submit_edit";
        public const string RandomPrompt = "random_prompt";
    }

    private readonly Lock _lock = new();
    private readonly Dictionary<string, RenderJob> _jobs = new(StringComparer.Ordinal);
    /// <summary>The FOREGROUND tier: per-owner fair round-robin, served exactly as before background work existed.</summary>
    private readonly Dictionary<long, Queue<RenderSlot>> _byOwner = [];
    /// <summary>The BACKGROUND (idle-time) tier: per-owner fair round-robin, drawn from only once the queue has been
    /// foreground-idle for the configured delay. A separate map — rather than one mixed queue filtered at pick time —
    /// is what makes "foreground first" structural: a background slot can never sit in front of foreground work.</summary>
    private readonly Dictionary<long, Queue<RenderSlot>> _bgByOwner = [];
    /// <summary>Least-recently-served tick per owner in the foreground tier.</summary>
    private readonly Dictionary<long, long> _lastServed = [];
    /// <summary>Least-recently-served tick per owner in the background tier (kept apart so the two tiers' fairness is
    /// independent).</summary>
    private readonly Dictionary<long, long> _bgLastServed = [];
    /// <summary>Monotonic service counter shared by both tiers' round-robin bookkeeping.</summary>
    private long _servedSeq;
    /// <summary>The last moment foreground work was submitted or resolved. Background slots become eligible only once
    /// <c>now - this &gt;= the idle delay</c>. Background running is NOT activity (it must not keep resetting its own
    /// timer), so this is stamped by foreground enqueues and finishing foreground slots only. Starts fresh at boot.</summary>
    private DateTimeOffset _lastForegroundActivityUtc = DateTimeOffset.UtcNow;
    private readonly Dictionary<string, RenderSlot> _comfyToSlot = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _signal = new(0);
    /// <summary>Serializes the durable Active→Cancelled transition with rehydration's durable-read→live-publish
    /// transition. Without this, a stale active-list row can be published after cancellation and later persisted back
    /// over the cancelled row.</summary>
    private readonly SemaphoreSlim _rehydrateMutation = new(1, 1);
    /// <summary>Terminal jobs with exactly one owner driving their final durable write. A rejected write leaves the
    /// job visible in memory and the driver retries until the row accepts the terminal state.</summary>
    private readonly HashSet<string> _finalPersistenceDrivers = new(StringComparer.Ordinal);
    private readonly string _machine = Environment.MachineName;
    private RenderSlot? _running;

    private readonly IComfyClient _comfy;
    private readonly IWorkflowCatalog _catalog;
    private readonly ITagModelClient _tagModel;
    private readonly ITagCatalog _tags;
    private readonly IMediaProcessor _media;
    private readonly IJobRepository _jobRepo;
    private readonly IUploadStore _uploads;
    private readonly ImageVisibilityService _visibility;
    private readonly IImageBlobRepository _blobs;
    private readonly IImageFrameRepository _frames;
    private readonly IGenTimingRepository _timings;
    private readonly Snapshots.ISnapshot<Snapshots.GenTimingAverages> _timingAverages;
    private readonly IUserLogService _userLog;
    private readonly IDatabaseAvailability _db;
    private readonly RenderOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RenderOrchestrator> _log;

    /// <summary>Construct the orchestrator with its collaborators. All are singletons except the history repository,
    /// which is resolved per-write from <paramref name="scopeFactory"/>.</summary>
    public RenderOrchestrator(
        IComfyClient comfy, IWorkflowCatalog catalog, ITagModelClient tagModel, ITagCatalog tags,
        IMediaProcessor media, IJobRepository jobRepo, IUploadStore uploads, ImageVisibilityService visibility,
        IImageBlobRepository blobs,
        IImageFrameRepository frames,
        IGenTimingRepository timings, Snapshots.ISnapshot<Snapshots.GenTimingAverages> timingAverages,
        IUserLogService userLog, IDatabaseAvailability databaseAvailability,
        RenderOptions options,
        IServiceScopeFactory scopeFactory, ILogger<RenderOrchestrator> log)
    {
        _comfy = comfy;
        _catalog = catalog;
        _tagModel = tagModel;
        _tags = tags;
        _media = media;
        _jobRepo = jobRepo;
        _uploads = uploads;
        _visibility = visibility;
        _blobs = blobs;
        _frames = frames;
        _timings = timings;
        _timingAverages = timingAverages;
        _userLog = userLog;
        _db = databaseAvailability;
        _options = options;
        _scopeFactory = scopeFactory;
        _log = log;
    }

    #region enqueue

    /// <summary>Create a job from one or more render items, persist it (all-Queued) BEFORE its slots become pickable,
    /// then make the slots schedulable and wake the worker. One item = a lone job; many = a batch.</summary>
    public async Task<RenderJob> EnqueueJobAsync(long owner, IReadOnlyList<RenderItem> items)
    {
        // Validate at the owning boundary, not only at HTTP callers: a zero-slot job is vacuously terminal, persists
        // as Error, never signals the worker, and can never be removed by AfterSlotAsync because it has no slots.
        _ = Ensure.NotEmpty(items);

        // The prompt DSL is resolved HERE, not on the client: Comfy-compatible '{a|b}' picks one option and
        // '{{a|b}}' fans an item into one slot per combo. Generation and edit therefore share the exact same syntax,
        // including direct API calls, and Comfy receives concrete text rather than frontend-only dynamic syntax.
        items = ExpandPromptGroups(items);

        RenderJob job = new()
        {
            JobId = Guid.NewGuid().ToString(GuidFormats.NoDashes),
            Owner = owner,
            MachineName = _machine,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        for (int i = 0; i < items.Count; i++)
        {
            GenerateSpec? gen = items[i].Gen;
            EditSpec? edit = items[i].Edit;
            string? notice = null;
            // Pre-queue normalization: snap any out-of-range input onto a valid value NOW, so the worker builds the
            // corrected value and the notice reaches the slot before its placeholder renders.
            //
            // This is NOT wrapped in a catch. NormalizeForQueue already answers the one expected non-failure — an
            // unknown or mis-kinded config — as a documented no-op, so anything that throws out of it is a real fault
            // in the workflow's own Normalize. Logging it and "enqueuing the request as-is" would hand the GPU the very
            // parameters normalization exists to correct, and still return a job id as though the request had been
            // accepted intact. The job has not been published or persisted yet (that happens below the loop),
            // so throwing here abandons it cleanly and the caller gets the real error instead of a bad render later.
            if (edit is not null)
            {
                QueueNormalizationResult norm = _comfy.NormalizeForQueue(edit.Workflow, RenderKind.Edit, edit.Overrides);
                if (norm.Notice is not null)
                {
                    edit = edit with { Overrides = AsDict(norm.Overrides) };
                    notice = norm.Notice;
                }
            }
            else if (gen is not null)
            {
                QueueNormalizationResult norm = _comfy.NormalizeForQueue(gen.Workflow, RenderKind.Generate, gen.Overrides);
                if (norm.Notice is not null)
                {
                    gen = gen with { Overrides = AsDict(norm.Overrides) };
                    notice = norm.Notice;
                }
            }
            // Seed is a generation parameter decided at this boundary: fill a fresh one unless the caller pinned it, so
            // the exact seed is persisted with the request and is what the workflow builds with.
            if (gen is not null)
            {
                gen = gen with { Overrides = WithSeed(gen.Overrides) };
            }

            if (edit is not null)
            {
                edit = edit with { Overrides = WithSeed(edit.Overrides) };
            }

            job.Slots.Add(new RenderSlot
            {
                Job = job,
                Index = i,
                Gen = gen,
                Edit = edit,
                Notice = notice,
                IsBackground = items[i].Background,
                EditResult = edit is not null ? new EditResult() : null,
            });
        }

        lock (_lock)
        {
            _jobs[job.JobId] = job;   // visible to Get()/owner lookups now; NOT yet schedulable
        }

        // The comment above is a rule, so the result is checked. PersistAsync waits out an unreachable database, so a
        // false here means the write was REJECTED — and a job that exists only in memory must not become schedulable:
        // it would render, and the record of it would die with the process. Refusing a new submission is acceptable;
        // accepting one we cannot write down is not. The job is dropped again so nothing is left half-accepted.
        Exception? persistFailure = await PersistAsync(job);
        if (persistFailure is not null)
        {
            lock (_lock)
            {
                _ = _jobs.Remove(job.JobId);
            }

            throw RenderStorageException.Submission(persistFailure);
        }

        // A foreground submission is what preemption and the idle clock hinge on, so decide it up front. A batch is
        // homogeneous in practice, but check every slot so a mixed one still counts a single foreground slot as
        // foreground work.
        bool anyForeground = job.Slots.Exists(s => !s.IsBackground);
        RenderSlot? preempt = null;
        lock (_lock)
        {
            foreach (RenderSlot s in job.Slots)
            {
                Dictionary<long, Queue<RenderSlot>> tier = s.IsBackground ? _bgByOwner : _byOwner;
                if (!tier.TryGetValue(owner, out Queue<RenderSlot>? q))
                {
                    q = new Queue<RenderSlot>();
                    tier[owner] = q;
                }

                q.Enqueue(s);
            }

            if (anyForeground)
            {
                // Restart the idle clock: any foreground submission means the queue is no longer idle, so background
                // work waits out a fresh window from here.
                _lastForegroundActivityUtc = DateTimeOffset.UtcNow;
                // Preempt a background slot the worker is running RIGHT NOW. The worker sees the flag on its next poll
                // and returns the slot non-terminal to the background tier; the interrupt below stops the GPU at once
                // so the foreground job does not wait for the background render to finish. Only interrupt if the
                // background prompt is actually out there — firing one with nothing of ours in flight would kill
                // whatever else is on that GPU.
                if (_running is { IsBackground: true } bg && !bg.Terminal)
                {
                    bg.PreemptRequested = true;
                    if (bg.Submitted)
                    {
                        preempt = bg;
                    }
                }
            }
        }

        if (preempt is not null)
        {
            // As in Cancel: the slot is already flagged, so a failed interrupt does not undo the preemption — but the
            // GPU is then still on the background render, and that must be recorded rather than dropped. This is a
            // Await the interrupt of the backend's single in-flight prompt (ours) before waking the worker — long
            // before it can notice the preempt, requeue, pick, build and submit the foreground slot — so the interrupt
            // cannot land on that later render. There is no interrupt-by-prompt-id on the backend to tighten this.
            try
            {
                await _comfy.InterruptAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Foreground submit preempted a background slot but the backend interrupt failed; its render may still be running.");
            }
        }

        if (job.Slots.Count > 0)
        {
            _ = _signal.Release(job.Slots.Count);
        }

        return job;
    }

    /// <summary>The overrides with a fresh RNG <c>seed</c> filled in unless the caller pinned one — so the generation
    /// seed is decided here, persisted with the request, and single-sourced for the build.</summary>
    internal static Dictionary<string, JsonElement> WithSeed(Dictionary<string, JsonElement>? overrides)
    {
        Dictionary<string, JsonElement> d = overrides is null ? [] : new Dictionary<string, JsonElement>(overrides);
        if (!d.TryGetValue(Keys.Seed, out JsonElement seed) || IsBlankSeed(seed))
        {
            d[Keys.Seed] = JsonSerializer.SerializeToElement(RenderSeed.Random());
        }

        return d;
    }

    private static bool IsBlankSeed(JsonElement seed) => seed.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => true,
        JsonValueKind.String => string.IsNullOrWhiteSpace(seed.GetString()),
        _ => false,
    };

    private static Dictionary<string, JsonElement>? AsDict(IReadOnlyDictionary<string, JsonElement>? d) =>
        d is null ? null : new Dictionary<string, JsonElement>(d);

    #endregion

    #region reads

    /// <summary>The user's still-active jobs (not yet finalized), oldest first.</summary>
    public List<RenderJob> ActiveForOwner(long owner)
    {
        lock (_lock)
        {
            return [.. _jobs.Values.Where(j => j.Owner == owner && !j.AllTerminal).OrderBy(j => j.CreatedAt)];
        }
    }

    /// <summary>Every still-active job on this instance, all owners, oldest first — the cross-user queue view.</summary>
    public List<RenderJob> AllActive()
    {
        lock (_lock)
        {
            return [.. _jobs.Values.Where(j => !j.AllTerminal).OrderBy(j => j.CreatedAt)];
        }
    }

    /// <summary>A live (in-memory) job by id, or null (a finalized job is read from <see cref="IJobRepository"/>).</summary>
    public RenderJob? Get(string jobId)
    {
        lock (_lock)
        {
            return _jobs.GetValueOrDefault(jobId);
        }
    }

    /// <summary>This instance's render work right now, counted under one lock so the numbers agree with each other.
    /// See <see cref="WorkloadSnapshot"/> for why in-flight and executing are separate counts.</summary>
    public WorkloadSnapshot Workload()
    {
        lock (_lock)
        {
            List<RenderJob> active = [.. _jobs.Values.Where(j => !j.AllTerminal)];
            int waiting = active.SelectMany(j => j.Slots).Count(s => !s.Terminal && !ReferenceEquals(s, _running));
            return new WorkloadSnapshot(
                ActiveJobs: active.Count,
                InFlightSlots: _running is null ? 0 : 1,
                ExecutingSlots: _running is { State: SlotState.Running } ? 1 : 0,
                WaitingSlots: waiting);
        }
    }

    /// <summary>
    /// What is still to be rendered on this instance, under the same one lock as <see cref="Workload"/> so the counts
    /// and the ETA describe the same instant. The queue page cannot work any of this out for itself: it sees 25 rows
    /// at a time and its `total` counts finished history too, so a client-side sum is wrong in both directions.
    /// </summary>
    public OutstandingSnapshot Outstanding(long viewer)
    {
        lock (_lock)
        {
            List<RenderJob> active = [.. _jobs.Values.Where(j => !j.AllTerminal)];
            RenderSlot? running = _running;
            // "What's left" is IMMINENT work: parked background slots may not run for the whole idle delay (or ever,
            // while foreground traffic continues), so pricing them into the header ETA would overstate near-term load
            // by an arbitrary amount. A background slot that is actually ON the GPU right now is imminent and counts.
            // (The job/owner counts below still come from `active`, so a background-only queue still lights up its
            // Cancel buttons — only the image count and ETA drop the parked background work.)
            List<RenderSlot> pending = [.. active.SelectMany(j => j.Slots).Where(s => !s.Terminal && (!s.IsBackground || ReferenceEquals(s, running)))];

            // The in-flight slot is priced from its own measurement only once it HAS one — the expected time and start
            // instant are assigned at submit, so a slot the worker has picked but not yet submitted has neither. That
            // one is priced from the workflow average like anything else waiting, rather than silently counting zero.
            double? runningRemaining = null;
            if (running is { ExpectedGenSeconds: { } expected, GenStartedAt: { } started })
            {
                runningRemaining = Math.Max(0, expected - (DateTimeOffset.UtcNow - started).TotalSeconds);
            }

            List<string> waiting = [.. pending
                .Where(s => runningRemaining is null || !ReferenceEquals(s, running))
                .Select(s => s.Model)];
            return new OutstandingSnapshot(
                active.Count, active.Count(j => j.Owner == viewer), pending.Count, runningRemaining, waiting);
        }
    }

    /// <inheritdoc />
    public RenderProgressRoute? ResolveProgressRoute(string comfyPromptId)
    {
        _ = Ensure.NotNullOrEmpty(comfyPromptId);
        lock (_lock)
        {
            return _comfyToSlot.TryGetValue(comfyPromptId, out RenderSlot? slot)
                ? new RenderProgressRoute(slot.Job.Owner, slot.Job.JobId)
                : null;
        }
    }

    /// <inheritdoc />
    public void ReportStepFraction(string comfyPromptId, double fraction)
    {
        _ = Ensure.NotNullOrEmpty(comfyPromptId);
        _ = Ensure.Between(fraction, 0, 1);
        lock (_lock)
        {
            if (_comfyToSlot.TryGetValue(comfyPromptId, out RenderSlot? slot) && !slot.Terminal)
            {
                slot.StepFraction = fraction;
            }
        }
    }

    /// <summary>Approximate count of image slots that will run before this job's first queued slot.</summary>
    public int JobsAhead(RenderJob job)
    {
        lock (_lock)
        {
            // The job the worker is on is the head of the line — nothing is ahead of it — whether its slot is being
            // prepared, waiting in the backend's queue, or executing.
            if (_running is not null && ReferenceEquals(_running.Job, job))
            {
                return 0;
            }

            if (!job.Slots.Exists(s => s.State == SlotState.Queued))
            {
                return 0;
            }

            int queuedAhead = _byOwner.Values.Sum(q => q.Count(s => s.Job.CreatedAt < job.CreatedAt));
            return queuedAhead + (_running is not null ? 1 : 0);
        }
    }

    #endregion

    #region cancel

    /// <summary>Cancel a whole job: drop the slots still waiting, and ask the worker to abandon the one it holds.
    /// Returns false if unknown.</summary>
    public async Task<bool> CancelAsync(string jobId)
    {
        RenderJob? job;
        bool interrupt = false;
        lock (_lock)
        {
            if (!_jobs.TryGetValue(jobId, out job))
            {
                return false;
            }

            foreach (RenderSlot s in job.Slots)
            {
                if (s.Terminal)
                {
                    continue;
                }
                // Whose slot it is, not what state it is in. The worker's slot may be mid-prompt-build or mid-submit
                // and is NOT necessarily Running; marking it terminal from here would be overwritten by the result the
                // worker then lands. So it is asked to stop (it checks before submitting and on every poll) while the
                // rest are dropped outright. Only interrupt the backend if the prompt is actually out there — firing
                // an interrupt with nothing of ours in flight would kill whatever else is on that GPU.
                if (ReferenceEquals(s, _running))
                {
                    s.CancelRequested = true;
                    interrupt = s.Submitted;
                }
                else
                {
                    s.State = SlotState.Cancelled;
                    s.Error = "cancelled";
                }
            }
            // Drop the just-cancelled slots from BOTH tiers' owner queues (a job is homogeneous, but a cancel touches
            // whichever tier its slots sit in). PickFromTier would drop them lazily too, but rebuilding here keeps the
            // queues — and the counts read off them — honest immediately.
            if (_byOwner.TryGetValue(job.Owner, out Queue<RenderSlot>? fgQ))
            {
                _byOwner[job.Owner] = new Queue<RenderSlot>(fgQ.Where(s => s.State == SlotState.Queued));
            }

            if (_bgByOwner.TryGetValue(job.Owner, out Queue<RenderSlot>? bgQ))
            {
                _bgByOwner[job.Owner] = new Queue<RenderSlot>(bgQ.Where(s => s.State == SlotState.Queued));
            }
        }

        if (interrupt)
        {
            // The cancel itself has already succeeded — these slots are terminal in our state regardless of what the
            // backend does next, so a failed interrupt does not undo it and must not fail the caller. What it must not
            // do is vanish: an empty catch here would let a backend that no longer honours interrupts leave the GPU
            // rendering cancelled work with nothing anywhere to say so.
            try
            {
                await _comfy.InterruptAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Job {JobId} was cancelled but the backend interrupt failed; its render may still be running.", jobId);
            }
        }

        _ = AfterSlotAsync(job);   // persist + finalize if everything is now terminal
        return true;
    }

    /// <summary>
    /// Cancel a job this instance OWNS whose row is still Active but which no worker holds — one stranded by a crash,
    /// or by a rehydrate pass that never reached it. Nothing is rendering it (that is what stranded means), so the row
    /// is simply cancelled. If rehydration made it live while this call waited, this method delegates to
    /// <see cref="Cancel"/> inside the same transition gate. Returns false when it belongs to another instance
    /// (invariant #4 — only its owner may advance it), or has already resolved.
    /// </summary>
    public async Task<bool> CancelStrandedAsync(string jobId, CancellationToken ct)
    {
        await _rehydrateMutation.WaitAsync(ct);
        try
        {
            bool live;
            lock (_lock)
            {
                live = _jobs.ContainsKey(jobId);
            }

            if (live)
            {
                return await CancelAsync(jobId);
            }

            JobRecord? rec = await _jobRepo.GetAsync(jobId, ct);
            if (rec is null || rec.Status != JobStatus.Active)
            {
                return false;
            }

            if (!string.Equals(rec.MachineName, _machine, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            await _jobRepo.CancelAsync(jobId, ct);
            return true;
        }
        finally
        {
            _ = _rehydrateMutation.Release();
        }
    }

    /// <summary>
    /// Cancel every unfinished job on this instance, or just one owner's. Returns how many jobs were cancelled.
    /// <para>Server-side deliberately: the queue page shows 25 rows of a list it re-polls every 2s, so a client-side
    /// loop over the rendered rows would clear only the visible page and race the poll rebuilding it.</para>
    /// <para>The render on the GPU is stopped without any separate interrupt call, and — this is the point — without
    /// the risk of one. <see cref="CancelAsync"/> interrupts only when the slot the worker holds belongs to the job being
    /// cancelled, so cancelling a set stops the in-flight image exactly when that image is part of the set. Firing an
    /// unconditional interrupt for "cancel mine" would kill whatever else was on that GPU, which for a cross-user box
    /// means killing another user's image while claiming to have cancelled only your own.</para>
    /// <para>Stranded rows are included: still Active in the database but held by no worker (a crash, or a rehydrate
    /// that never reached them). They are precisely what someone reaching for "cancel everything" wants gone, and
    /// nothing else will ever clear them.</para>
    /// </summary>
    public async Task<int> CancelAllAsync(long? owner, CancellationToken ct)
    {
        List<RenderJob> live = owner is { } o ? ActiveForOwner(o) : AllActive();
        int cancelled = 0;
        foreach (RenderJob job in live)
        {
            if (await CancelAsync(job.JobId))
            {
                cancelled++;
            }
        }

        IReadOnlyList<JobRecord> rows = await _jobRepo.ListActiveForMachineAsync(_machine, ct);
        foreach (JobRecord rec in rows)
        {
            if (owner is { } only && rec.UserId != only)
            {
                continue;
            }

            if (await CancelStrandedAsync(rec.JobId, ct))
            {
                cancelled++;   // re-checks live/owner/status itself
            }
        }

        return cancelled;
    }

    /// <summary>
    /// Re-enqueue the images of a FINISHED job that were never made — its cancelled and failed slots — as a brand new
    /// job. The slot's stored spec — the same typed columns a restart rehydrates from — is what gets re-run, so this
    /// re-runs what was asked for rather than re-deriving it. The seed travels with it, so a requeued image is the
    /// image that was going to be made.
    /// <para>A NEW job, never a revival of the old row. A finalized job leaving the active feed is load-bearing —
    /// /jobs returns only active jobs, and a job DISAPPEARING from it is exactly how a client concludes it finished —
    /// so re-activating a row clients have already reconciled would replay as a fresh completion of an old job.</para>
    /// <para>Only the slots that produced nothing are redone. A batch of ten where three landed requeues seven; the
    /// three that worked are done and their images already exist.</para>
    /// <para>Refused at the door when an input can no longer be found, rather than enqueueing a job that renders and
    /// then fails. Uploads — an edit source, reference, inpaint mask, i2v end frame — are deliberately never
    /// persisted, so once the process that held them is gone the render is not reproducible and the honest answer is
    /// to say so. (An id that resolves through neither the upload store nor <c>ImageBlob</c> is treated as gone. The
    /// render path has one further fallback for ids predating DB-first storage, so a legacy source is refused here
    /// even though it might have rendered — refusing something that could work beats offering a doomed job.)</para>
    /// </summary>
    public async Task<RequeueOutcome> RequeueAsync(string jobId, long owner, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_jobs.ContainsKey(jobId))
            {
                return new RequeueOutcome(RequeueStatus.StillActive);
            }
        }

        JobRecord? rec = await _jobRepo.GetAsync(jobId, ct);
        if (rec is null)
        {
            return new RequeueOutcome(RequeueStatus.UnknownJob);
        }
        // Owner-checked, unlike /cancel/{id}. Cancel destroys work; requeue CREATES it, under an owner, and the
        // scheduler is fair round-robin per owner — so an unchecked requeue would let one user push work into
        // another's queue share.
        if (rec.UserId != owner)
        {
            return new RequeueOutcome(RequeueStatus.NotOwner);
        }

        List<JobSlotRecord> missing = [.. rec.Slots
            .Where(s => s.ImageId is null && s.State is JobSlotState.Error or JobSlotState.Cancelled)
            .OrderBy(s => s.SlotIndex)];
        if (missing.Count == 0)
        {
            return new RequeueOutcome(RequeueStatus.NothingMissing);
        }

        List<RenderItem> items = new(missing.Count);
        List<EditSpec> edits = [];
        foreach (JobSlotRecord sr in missing)
        {
            string which = $"image {sr.SlotIndex + 1}";
            // One check, on a real column: a column either has a workflow in it or it does not. Deserializing a blob
            // instead would force a second question — is the object usable? — because System.Text.Json ignores members
            // it doesn't recognise, so a request written under an older property name yields a spec with a null
            // workflow and no error.
            if (string.IsNullOrWhiteSpace(sr.Workflow))
            {
                return new RequeueOutcome(RequeueStatus.Unrunnable, Reason: $"{which} didn't record the workflow that would remake it");
            }

            try
            {
                // Carry the slot's scheduling class through: a background job's missing images re-run as background,
                // not silently promoted to foreground.
                if (sr.IsEdit)
                {
                    EditSpec edit = EditSpecOf(sr);
                    edits.Add(edit);
                    items.Add(RenderItem.ForEdit(edit, sr.IsBackground));
                }
                else
                {
                    items.Add(RenderItem.ForGenerate(GenerateSpecOf(sr), sr.IsBackground));
                }
            }
            catch (JsonException ex)
            {
                return new RequeueOutcome(RequeueStatus.Unrunnable, Reason: $"{which}'s stored parameters are unreadable: {ex.Message}");
            }
        }

        if (await FirstMissingEditInputAsync(owner, edits, ct) is { } gone)
        {
            return new RequeueOutcome(RequeueStatus.Unrunnable, Reason: gone);
        }

        RenderJob job = await EnqueueJobAsync(owner, items);
        return new RequeueOutcome(RequeueStatus.Requeued, job.JobId, items.Count);
    }

    /// <summary>The first edit input across these specs that can no longer be USED — the owner may no longer read it,
    /// it can no longer be found, or it is a reference whose media KIND the workflow doesn't accept — phrased for the
    /// user, or null when every one is usable. Bulk ownership and content-type queries keep the checks set-based.</summary>
    private async Task<string?> FirstMissingEditInputAsync(long owner, List<EditSpec> edits, CancellationToken ct)
    {
        if (edits.Count == 0)
        {
            return null;
        }

        // (id, what it is) in the order they're reported, so the message names the input the user recognises.
        List<(string Id, string What)> inputs = [];
        foreach (EditSpec e in edits)
        {
            inputs.Add((e.ImageId, "source image"));
            if (!string.IsNullOrWhiteSpace(e.MaskImageId))
            {
                inputs.Add((e.MaskImageId, "inpaint mask"));
            }

            if (!string.IsNullOrWhiteSpace(e.LastFrameImageId))
            {
                inputs.Add((e.LastFrameImageId, "end frame"));
            }

            foreach (string r in e.ReferenceIds ?? [])
            {
                if (!string.IsNullOrWhiteSpace(r))
                {
                    inputs.Add((r, "reference"));
                }
            }
        }

        HashSet<string> inputIds = inputs.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        IReadOnlySet<string> readable = await _visibility.ReadableAsync(owner, inputIds, ct);
        if (!inputIds.All(readable.Contains))
        {
            // Do not identify the failed id or distinguish foreign from absent. The owner knows only that the old job's
            // input capability is no longer valid, which is enough to explain why replay is refused without an oracle.
            return "one or more of its edit inputs is no longer available";
        }

        List<string> unresolved = [.. inputs.Where(i => _uploads.Get(i.Id) is null).Select(i => i.Id).Distinct(StringComparer.Ordinal)];
        IReadOnlyDictionary<string, string> stored = unresolved.Count == 0
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : await _blobs.GetContentTypesAsync(unresolved, ct);

        foreach ((string? id, string? what) in inputs)
        {
            if (_uploads.Get(id) is null && !stored.ContainsKey(id))
            {
                return $"its {what} is gone — an uploaded input lives only in the process that received it and is never stored";
            }
        }

        // Every reference's kind is intrinsic to its stored blob (content type), so a caller cannot smuggle an
        // audio/video file into an image-only workflow by mislabelling it: the kind is read here, authoritatively, and
        // a reference the workflow doesn't accept fails the whole enqueue rather than being silently dropped downstream.
        foreach (EditSpec e in edits)
        {
            string? rejection = ReferenceKindRejection(e, ContentTypeOf);
            if (rejection is not null)
            {
                return rejection;
            }
        }

        return null;

        string? ContentTypeOf(string id) => _uploads.Get(id)?.ContentType ?? (stored.TryGetValue(id, out string? ct2) ? ct2 : null);
    }

    /// <summary>Reject the first reference on <paramref name="edit"/> whose media kind the workflow doesn't accept (an
    /// unclassifiable content type, a kind the workflow declares no allowance for, or more of a kind than its per-kind
    /// max), phrased for the user — or null when every reference is acceptable. A workflow that declares no references
    /// at all rejects any reference it is handed.</summary>
    private string? ReferenceKindRejection(EditSpec edit, Func<string, string?> contentTypeOf)
    {
        IReadOnlyList<string> refIds = edit.ReferenceIds is { Count: > 0 } r ? [.. r.Where(x => !string.IsNullOrWhiteSpace(x))] : [];
        if (refIds.Count == 0)
        {
            return null;
        }

        WorkflowReference? allowed = _catalog.ResolveInfo(edit.Workflow)?.Reference;
        if (allowed is null)
        {
            return "this workflow doesn't accept reference inputs";
        }

        Dictionary<ReferenceKind, int> seen = [];
        foreach (string id in refIds)
        {
            ReferenceKind? kind = ReferenceKinds.Classify(contentTypeOf(id));
            if (kind is not { } k)
            {
                return "one of its references isn't a recognised image, audio or video file";
            }

            if (!allowed.Accepts(k))
            {
                return $"this workflow doesn't accept {ReferenceKinds.Wire(k)} references";
            }

            seen[k] = seen.TryGetValue(k, out int n) ? n + 1 : 1;
            if (seen[k] > allowed.MaxOf(k))
            {
                return $"this workflow accepts at most {allowed.MaxOf(k)} {ReferenceKinds.Wire(k)} reference(s)";
            }
        }

        return null;
    }

    /// <summary>Abandon the single image the worker is on (its job's other slots keep their place). Returns false when
    /// the worker has nothing. If the prompt hasn't reached the backend yet there is nothing to interrupt — the worker
    /// is told to stop and drops it before submitting.</summary>
    public async Task<bool> CancelRunningAsync()
    {
        RenderSlot? s;
        bool interrupt;
        lock (_lock)
        {
            s = _running;
            if (s is { } running)
            {
                running.CancelRequested = true;
                interrupt = running.Submitted;
            }
            else
            {
                interrupt = false;
            }
        }

        if (s is null)
        {
            return false;
        }

        if (interrupt)
        {
            // As in Cancel: the slot is already flagged to stop on our side, so a failed interrupt does not undo the
            // cancel — but it does mean the GPU is still on it, and that has to be recorded rather than dropped.
            try
            {
                await _comfy.InterruptAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Running slot was cancelled but the backend interrupt failed; its render may still be running.");
            }
        }

        return true;
    }

    #endregion

    #region worker

    /// <summary>The background render loop: rehydrate this instance's in-flight jobs, then pick and run slots fairly
    /// until cancellation. Driven by a hosted-service adapter in the web host.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        // Rehydration must EVENTUALLY happen, not merely be attempted once: if the database is
        // unreachable at boot, giving up permanently orphans this instance's in-flight jobs as
        // "running" rows that no worker owns and no API call can cancel. Retry in the
        // background with backoff until one pass succeeds — new work is accepted meanwhile,
        // and the merge skips jobs already in memory so a retry after a partial pass cannot
        // duplicate slots.
        _ = Task.Run(async () =>
        {
            TimeSpan delay = TimeSpan.FromSeconds(5);
            while (!ct.IsCancellationRequested && !await RehydrateAsync(ct))
            {
                _log.LogWarning("Rehydrate will retry in {Delay}s.", delay.TotalSeconds);
                try
                {
                    await Task.Delay(delay, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 300));
            }
        }, ct);

        while (!ct.IsCancellationRequested)
        {
            RenderSlot? slot = PickNext();
            if (slot is null)
            {
                // Nothing schedulable right now. Drain any stale wake tokens so the wait below actually blocks — a
                // background enqueue releases the semaphore even though its slot is not eligible until the idle delay
                // elapses, and without draining those leftover tokens would spin the loop. Re-check once after the
                // drain for work enqueued just before it; a token released AFTER the drain still unblocks the wait, so
                // no wake is lost.
                while (_signal.Wait(0))
                {
                }

                slot = PickNext();
                if (slot is null)
                {
                    // If background work is waiting ONLY on the idle timer, wake when it will elapse; otherwise wait for
                    // the next enqueue. The bound is the operator's configured idle delay, not an invented timeout — it
                    // is the mechanism by which idle-time work starts, exactly as the feature specifies.
                    TimeSpan? until = NextBackgroundWaitDelay();
                    try
                    {
                        if (until is TimeSpan d)
                        {
                            _ = await _signal.WaitAsync(d, ct);
                        }
                        else
                        {
                            await _signal.WaitAsync(ct);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    continue;
                }
            }

            await RunSlotAsync(slot, ct);
            bool wasForeground = !slot.IsBackground;
            lock (_lock)
            {
                _running = null;
            }
            // A slot that comes back NON-terminal was held (database out of reach) or PREEMPTED (a foreground submit
            // stopped it) — not finished. It is accepted work and its job is still Active, so it goes back on its tier's
            // queue rather than being stranded in memory. Requeue routes by IsBackground, so a preempted background slot
            // returns to the background tier and re-gates on the next idle window.
            if (!slot.Terminal && !ct.IsCancellationRequested)
            {
                Requeue(slot);
            }
            // A foreground slot leaving the GPU (finished, failed, or held) is foreground activity: restart the idle
            // clock so background work waits out a fresh idle window rather than starting the instant this slot resolves.
            if (wasForeground)
            {
                lock (_lock)
                {
                    _lastForegroundActivityUtc = DateTimeOffset.UtcNow;
                }
            }

            await AfterSlotAsync(slot.Job);   // persist this slot's result; finalize the job if all slots are terminal
        }
    }

    /// <summary>Put a held or preempted slot back on ITS TIER's owner queue and wake the worker. Routing by
    /// <see cref="RenderSlot.IsBackground"/> is what returns a preempted background slot to the background tier, so it
    /// re-gates on the next idle window rather than jumping ahead of foreground work. Its position is the back of that
    /// owner's line, the fair place for work that could not be done right now.</summary>
    private void Requeue(RenderSlot slot)
    {
        lock (_lock)
        {
            if (slot.Terminal)
            {
                return;
            }

            Dictionary<long, Queue<RenderSlot>> tier = slot.IsBackground ? _bgByOwner : _byOwner;
            if (!tier.TryGetValue(slot.Job.Owner, out Queue<RenderSlot>? q))
            {
                q = new Queue<RenderSlot>();
                tier[slot.Job.Owner] = q;
            }

            q.Enqueue(slot);
        }

        _ = _signal.Release();
    }

    /// <summary>
    /// The next slot to run: FOREGROUND first, then BACKGROUND only once the queue has been foreground-idle for the
    /// configured delay. Foreground is served exactly as before — nothing about idle-time work changes it. Background is
    /// considered only when no foreground slot is pickable AND <c>now - lastForegroundActivity &gt;= delay</c>; the worker
    /// running a background slot is not itself activity, so it does not reset that clock.
    /// </summary>
    private RenderSlot? PickNext()
    {
        lock (_lock)
        {
            RenderSlot? foreground = PickFromTier(_byOwner, _lastServed);
            if (foreground is not null)
            {
                return foreground;
            }
            // No foreground work waiting. Idle-time work runs only after the foreground-idle delay has elapsed.
            if (DateTimeOffset.UtcNow - _lastForegroundActivityUtc >= IdleDelay())
            {
                return PickFromTier(_bgByOwner, _bgLastServed);
            }

            return null;
        }
    }

    /// <summary>Fair round-robin within one tier via LEAST-RECENTLY-SERVED owner; ties break to the oldest queued
    /// slot's job. Sets <see cref="_running"/> to the picked slot. Call under <see cref="_lock"/>.</summary>
    private RenderSlot? PickFromTier(Dictionary<long, Queue<RenderSlot>> tier, Dictionary<long, long> lastServed)
    {
        long? best = null;
        long bestTick = long.MaxValue;
        DateTimeOffset bestHead = DateTimeOffset.MaxValue;
        foreach ((long owner, Queue<RenderSlot>? q) in tier)
        {
            while (q.Count > 0 && q.Peek().State != SlotState.Queued)
            {
                _ = q.Dequeue();   // drop cancelled/stale heads
            }

            if (q.Count == 0)
            {
                continue;
            }

            long tick = lastServed.GetValueOrDefault(owner, 0L);
            DateTimeOffset head = q.Peek().Job.CreatedAt;
            if (tick < bestTick || (tick == bestTick && head < bestHead))
            {
                best = owner;
                bestTick = tick;
                bestHead = head;
            }
        }

        if (best is null)
        {
            return null;
        }

        RenderSlot slot = tier[best.Value].Dequeue();
        lastServed[best.Value] = ++_servedSeq;
        // Picked, NOT running: the prompt still has to be built (tag sampling, image fetches) and submitted, and
        // the backend still has to start executing it. The slot stays Queued until the backend says otherwise —
        // see ObserveExecuting. What being picked does mean is that this slot is now the worker's.
        _running = slot;
        return slot;
    }

    /// <summary>How long the worker should sleep before background work MIGHT become schedulable, or null when there is
    /// no background work waiting on the idle timer (so the worker waits for the next enqueue instead). Only meaningful
    /// after <see cref="PickNext"/> has returned null — i.e. no foreground slot is pickable — so the only thing gating
    /// the background tier is the idle delay. Zero means it is eligible now (a race the next PickNext resolves).</summary>
    private TimeSpan? NextBackgroundWaitDelay()
    {
        lock (_lock)
        {
            bool anyBackground = _bgByOwner.Values.Any(q => q.Any(s => s.State == SlotState.Queued));
            if (!anyBackground)
            {
                return null;
            }

            TimeSpan remaining = _lastForegroundActivityUtc + IdleDelay() - DateTimeOffset.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>The configured foreground-idle delay before background work runs, read LIVE (it is a machine setting the
    /// settings page can change while the app runs). Exposed so the queue view can show "waiting for idle (Nm)".</summary>
    public TimeSpan IdleDelay() => _options.BackgroundIdleDelay();

    private async Task RunSlotAsync(RenderSlot slot, CancellationToken ct)
    {
        try
        {
            // A cancel can land between the pick and the submit — building a prompt does real work (tag sampling,
            // image fetches), and Cancel cannot mark the worker's slot terminal itself. Honour it BEFORE handing
            // anything to the backend, so a cancelled slot never becomes a render nobody is waiting for.
            if (slot.CancelRequested)
            {
                CancelSlot(slot);
                return;
            }
            // A foreground submit can preempt this background slot before it has been handed to the backend. Return it
            // non-terminal and unsubmitted so the worker requeues it to the background tier — nothing was submitted, so
            // there is nothing to interrupt.
            if (slot.PreemptRequested)
            {
                PreemptSlot(slot);
                return;
            }

            string promptId;
            byte[]? src = null;
            bool resuming = slot.Submitted;   // a rehydrated slot that was mid-render before a restart

            if (resuming)
            {
                promptId = slot.ComfyPromptId ?? throw new InvalidOperationException("A resuming slot must have a prompt id.");
                lock (_lock)
                {
                    _comfyToSlot[promptId] = slot;
                }
            }
            else if (slot.IsEdit)
            {
                EditSpec edit = slot.RequireEdit();
                try
                {
                    src = await GetImageBytesAsync(edit.ImageId, ct);
                }
                catch (RenderInputNotFoundException)
                {
                    FailSlot(slot, $"source image '{edit.ImageId}' not found");
                    return;
                }

                List<ReferenceUpload> references = [];
                foreach (string refId in edit.ReferenceIds ?? [])
                {
                    (byte[] Bytes, string ContentType) refMedia;
                    try
                    {
                        refMedia = await GetImageMediaAsync(refId, ct);
                    }
                    catch (RenderInputNotFoundException)
                    {
                        FailSlot(slot, $"reference '{refId}' not found");
                        return;
                    }

                    // The reference's kind rides its stored blob's content type — the enqueue gate already rejected any
                    // kind this workflow doesn't accept, so an unclassifiable one here is a corrupt input, not policy.
                    if (ReferenceKinds.Classify(refMedia.ContentType) is not { } refKind)
                    {
                        FailSlot(slot, $"reference '{refId}' isn't a recognised image, audio or video file");
                        return;
                    }

                    references.Add(new ReferenceUpload(refMedia.Bytes, refKind, refMedia.ContentType));
                }

                byte[]? maskBytes = null;
                if (!string.IsNullOrEmpty(edit.MaskImageId))
                {
                    try
                    {
                        maskBytes = await GetImageBytesAsync(edit.MaskImageId, ct);
                    }
                    catch (RenderInputNotFoundException)
                    {
                        FailSlot(slot, $"mask image '{edit.MaskImageId}' not found");
                        return;
                    }
                }

                byte[]? lastFrameBytes = null;
                if (!string.IsNullOrEmpty(edit.LastFrameImageId))
                {
                    try
                    {
                        lastFrameBytes = await GetImageBytesAsync(edit.LastFrameImageId, ct);
                    }
                    catch (RenderInputNotFoundException)
                    {
                        FailSlot(slot, $"last-frame image '{edit.LastFrameImageId}' not found");
                        return;
                    }
                }
                // Finalize the instruction for tag-speaking editors (inpaint), as the generate path does. Non-tag
                // editors have no tagging block, so Finalize passes the instruction through unchanged.
                WorkflowInfo? editInfo = _catalog.ResolveInfo(edit.Workflow);

                // Mask routing (implicit wire shape): a Kind=Inpaint workflow consumes the mask IN-GRAPH; a Kind=Edit
                // workflow keeps the mask OUT of the graph and the server composites the masked region back afterwards
                // (below, at the result block). Any other kind carrying a mask is a client bug — throw, never silently
                // ignore. The mask is painted at source resolution, so it must match the source dimensions exactly.
                bool inGraphMask = string.Equals(editInfo?.Kind, WorkflowKindTokens.Inpaint, StringComparison.Ordinal);
                if (inGraphMask && maskBytes is null)
                {
                    // A mask is a REQUIREMENT of an inpaint — the painted region is the whole point. Without this
                    // guard the graph would silently fall back to the source's alpha channel (LoadImageMask's default),
                    // "inpainting" nothing or everything; an inpaint item arriving without a mask fails here instead.
                    FailSlot(slot, $"'{edit.Workflow}' is an inpaint workflow and requires a painted mask, but none was supplied");
                    return;
                }

                if (maskBytes is not null)
                {
                    bool serverComposite = string.Equals(editInfo?.Kind, WorkflowKindTokens.Edit, StringComparison.Ordinal);
                    if (!inGraphMask && !serverComposite)
                    {
                        FailSlot(slot, $"a mask was supplied to '{edit.Workflow}', which is neither an inpaint nor an edit workflow and cannot use one");
                        return;
                    }

                    ImageDimensions maskDims = _media.Identify(maskBytes);
                    ImageDimensions srcDims = _media.Identify(src);
                    if (maskDims.Width != srcDims.Width || maskDims.Height != srcDims.Height)
                    {
                        FailSlot(slot, $"the mask is {maskDims.Width}x{maskDims.Height} but the source image is {srcDims.Width}x{srcDims.Height}; a mask is painted at source resolution and must match it");
                        return;
                    }
                }

                FinalizedPrompt editFinal = PromptFinalizer.Finalize(edit.Instruction, editInfo?.Tagging);
                // The instruction and its negative arrive in marker form and are stored verbatim, exactly as the generate
                // path stores its raw prompt — so an edited image's prompt comes back to the box the way it was written.
                slot.RawPrompt = edit.Instruction;
                slot.RawNegativePrompt = edit.NegativePrompt;
                slot.EffectivePrompt = editFinal.Rendered;
                slot.Marks = editFinal.Marks;
                await _userLog.LogAsync(slot.Job.Owner, LogCategories.SubmitEdit, editFinal.Rendered, ct);
                // Finalize the negative with the SAME rules as the instruction/positive: the negative box shares the
                // tag/artist autocomplete, so its text arrives carrying '#'/'@' markers (and underscores). Without this
                // those markers leak raw into the negative conditioning and degrade output. Marks aren't kept (negatives
                // aren't bookmarkable). Comfy then appends this onto the model's default negative (ComposeNegative).
                string editNeg = PromptFinalizer.Finalize(edit.NegativePrompt, editInfo?.Tagging).Rendered;
                // Kind=Edit composites server-side, so the plain graph must run untouched — the mask never reaches Comfy.
                byte[]? graphMask = inGraphMask ? maskBytes : null;
                SubmitResult editSubmit = await _comfy.SubmitEditAsync(src, editFinal.Rendered, editNeg, edit.Workflow, references, edit.Overrides, graphMask, lastFrameBytes, ct);
                promptId = editSubmit.PromptId;
                slot.EtaSignature = editSubmit.Eta;
                slot.ModelPrompt = editSubmit.ModelPrompt;
                slot.ModelManifest = editSubmit.ModelManifest;
                slot.RenderDimensions = editSubmit.Dimensions;
            }
            else
            {
                // Guard the discriminant once so the whole generate branch reads slot.Gen without re-asserting it.
                if (slot.Gen is null)
                {
                    throw new InvalidOperationException("Slot is not a generate slot.");
                }

                WorkflowInfo? info = _catalog.ResolveInfo(slot.Gen.Workflow);
                // A prose model may opt into random tag generation without claiming booru autocomplete semantics.
                // When this render actually requests it, use neutral marker/folding rules solely so sampled names can
                // travel through the existing raw-prompt provenance path and reach the model as comma-separated ordinary
                // text. Merely enabling the workflow setting does not otherwise alter a prose prompt.
                bool wantsGeneratedPrompt = slot.Gen.RandomPrompt == TriState.True && info?.TagGeneratorEnabled == true;
                WorkflowTagging? promptTagging = info?.Tagging
                    ?? (wantsGeneratedPrompt ? ProseTagGeneratorRules : null);
                // The RAW prompt is the source of truth. It is the marker-form string the user submitted ("#long_hair,
                // @greg_rutkowski"), and the random samplers below APPEND TO IT in that same dialect — so after they run
                // it still reads as something the user could have typed. The rendered prompt and the marks are then
                // derived from it, once, by the finalizer. One direction of transform: nothing downstream ever has to
                // invert a finalized prompt to guess back the markers and underscores it destroyed.
                string raw = slot.Gen.Prompt;
                // What the user put in the NEGATIVE is a standing exclusion for the random samplers: a tag they negated
                // must never be handed back to them as a randomly-chosen positive (same for a negated artist).
                (HashSet<string> Tags, HashSet<string> Artists) negKeys = PromptFinalizer.NegativeKeys(slot.Gen.NegativePrompt);
                // The user's standing bans for THIS workflow, read from the store right here at render time. A ban is a
                // server-side fact, so it is never taken from the request: a caller that omits it (an API-key client, a
                // browser holding a stale ban cache, a job resumed from before the ban) must not be able to generate its
                // way around one. Only fetched when a random sampler is actually going to run — bans bind auto-gen only.
                (HashSet<string> Tags, HashSet<string> Artists) bans = wantsGeneratedPrompt || slot.Gen.RandomArtist == TriState.True
                    ? await BannedKeysAsync(slot.Job.Owner, slot.Model, ct)
                    : (Tags: new HashSet<string>(StringComparer.Ordinal), Artists: new HashSet<string>(StringComparer.Ordinal));
                // Provenance captured EXACTLY as the samplers append — the canonical keys of the tokens they add. The
                // viewer dashes these chips. Taken here, at the append, not reconstructed by diffing OriginalPrompt (which
                // is pre-expansion, so a diff mis-flags wildcard/locked-artist tags as auto). Empty when nothing sampled.
                HashSet<string> generatedTokens = new(StringComparer.Ordinal);
                // Random-prompt: generate the whole prompt PER SLOT from the tag model, seeded by the user's typed text,
                // when this workflow's own toggle permits it. This does NOT fail soft: a tag model that is down or erroring
                // throws out of GenerateAsync and fails the slot (see the catch at the bottom of RunSlotAsync). This is
                // deliberate: silently rendering the typed seed instead of the generated prompt would produce an image
                // the user did not ask for and give no hint why.
                if (wantsGeneratedPrompt && promptTagging is { } generatorTagging)
                {
                    (string? seed, HashSet<string>? suppressKeys) = TagSeed(raw, generatorTagging);
                    HashSet<string> bannedTags = RandomPromptBannedTags(bans, negKeys, suppressKeys);
                    // The generation mask for this slot: the one submitted with it, or the owner's stored mask when the
                    // caller specified none. It rides on the SLOT (unlike the bans, which stay a server-side fact read
                    // fresh here) because it is a composer control now — the chips under the Random prompt slider — so a
                    // queued batch renders under the mask it was submitted with, not whatever the chips say by the time
                    // it comes up. Bounds THIS path only — tag autocomplete is unaffected by it.
                    IReadOnlyList<string> allowedTypes = await AllowedTagTypesAsync(slot.Job.Owner, slot.Gen.TagTypes, ct);
                    IReadOnlyList<string>? gen = await _tagModel.GenerateAsync(seed, slot.Gen.Temperature, bannedTags, allowedTypes, ct);
                    string genOut = gen is null ? "(null)" : string.Join(Format.ListSeparator, gen);
                    // The predictor's in/out goes to the PER-USER ENCRYPTED log and nowhere else. Duplicating it to
                    // the plaintext app log would be one toggle away from writing prompts to disk permanently once a
                    // file sink exists — and the encrypted line below already carries the same content, so that
                    // duplication would buy nothing but the risk.
                    await _userLog.LogAsync(slot.Job.Owner, LogCategories.RandomPrompt, $"IN seed=[{seed}]  OUT=[{genOut}]", ct);
                    if (gen is { Count: > 0 })
                    {
                        // Appended on the canonical token in marker form: the finalizer renders it (folding underscores
                        // per the model's rules) and marks it, so the sampled names are chips exactly like the typed
                        // ones — '@' for the artist-type names, '#' for the rest, per the same catalog the chips take
                        // their category from.
                        List<string> additions = PromptFinalizer.MarkSampled(gen, bannedTags, _tags.IsArtist);
                        if (additions.Count > 0)
                        {
                            raw = PromptFinalizer.Append(raw, string.Join(Format.ListSeparator, additions));
                            // The additions are marker-form ("#long_hair"/"@kazaana"); their canonical key is the mark key.
                            foreach (string token in additions)
                            {
                                _ = generatedTokens.Add(PromptFinalizer.Normalize(token));
                            }
                        }
                    }
                }
                // Random-artist: pick a fresh artist PER SLOT (so a batch gets a different one per image), model permitting.
                if (slot.Gen.RandomArtist == TriState.True && info?.Tagging is { Artists: true })
                {
                    HashSet<string> bannedArtists = bans.Artists;
                    bannedArtists.UnionWith(negKeys.Artists);
                    string? artist = _tags.RandomArtist(bannedArtists.Count > 0 ? bannedArtists : null);
                    if (!string.IsNullOrEmpty(artist))
                    {
                        string artistKey = PromptFinalizer.Normalize(artist);
                        raw = PromptFinalizer.Append(raw, PromptMarkers.ArtistMarker + artistKey);
                        _ = generatedTokens.Add(artistKey);
                    }
                }
                // The single derivation: the prompt the model renders and the marks that describe it both come from the
                // raw string we are about to store, so the three can never disagree.
                FinalizedPrompt final = PromptFinalizer.Finalize(raw, promptTagging);
                slot.RawPrompt = raw;
                // The negative is stored exactly as submitted. The random samplers never touch it (they only ever ADD a
                // positive), so verbatim here is simply what the user typed — null when they typed nothing, which is
                // what leaves the model's built-in default negative standing alone.
                slot.RawNegativePrompt = slot.Gen.NegativePrompt;
                slot.EffectivePrompt = final.Rendered;
                slot.Marks = final.Marks;
                slot.GeneratedTokens = generatedTokens.Count > 0 ? generatedTokens : null;
                await _userLog.LogAsync(slot.Job.Owner, LogCategories.Submit, final.Rendered, ct);
                // Finalize the negative with the same tag rules as the positive (the negative box shares the tag/artist
                // autocomplete, so its text carries '#'/'@' markers). Comfy appends this onto the model's default negative.
                string genNeg = PromptFinalizer.Finalize(slot.Gen.NegativePrompt, promptTagging).Rendered;
                SubmitResult submit = await _comfy.SubmitGenerateAsync(final.Rendered, genNeg, slot.Gen.Workflow, slot.Gen.Aspect, slot.Gen.Overrides, slot.Gen.Loras, ct);
                promptId = submit.PromptId;
                slot.EtaSignature = submit.Eta;
                slot.ModelPrompt = submit.ModelPrompt;
                slot.ModelManifest = submit.ModelManifest;
                slot.RenderDimensions = submit.Dimensions;
            }

            if (!resuming)
            {
                // The prompt is with the backend — our fair-queue wait is over. Stamp submit time + expected render
                // seconds (this machine's recent average, or null the first time) for the ETA. This is not the same
                // as the slot RUNNING: the backend decides when it starts executing, and says so on the next poll.
                DateTimeOffset startedAt = DateTimeOffset.UtcNow;
                double? expected = null;
                try
                {
                    // EXACT-matched ETA ONLY: the plain average of recent samples whose signature equals this
                    // request's resolution/steps/frames. No fallback and no scaling of near-miss samples — a
                    // signature with no matching history shows NO ETA rather than a wrong number.
                    double? avgMs = slot.EtaSignature is { } sig
                        ? await _timings.EtaAverageMsAsync(_machine, slot.Model, sig, 10, ct)
                        : null;
                    expected = avgMs is double averageMilliseconds ? averageMilliseconds / 1000.0 : null;
                }
                catch (Exception ex)
                {
                    // The ETA is a decoration on a render that is already submitted; losing it must not fail the
                    // render. But a timings table that has stopped answering is worth knowing about; saying nothing
                    // would leave "no model ever shows an ETA" with no trail leading anywhere.
                    _log.LogWarning(ex, "ETA lookup failed for {Model}; this slot renders without one.", slot.Model);
                }

                lock (_lock)
                {
                    slot.ComfyPromptId = promptId;
                    _comfyToSlot[promptId] = slot;
                    slot.GenStartedAt = startedAt;
                    slot.ExpectedGenSeconds = expected;
                }

                _ = await PersistAsync(slot.Job);   // record the promptId, so a restart resumes this render instead of redoing it
            }

            // Poll for the result; no deadline. Ends on completion, a user cancel, the backend losing the prompt, or shutdown.
            GeneratedImage? img = null;
            while (!ct.IsCancellationRequested)
            {
                if (slot.CancelRequested || slot.PreemptRequested)
                {
                    break;
                }

                await Task.Delay(1500, ct);
                RenderPollResult poll = await _comfy.PollResultAsync(promptId, ct);
                if (poll.State == RenderPollState.Unavailable)
                {
                    // A failed history request is not evidence that the prompt is absent. In particular, do not pair
                    // it with an empty /queue response and increment the vanish counter: the prompt may have finished
                    // and be waiting in history while that endpoint is temporarily unhealthy.
                    continue;
                }

                if (poll.State == RenderPollState.Ready)
                {
                    img = poll.Image ?? throw new InvalidOperationException(
                        "The renderer reported a ready prompt without an image.");
                    break;
                }

                BackendQueue? backend = await _comfy.GetQueueAsync(ct);
                if (backend is null)
                {
                    continue;                                // backend unreachable -> unknown, keep waiting
                }
                // The one place a slot becomes (or stops being) "running": the backend's own account of what its GPU
                // is executing. A prompt merely sitting in its queue is still waiting, and says so.
                ObserveExecuting(slot, backend.Executing.Contains(promptId));
                if (backend.Has(promptId))
                {
                    slot.MissedLivenessChecks = 0;
                    continue;
                }

                if (++slot.MissedLivenessChecks >= LivenessVanishThreshold)
                {
                    FailSlot(slot, "the renderer no longer has this job (it likely restarted)");
                    return;
                }
            }

            if (img is null)
            {
                // Preempted by a foreground submit (and not also cancelled — a user cancel is terminal and wins): stop
                // the GPU and return the slot NON-terminal and FRESH, so it re-renders from scratch on the next idle
                // window. It is never marked Cancelled/Error — a preempted background render was not stopped because
                // anything went wrong, and the requeue routes it back to the background tier.
                if (slot.PreemptRequested && !slot.CancelRequested)
                {
                    if (slot.Submitted)
                    {
                        // The enqueue that preempted this fired the interrupt already; fire it again defensively in case
                        // the slot only reached the backend afterwards. Idempotent: the foreground slot is submitted
                        // only after this method returns, so nothing else of ours is on the GPU to disturb.
                        try
                        {
                            await _comfy.InterruptAsync(CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            _log.LogError(ex, "Preempt for slot {Index}: the backend interrupt failed; its render may still be running.", slot.Index);
                        }
                    }

                    PreemptSlot(slot);
                    return;
                }
                // Cancelled in the window between the pre-submit check and the prompt reaching the backend: Cancel saw
                // nothing to interrupt, so stop the render we went on to start rather than leave it running for nobody.
                // Guarded on the user's cancel, not on `ct` — a shutdown leaves the prompt alone so a restart resumes it.
                if (slot.CancelRequested && slot.Submitted)
                {
                    // The slot resolves as cancelled either way; the interrupt is what stops the GPU from finishing a
                    // render nobody will collect. If it does not land, say so — do not drop the reason.
                    try
                    {
                        await _comfy.InterruptAsync(CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Late cancel for slot {Index}: the backend interrupt failed; its render may still be running.", slot.Index);
                    }
                }

                CancelSlot(slot);
                return;
            }

            // Success — record the actual render duration (submit -> image; queue wait excluded) for future ETAs.
            // A resumed prompt deliberately has no local start timestamp: the pre-restart value includes application
            // downtime and cannot produce a truthful sample. Null means skip, never fabricate a near-zero duration.
            int? completedMs = CompletedTimingMilliseconds(slot.GenStartedAt, DateTimeOffset.UtcNow);
            if (completedMs is int elapsedMs)
            {
                try
                {
                    EtaSignature? etaSig = slot.EtaSignature;
                    await _timings.AddAsync(new GenTimingEntry(_machine, slot.Model, slot.IsEdit, elapsedMs,
                        etaSig?.Width, etaSig?.Height, etaSig?.Steps, etaSig?.Frames), ct);
                    // The sample is persisted — flush the averages snapshot so the next /forge/workflows and /forge/queue
                    // read reflects it. Done AFTER the insert so the rebuild can't race it and re-cache the old averages.
                    _timingAverages.Invalidate();
                }
                catch (Exception ex)
                {
                    // Telemetry, and the image is already rendered — this must not fail the slot. It must still be
                    // recorded: these samples are what every future ETA is computed from, so losing them silently
                    // degrades the ETAs of every later render with nothing to attribute it to.
                    _log.LogWarning(ex, "Render-timing sample could not be recorded for {Model}.", slot.Model);
                }
            }

            // The artifact's media type is the file ComfyUI wrote: a still (.png), the silent animated-webp clip most
            // video models save (.webp), or a real mp4 CONTAINER — MiniMax-H3 alone saves an mp4 with a baked-in stereo
            // AUDIO track (webp can't carry audio; see MiniMaxH3Workflows -> SaveVideo). Content type follows the
            // container; it rides through the blob and the serve path (/image/{id}, /image/{id}/mp4 pass-through) with
            // the audio intact and never re-transcoded.
            bool isMp4 = img.Filename.EndsWith(MediaFileExtensions.Mp4, StringComparison.OrdinalIgnoreCase);
            bool isVideo = isMp4 || img.Filename.EndsWith(MediaFileExtensions.Webp, StringComparison.OrdinalIgnoreCase);
            string contentType = isMp4 ? "video/mp4" : isVideo ? "image/webp" : "image/png";

            // A workflow that DECLARES video must have produced a clip. SaveAnimatedWEBP writes a .webp whether it was
            // handed one frame or forty, so the extension says nothing — a single-frame still can come back from a
            // workflow that asked for many frames and still read as "done", surfacing only later as an unreadable
            // source in an editor that consumes clips. A render that did not make the thing it exists to make is a
            // failed render. An mp4 is a video container by construction (CreateVideo), so it counts as a clip.
            WorkflowInfo? declared = _catalog.ResolveInfo(slot.IsEdit ? slot.RequireEdit().Workflow : slot.RequireGen().Workflow);
            if (declared?.ProducesVideo == true && !(isMp4 || _media.IsAnimatedWebp(img.Png)))
            {
                throw new RenderValidationException(
                    "This is a video workflow and the render came back as a single frame, not a clip. "
                    + "The frame count reaching the graph is the thing to look at.");
            }
            // An output whose header will not read is a FAILED render, not a 0x0 image. Substituting (0, 0) here would
            // write a fabricated size into the blob row and into history, where nothing downstream could tell it from a
            // real measurement. Let it throw: the handler at the bottom of this method fails the slot with the real reason.
            // ImageSharp reads a still/webp; an mp4 needs the container's own box tree (ImageSharp can't read it).
            ImageDimensions dims = isMp4 ? _media.IdentifyVideo(img.Png) : _media.Identify(img.Png);
            (int w, int h) = (dims.Width, dims.Height);

            if (slot.IsEdit)
            {
                EditSpec edit = slot.RequireEdit();
                WorkflowInfo? editInfo = _catalog.ResolveInfo(edit.Workflow);
                // A mask on a Kind=Edit item means the plain graph ran the whole canvas and the server pastes the masked
                // region back over the source HERE — for a fresh render and a resumed one alike (the resumed slot never
                // reloaded the source, so it is re-fetched below). Kind=Inpaint consumed the mask in-graph already.
                string maskImageId = edit.MaskImageId ?? "";
                bool serverComposite = !isVideo
                    && string.Equals(editInfo?.Kind, WorkflowKindTokens.Edit, StringComparison.Ordinal)
                    && maskImageId.Length > 0;

                // The fresh path has the source in scope; the composite path can re-fetch it. A resumed NON-composite
                // edit has neither and is stored as-is by the generic path below, exactly as before.
                if (src is not null || serverComposite)
                {
                    byte[] outBytes = img.Png;
                    if (serverComposite)
                    {
                        byte[] original = src ?? await GetImageBytesAsync(edit.ImageId, ct);
                        byte[] maskPng = await GetImageBytesAsync(maskImageId, ct);
                        outBytes = _media.CompositeMasked(original, outBytes, maskPng, CompositeMaskGrowPx, CompositeMaskBlurPx);
                        // The composite is re-encoded PNG at the ORIGINAL (source) dimensions, which differ from the
                        // edit's bucket dims — re-read them so history records the size actually stored.
                        ImageDimensions cd = _media.Identify(outBytes);
                        (w, h) = (cd.Width, cd.Height);
                    }

                    // A video has no still pHash to compare, so its diff is null (no score is recorded and the gate
                    // below skips it — a video edit is never declared "no change"). For a still it is MEASURED against
                    // the source; a comparison that cannot run fails the slot rather than defaulting past the gate.
                    // Also null when there is no source to compare (a resumed composite — the gate is skipped anyway).
                    double? diff = src is null || isVideo ? null : _media.Difference(src, img.Png);
                    // Some edits intentionally preserve composition (inpaint; pixel transforms), and a server composite
                    // keeps all but the painted region — both read a tiny whole-image diff BY DESIGN, so both opt out of
                    // the no-change gate.
                    bool preservesComposition = editInfo?.PreservesComposition ?? false;
                    if (!preservesComposition && !serverComposite && diff is double d && d < _media.NoChangeThreshold)
                    {
                        SlotEditNoChange(slot, Math.Round(d, 3));
                        return;
                    }

                    string editId = await StoreImageAsync(outBytes, contentType, w, h, ct);
                    await PersistSpriteDataAsync(editId, img, ct);
                    if (!await TryWriteHistoryAsync(slot, editId, w, h, ct))
                    {
                        return;
                    }

                    SlotDone(slot, editId, w, h, changed: true, score: diff is double sd ? Math.Round(sd, 3) : null);
                    return;
                }
            }

            string id = await StoreImageAsync(img.Png, contentType, w, h, ct);
            await PersistSpriteDataAsync(id, img, ct);
            if (!await TryWriteHistoryAsync(slot, id, w, h, ct))
            {
                return;
            }

            SlotDone(slot, id, w, h);
        }
        catch (RenderValidationException ex)
        {
            FailSlot(slot, ex.Message);
        }
        // The user asked to stop. Shutdown ALSO cancels, and that must not resolve the slot: it is accepted work that
        // a restart resumes, and marking it cancelled because the process is going down discards it.
        catch (OperationCanceledException) when (slot.CancelRequested)
        {
            CancelSlot(slot);
        }
        catch (OperationCanceledException)
        {
            _log.LogInformation("Slot {Index} of job {JobId} released on shutdown; it stays queued and resumes on restart.",
                slot.Index, slot.Job.JobId);
        }
        // Safety net for a database touch this method does not already wrap: the storage layer being out of range is
        // not a property of this render, so the slot is HELD (left non-terminal) and the worker puts it back.
        catch (Exception ex) when (_db.IsUnavailable(ex))
        {
            _log.LogWarning(ex, "Slot {Index} of job {JobId} is held: the database is unreachable. It has not failed.",
                slot.Index, slot.Job.JobId);
        }
        catch (Exception ex)
        {
            FailSlot(slot, ex.Message);
        }
    }

    /// <summary>A truthful completed-render duration, or null when this process did not observe the submit time. The
    /// latter is the resumed-render case: using the persisted timestamp would include restart downtime.</summary>
    internal static int? CompletedTimingMilliseconds(DateTimeOffset? startedAt, DateTimeOffset finishedAt) =>
        startedAt is { } started
            ? (int)Math.Clamp((finishedAt - started).TotalMilliseconds, 0, int.MaxValue)
            : null;

    /// <summary>
    /// Store a finished render's bytes, waiting out an unreachable database.
    /// <para>This is the single worst place to fail. The GPU work is done, the image exists in memory, and the only
    /// thing standing between it and the library is a write. Failing the slot here throws away the expensive part for
    /// a reason that resolves itself — which is exactly what happens when the machine drives out of range of the
    /// server mid-batch.</para>
    /// </summary>
    private Task<string> StoreImageAsync(byte[] bytes, string contentType, int width, int height, CancellationToken ct) =>
        AwaitingDatabaseAsync(
            c => _blobs.AddAsync(new NewImageBlob(bytes, contentType, width, height, ImageBlobKind.Generated), c),
            "storing a finished render", ct);

    /// <summary>Persist a pixel-quantize generation's derived palette, fp label frequencies, and native-res lossless
    /// frames next to the stored image. No-op when the generation carried none of them.</summary>
    private async Task PersistSpriteDataAsync(string imageId, GeneratedImage img, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(img.PaletteJson))
        {
            await _blobs.SetPaletteAsync(imageId, img.PaletteJson, ct);
        }

        if (!string.IsNullOrEmpty(img.FrequenciesJson))
        {
            await _blobs.SetFrequenciesAsync(imageId, img.FrequenciesJson, ct);
        }

        if (img.LosslessFrames is { Count: > 0 } frames)
        {
            await _frames.AddFramesAsync(imageId, frames, ct);
        }
    }

    #endregion

    #region slot transitions (under lock)

    /// <summary>
    /// Record what the backend just said about the worker's slot. This is the ONLY writer of
    /// <see cref="SlotState.Running"/>: a slot runs because the backend reports it executing, never because we picked
    /// it, submitted it, or saw it running a minute ago. Symmetric on purpose — the moment the backend stops saying
    /// so, the slot goes back to waiting.
    /// <para>Guarded on the worker's own slot, which makes "at most one running" structural rather than hoped-for: a
    /// second caller would be a bug in the worker loop, so it throws instead of quietly putting two images on one GPU.</para>
    /// </summary>
    private void ObserveExecuting(RenderSlot slot, bool executing)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(_running, slot))
            {
                throw new InvalidOperationException(
                    $"Slot {slot.Job.JobId}#{slot.Index} is not the worker's current slot; only it may be Running.");
            }

            if (slot.Terminal)
            {
                return;
            }

            slot.State = executing ? SlotState.Running : SlotState.Queued;
        }
    }

    private void SlotDone(RenderSlot slot, string id, int w, int h, bool changed = true, double? score = null)
    {
        lock (_lock)
        {
            if (slot.Terminal)
            {
                return;   // cancelled while the result was landing — don't resurrect it as Done
            }

            slot.ImageId = id;
            slot.Width = w;
            slot.Height = h;
            if (slot.RenderDimensions is { } dimensions)
            {
                slot.RenderDimensions = dimensions with { Output = new PixelDimensions(w, h) };
            }
            // Only an edit slot carries an outcome; a generate's default (changed, no score) has nowhere to go and needs none.
            if (slot.EditResult is { } outcome)
            {
                outcome.Changed = changed;
                outcome.ChangeScore = score;
            }

            slot.State = SlotState.Done;
        }
    }

    private void SlotHistoryFailed(RenderSlot slot, string imageId, int width, int height)
    {
        lock (_lock)
        {
            ApplyHistoryWriteFailure(slot, imageId, width, height);
        }
    }

    /// <summary>Keep the already-stored image addressable from the failed job while making the durable-history defect
    /// visible. The slot must not read Done: there is no history row through which the library can collect the blob.</summary>
    internal static void ApplyHistoryWriteFailure(RenderSlot slot, string imageId, int width, int height)
    {
        if (slot.Terminal)
        {
            return;
        }

        slot.ImageId = imageId;
        slot.Width = width;
        slot.Height = height;
        if (slot.RenderDimensions is { } dimensions)
        {
            slot.RenderDimensions = dimensions with { Output = new PixelDimensions(width, height) };
        }

        slot.Error = "The image was saved, but it could not be added to history. It remains available from this job.";
        slot.State = SlotState.Error;
    }

    private void SlotEditNoChange(RenderSlot slot, double score)
    {
        lock (_lock)
        {
            if (slot.Terminal)
            {
                return;
            }

            EditResult outcome = slot.EditResult ?? throw new InvalidOperationException("SlotEditNoChange on a non-edit slot.");
            outcome.Changed = false;
            outcome.ChangeScore = score;
            slot.State = SlotState.Done;
        }
    }

    private void FailSlot(RenderSlot slot, string error)
    {
        lock (_lock)
        {
            if (slot.Terminal)
            {
                return;
            }

            slot.Error = error;
            slot.State = SlotState.Error;
        }
    }

    /// <summary>Resolve a slot the user stopped. Terminal like <see cref="FailSlot"/>, but as its own state: nothing
    /// went wrong, and the reason string is kept as the detail rather than as the only place the difference lived.</summary>
    private void CancelSlot(RenderSlot slot)
    {
        lock (_lock)
        {
            if (slot.Terminal)
            {
                return;
            }

            slot.Error = "cancelled";
            slot.State = SlotState.Cancelled;
        }
    }

    /// <summary>
    /// Return a preempted background slot to a fresh, NON-terminal <see cref="SlotState.Queued"/> state — the whole
    /// point of the halt/requeue: the partial in-flight render is discarded, the slot's submission state is cleared so
    /// it renders from scratch next time (a null <c>ComfyPromptId</c> makes <see cref="RenderSlot.Submitted"/> false),
    /// and the worker loop then requeues it to the background tier. Distinct from <see cref="CancelSlot"/>, which is
    /// terminal: a preempted slot has not failed and was not cancelled — it is simply waiting for the next idle window.
    /// </summary>
    private void PreemptSlot(RenderSlot slot)
    {
        lock (_lock)
        {
            if (slot.Terminal)
            {
                return;
            }

            if (slot.ComfyPromptId is { } c)
            {
                _ = _comfyToSlot.Remove(c);
            }

            slot.ComfyPromptId = null;
            slot.GenStartedAt = null;
            slot.ExpectedGenSeconds = null;
            slot.EtaSignature = null;
            slot.StepFraction = null;
            slot.MissedLivenessChecks = 0;
            slot.PreemptRequested = false;
            slot.State = SlotState.Queued;
        }
    }

    /// <summary>After a slot resolves: write the job through. Once every slot is terminal, exactly one final-persistence
    /// driver owns the durable write; rejection leaves the terminal outcome resident and schedules retries. The job is
    /// removed from memory only after its terminal row succeeds.</summary>
    private async Task AfterSlotAsync(RenderJob job)
    {
        bool terminal;
        bool ownsFinalDriver = false;
        lock (_lock)
        {
            terminal = job.AllTerminal;
            if (terminal)
            {
                job.FinishedAt ??= DateTimeOffset.UtcNow;
                ownsFinalDriver = _finalPersistenceDrivers.Add(job.JobId);
            }
        }

        if (!terminal)
        {
            _ = await PersistAsync(job);
            return;
        }

        if (!ownsFinalDriver)
        {
            return;
        }

        if (await PersistAsync(job) is null)
        {
            try
            {
                await RemoveDurablyFinalizedJobAsync(job);
            }
            finally
            {
                lock (_lock)
                {
                    _ = _finalPersistenceDrivers.Remove(job.JobId);
                }
            }
        }
        else
        {
            _log.LogWarning("Final persistence for job {JobId} was rejected; its terminal result remains live and will be retried.", job.JobId);
            _ = RetryFinalPersistenceAsync(job);
        }
    }

    /// <summary>Health driver for a rejected terminal write. Non-database defects are retried with a capped backoff;
    /// database outages are already waited out inside <see cref="PersistAsync"/>. The driver is deliberately detached:
    /// startup rehydration and request threads must remain responsive while the terminal outcome stays queryable.</summary>
    private async Task RetryFinalPersistenceAsync(RenderJob job)
    {
        TimeSpan delay = TimeSpan.FromSeconds(1);
        try
        {
            while (true)
            {
                await Task.Delay(delay);
                lock (_lock)
                {
                    if (!_jobs.TryGetValue(job.JobId, out RenderJob? current) || !ReferenceEquals(current, job))
                    {
                        return;
                    }
                }

                if (await PersistAsync(job) is null)
                {
                    await RemoveDurablyFinalizedJobAsync(job);
                    return;
                }

                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
                _log.LogWarning("Final persistence for job {JobId} is still pending; retrying in {Delay}s.",
                    job.JobId, delay.TotalSeconds);
            }
        }
        catch (Exception ex)
        {
            // PersistAsync converts repository failures to results; this catches only a defect in the retry driver
            // itself. Leave the job resident and loud rather than allowing an unobserved task fault to hide it.
            _log.LogError(ex, "Final persistence retry driver failed for job {JobId}; the terminal result remains live.", job.JobId);
        }
        finally
        {
            lock (_lock)
            {
                _ = _finalPersistenceDrivers.Remove(job.JobId);
            }
        }
    }

    /// <summary>Drop a terminal job from the live cache only after its terminal row has been accepted.</summary>
    private async Task RemoveDurablyFinalizedJobAsync(RenderJob job)
    {
        // Now that the write-through is done and this job will never upsert again, drop any slot whose image the
        // user deleted while the batch was still running (the delete cascade has to leave live slots alone).
        try
        {
            await _jobRepo.SweepDeletedImageSlotsAsync(job.JobId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Slot sweep failed for job {JobId}", job.JobId);
        }

        lock (_lock)
        {
            _ = _jobs.Remove(job.JobId);
            foreach (RenderSlot s in job.Slots)
            {
                if (s.ComfyPromptId is { } c)
                {
                    _ = _comfyToSlot.Remove(c);
                }
            }
        }
    }

    /// <summary>
    /// Persist a produced image to the user's history — the single, server-side, exactly-once write, done BEFORE the
    /// slot is marked Done.
    /// <para>An unreachable database is WAITED OUT, not logged past. "Best-effort" is right for a write that failed
    /// on its own merits, but treating an outage that way leaves an image sitting in blob storage with no history
    /// row: it exists, and it is invisible in the library, permanently. The bytes survived and the record of them
    /// didn't, which is the worse half of losing it.</para>
    /// </summary>
    private async Task WriteHistoryAsync(RenderSlot slot, string imageId, CancellationToken ct)
    {
        string modelId = slot.Model;
        string friendly = _catalog.ResolveInfo(modelId)?.FriendlyName ?? modelId;
        // The raw (marker-form) prompt falls back to the submitted spec only for a slot that produced an image
        // without going through RunSlotAsync's prompt build — the same fallback shape the EffectivePrompt line uses.
        string raw = slot.RawPrompt ?? (slot.IsEdit ? slot.RequireEdit().Instruction : slot.RequireGen().Prompt);
        string? rawNegative = slot.RawNegativePrompt ?? (slot.IsEdit ? slot.RequireEdit().NegativePrompt : slot.RequireGen().NegativePrompt);
        string prompt = slot.EffectivePrompt ?? raw;
        // What the user typed travels separately for a generate because enqueue resolves {a|b}/{{a|b}} before the
        // slot runs. Edits retain their resolved instruction here, matching their pre-existing history contract.
        string? original = slot.IsEdit ? slot.RequireEdit().Instruction : slot.RequireGen().OriginalPrompt;
        // A generate that reached here rendered, and a render only happens once NormalizeAspect has accepted the
        // aspect at submit (it throws on anything but square/landscape/portrait) — so a null here is not a missing
        // value to fill with "square", it is a broken invariant. Edits carry no aspect: "" is their real value.
        string aspect = slot.IsEdit ? "" : (slot.RequireGen().Aspect
            ?? throw new InvalidOperationException("A rendered generate reached history with no aspect, which NormalizeAspect should have made impossible at submit."));
        IReadOnlyList<Mark> marks = slot.Marks is not { Count: > 0 }
            ? Array.Empty<Mark>()
            : slot.Marks.Select(kv => new Mark(kv.Key, TokenKindWire.Parse(kv.Value), slot.GeneratedTokens?.Contains(kv.Key) == true)).ToList();
        // The user LoRA stack this image was generated with (generates only). Recorded so the viewer lists them
        // and Reload reproduces the exact stack; empty for edits and for generations that used none.
        IReadOnlyList<LoraSelection>? genLoras = slot.IsEdit ? null : slot.RequireGen().Loras;
        IReadOnlyList<HistoryLora> loras = genLoras is not { Count: > 0 }
            ? Array.Empty<HistoryLora>()
            : genLoras.Select(l => new HistoryLora(l.Name, l.Weight)).ToList();

        HistoryEntry entry = new()
        {
            UserId = slot.Job.Owner,
            GatewayImageId = imageId,
            Prompt = prompt,
            RawPrompt = raw,
            RawNegativePrompt = rawNegative,
            OriginalPrompt = original,
            ModelFriendly = friendly,
            ModelId = modelId,
            Aspect = aspect,
            CreatedAtUtc = DateTime.UtcNow,
            Marks = marks,
            Loras = loras,
        };

        // A fresh scope per ATTEMPT: the repository is scoped, and a scope built before an outage would be reused
        // across every retry of a wait that can span the whole outage.
        await AwaitingDatabaseAsync(async c =>
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            _ = await scope.ServiceProvider.GetRequiredService<IHistoryRepository>().AddAsync(entry, c);
        }, $"recording image {imageId} in history", ct);
    }

    /// <summary>Record history before declaring success. Non-outage data defects are terminal and visible, while the
    /// stored image id remains on the slot so it can still be opened from the job result.</summary>
    private async Task<bool> TryWriteHistoryAsync(RenderSlot slot, string imageId, int width, int height, CancellationToken ct)
    {
        try
        {
            await WriteHistoryAsync(slot, imageId, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !_db.IsUnavailable(ex))
        {
            _log.LogError(ex, "History write failed for image {ImageId} (job {JobId}); the slot will expose the defect.",
                imageId, slot.Job.JobId);
            SlotHistoryFailed(slot, imageId, width, height);
            return false;
        }
    }

    #endregion

    #region persistence (write-through)

    /// <summary>Write the job through. Returns the actual failure (null on success), so the submission boundary can
    /// surface its provider detail and a caller about to DISCARD the in-memory job can decline to — on the finalizing
    /// write, memory holds the only copy of the outcome.</summary>
    private async Task<Exception?> PersistAsync(RenderJob job)
    {
        JobRecord rec;
        lock (_lock)
        {
            rec = ToRecord(job);
        }

        try
        {
            // Waits out an unreachable database rather than reporting the write as failed. Non-terminal transitions
            // rely on a later state change to carry a rejected write; a terminal transition is different and has the
            // explicit RetryFinalPersistenceAsync driver, which retains the visible result until this succeeds.
            await AwaitingDatabaseAsync(
                ct => _jobRepo.UpsertAsync(rec, ct), $"persisting job {job.JobId}", CancellationToken.None);
            return null;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Job persist failed for {JobId} ({Slots} slots, status {Status}).",
                job.JobId, rec.Slots.Count, rec.Status);
            return ex;
        }
    }

    /// <summary>
    /// Run a database operation, WAITING OUT an unreachable database instead of failing the work it belongs to.
    /// <para>A database outage is not a rendering failure. Failing is only the right answer when there is something
    /// else the app could do instead, and with no database there is nothing — no other source of truth, no degraded
    /// mode worth having. Every job is equally affected and the condition resolves itself, so accepted work waits.
    /// Refusing NEW submissions during an outage is fine; discarding work already accepted is not.</para>
    /// <para>Non-blocking, unbounded, and loud: the same capped backoff the startup rehydrate uses (5s doubling to a
    /// 5-minute ceiling), with a log line each round naming what is held and how long it has been waiting — so a
    /// stalled queue reads as "waiting for the database" and is never mistaken for a hang.</para>
    /// <para>Anything the probe does NOT recognise as unreachable propagates immediately. Fail-fast stays correct
    /// for real failures; this is only about the storage layer being out of range.</para>
    /// </summary>
    private async Task<T> AwaitingDatabaseAsync<T>(Func<CancellationToken, Task<T>> operation, string what, CancellationToken ct)
    {
        TimeSpan delay = TimeSpan.FromSeconds(5);
        DateTimeOffset since = DateTimeOffset.UtcNow;
        while (true)
        {
            try
            {
                return await operation(ct);
            }
            catch (Exception ex) when (_db.IsUnavailable(ex) && !ct.IsCancellationRequested)
            {
                _log.LogWarning(ex,
                    "Database unreachable while {What}. Holding the work (not failing it) and retrying in {Delay}s; waiting {Waited} so far.",
                    what, delay.TotalSeconds, DateTimeOffset.UtcNow - since);
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 300));
            }
        }
    }

    /// <summary>Void-returning form of <see cref="AwaitingDatabaseAsync{T}"/>.</summary>
    private async Task AwaitingDatabaseAsync(Func<CancellationToken, Task> operation, string what, CancellationToken ct) =>
        await AwaitingDatabaseAsync<object?>(async c =>
        {
            await operation(c);
            return null;
        }, what, ct);

    /// <summary>Snapshot the in-memory job into its durable record. Called under _lock. Status derives from the slots:
    /// Active until all terminal, then Done if anything was produced, else Error.</summary>
    private static JobRecord ToRecord(RenderJob j)
    {
        JobRecord rec = new()
        {
            JobId = j.JobId,
            UserId = j.Owner,
            MachineName = j.MachineName,
            // Slot 0's workflow id and prompt, both honestly non-null by the time a job exists: the workflow is
            // validated non-blank at every entry (ForgeApi, batch, requeue), and the prompt/instruction is coalesced to
            // "" at the wire→domain boundary (an empty prompt, or an instruction-less editor, is a real value). No
            // coalesce here — that would only paper over a null the boundary has already ruled out.
            Model = j.Model,
            Prompt = j.Prompt,
            Total = j.Total,
            Status = RenderPhases.Persisted(j),
            CreatedAtUtc = j.CreatedAt.UtcDateTime,
            FinishedAtUtc = j.FinishedAt?.UtcDateTime,
        };
        foreach (RenderSlot? s in j.Slots.OrderBy(s => s.Index))
        {
            rec.Slots.Add(new JobSlotRecord
            {
                JobId = j.JobId,
                SlotIndex = s.Index,
                IsEdit = s.IsEdit,
                IsBackground = s.IsBackground,
                // A running slot persists as Queued. "Running" is a live fact about a GPU that this row cannot keep
                // true past the process's life, and writing it anyway would leave every crashed or orphaned job
                // claiming to render forever. Resuming needs no more than what's already here: non-terminal, plus the
                // ComfyPromptId to pick the poll back up.
                State = RenderPhases.Persisted(s.State),
                ComfyPromptId = s.ComfyPromptId,
                ImageId = s.ImageId,
                Width = s.Width == 0 ? null : s.Width,
                Height = s.Height == 0 ? null : s.Height,
                Error = s.Error,
                EffectivePrompt = s.EffectivePrompt,
                ModelPrompt = s.ModelPrompt,
                ModelManifestJson = s.ModelManifest is null ? null : JsonSerializer.Serialize(s.ModelManifest),
                RenderDimensionsJson = s.RenderDimensions is null ? null : JsonSerializer.Serialize(s.RenderDimensions),
                RawPrompt = s.RawPrompt,
                RawNegativePrompt = s.RawNegativePrompt,
                Marks = s.Marks is null ? [] : [.. s.Marks.Select(kv => new Mark(kv.Key, TokenKindWire.Parse(kv.Value), s.GeneratedTokens?.Contains(kv.Key) == true))],
                GenStartedAtUtc = s.GenStartedAt?.UtcDateTime,
                ExpectedGenSeconds = s.ExpectedGenSeconds,
                // The spec, field by field — stored as columns rather than one blob, with the ids left legible so the
                // database can join and cascade on them. The mode-specific columns are grouped: exactly one of the two
                // is populated, by mode, and each field is absent (not forced-null) from the other mode's slot.
                Workflow = s.RehydrateFallback is { } fallbackWorkflow ? fallbackWorkflow.Workflow : s.IsEdit ? s.RequireEdit().Workflow : s.RequireGen().Workflow,
                Prompt = s.RehydrateFallback is { } fallbackPrompt ? fallbackPrompt.Prompt : s.IsEdit ? s.RequireEdit().Instruction : s.RequireGen().Prompt,
                NegativePrompt = s.RehydrateFallback is { } fallbackNegative ? fallbackNegative.NegativePrompt : s.IsEdit ? s.RequireEdit().NegativePrompt : s.RequireGen().NegativePrompt,
                OverridesJson = s.RehydrateFallback is { } fallbackOverrides ? fallbackOverrides.OverridesJson : OverridesJsonOf(s),
                LorasJson = s.RehydrateFallback is { } fallbackLoras ? fallbackLoras.LorasJson : LorasJsonOf(s),
                Generate = s.RehydrateFallback is { } fallbackGenerate ? fallbackGenerate.Generate : s.IsEdit ? null : new GenerateSlotData
                {
                    Aspect = s.RequireGen().Aspect,
                    RandomArtist = s.RequireGen().RandomArtist,
                    RandomPrompt = s.RequireGen().RandomPrompt,
                    Temperature = s.RequireGen().Temperature,
                    TagTypesJson = s.RequireGen().TagTypes is null ? null : JsonSerializer.Serialize(s.RequireGen().TagTypes),
                },
                Edit = s.RehydrateFallback is { } fallbackEdit ? fallbackEdit.Edit : !s.IsEdit ? null : new EditSlotData
                {
                    Changed = s.EditResult?.Changed ?? true,
                    ChangeScore = s.EditResult?.ChangeScore,
                    SourceImageId = s.RequireEdit().ImageId,
                    MaskImageId = s.RequireEdit().MaskImageId,
                    LastFrameImageId = s.RequireEdit().LastFrameImageId,
                    ReferenceIds = [.. s.RequireEdit().ReferenceIds ?? []],
                },
            });
        }

        return rec;
    }

    /// <summary>The slot's exposed-parameter values as stored JSON, or null when it set none. An arbitrary bag keyed
    /// by parameter name — not a relation to anything — so it stays JSON, and plain: none of it is protected.</summary>
    private static string? OverridesJsonOf(RenderSlot s)
    {
        Dictionary<string, JsonElement>? overrides = s.IsEdit ? s.RequireEdit().Overrides : s.RequireGen().Overrides;
        return overrides is null ? null : JsonSerializer.Serialize(overrides);
    }

    /// <summary>The slot's user LoRA stack as stored JSON, or null when it used none (generates only — an edit has no
    /// LoRA stack). A plain per-slot value bag, like <see cref="OverridesJsonOf"/>, so a resumed batch keeps its LoRAs.</summary>
    private static string? LorasJsonOf(RenderSlot s)
    {
        if (s.IsEdit)
        {
            return null;
        }

        IReadOnlyList<LoraSelection>? loras = s.RequireGen().Loras;
        return loras is not { Count: > 0 } ? null : JsonSerializer.Serialize(loras);
    }

    /// <summary>Rebuild a slot's generate spec from its typed columns. No deserialization contract to get wrong: a
    /// column that went missing is a database error, not a silently-null property.</summary>
    private static GenerateSpec GenerateSpecOf(JobSlotRecord sr)
    {
        GenerateSlotData g = sr.Generate ?? throw new InvalidOperationException("Generate slot record has no generate data.");
        return new(
            sr.Workflow ?? "",
            sr.Prompt ?? "",
            sr.NegativePrompt,
            g.Aspect,
            g.RandomArtist,
            g.RandomPrompt,
            g.Temperature,
            Deser<Dictionary<string, JsonElement>>(sr.OverridesJson),
            Deser<List<string>>(g.TagTypesJson),
            Loras: Deser<List<LoraSelection>>(sr.LorasJson),
            ResolvePromptSyntax: false);
    }

    /// <summary>Rebuild a slot's edit spec from its typed columns and its reference child rows.</summary>
    private static EditSpec EditSpecOf(JobSlotRecord sr)
    {
        EditSlotData e = sr.Edit ?? throw new InvalidOperationException("Edit slot record has no edit data.");
        return new(
            sr.Workflow ?? "",
            sr.Prompt ?? "",
            e.SourceImageId ?? "",
            sr.NegativePrompt,
            [.. e.ReferenceIds],
            Deser<Dictionary<string, JsonElement>>(sr.OverridesJson),
            e.MaskImageId,
            e.LastFrameImageId,
            ResolvePromptSyntax: false);
    }

    /// <summary>Reload this instance's still-active jobs and resume them: a mid-render slot keeps its
    /// prompt id and is re-queued to RESUME polling; an unsubmitted slot renders fresh; a slot whose request payload
    /// was lost is failed so the job can still finalize. Returns false on any failure — the caller retries until a
    /// pass succeeds, and jobs already in memory are skipped so retries cannot duplicate.</summary>
    internal async Task<bool> RehydrateAsync(CancellationToken ct)
    {
        IReadOnlyList<JobRecord> active;
        try
        {
            active = await _jobRepo.ListActiveForMachineAsync(_machine, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Job rehydrate failed; will retry.");
            return false;
        }

        if (active.Count == 0)
        {
            return true;
        }

        int resumed = 0;
        try
        {
            foreach (JobRecord rec in active)
            {
                resumed += await RehydrateActiveRecordAsync(rec, ct);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Job rehydrate interrupted mid-pass; the remainder will retry.");
            if (resumed > 0)
            {
                _ = _signal.Release(resumed);
            }

            return false;
        }

        if (resumed > 0)
        {
            _ = _signal.Release(resumed);
        }

        _log.LogInformation("Rehydrated {Jobs} active job(s), {Slots} slot(s) resumed.", active.Count, resumed);
        return true;
    }

    /// <summary>Re-read and publish one row atomically with respect to stranded cancellation. The list handed to this
    /// method is only a candidate set; its Active state may already be stale by the time this job is reached.</summary>
    private async Task<int> RehydrateActiveRecordAsync(JobRecord listed, CancellationToken ct)
    {
        await _rehydrateMutation.WaitAsync(ct);
        try
        {
            lock (_lock)
            {
                if (_jobs.ContainsKey(listed.JobId))
                {
                    return 0;   // already live — a retry after a partial pass
                }
            }

            JobRecord? current = await _jobRepo.GetAsync(listed.JobId, ct);
            if (current is null || current.Status != JobStatus.Active
                || !string.Equals(current.MachineName, _machine, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            // One unresumable job must not sink the pass. Rehydration is ordered oldest-first, so a malformed row at
            // the head is failed and removed from the live maps, allowing later jobs to resume.
            try
            {
                return await RehydrateJobAsync(current);
            }
            catch (Exception ex) when (_db.IsUnavailable(ex))
            {
                _log.LogWarning(ex, "Rehydrate reached job {JobId} with the database unreachable; the pass will retry.", current.JobId);
                throw;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Job {JobId} could not be resumed; marking it failed.", current.JobId);
                await _jobRepo.FailAsync(current.JobId, "could not be resumed after restart", ct);
                RemoveRehydratedJob(current);
                return 0;
            }
        }
        finally
        {
            _ = _rehydrateMutation.Release();
        }
    }

    private void RemoveRehydratedJob(JobRecord record)
    {
        // RehydrateJobAsync may have thrown after publication. Drop the job and its queued slots from both tiers so
        // work whose durable row was failed cannot still render.
        lock (_lock)
        {
            _ = _jobs.Remove(record.JobId);
            if (_byOwner.TryGetValue(record.UserId, out Queue<RenderSlot>? fgQ))
            {
                _byOwner[record.UserId] = new Queue<RenderSlot>(
                    fgQ.Where(s => !string.Equals(s.Job.JobId, record.JobId, StringComparison.Ordinal)));
            }

            if (_bgByOwner.TryGetValue(record.UserId, out Queue<RenderSlot>? bgQ))
            {
                _bgByOwner[record.UserId] = new Queue<RenderSlot>(
                    bgQ.Where(s => !string.Equals(s.Job.JobId, record.JobId, StringComparison.Ordinal)));
            }
        }
    }

    /// <summary>Rebuild one persisted job into the in-memory queue and return how many of its slots were re-queued.
    /// Throws if the record cannot be turned into a runnable job — the caller fails that job rather than retrying it
    /// forever.</summary>
    private async Task<int> RehydrateJobAsync(JobRecord rec)
    {
        int resumed = 0;
        RenderJob job = new()
        {
            JobId = rec.JobId,
            Owner = rec.UserId,
            MachineName = rec.MachineName,
            CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(rec.CreatedAtUtc, DateTimeKind.Utc)),
        };
        foreach (JobSlotRecord? sr in rec.Slots.OrderBy(s => s.SlotIndex))
        {
            // The spec comes back from TYPED COLUMNS, so there is no request payload left to misparse and no
            // "deserialized into an object with a hole in it" to guard against — a missing column is a database
            // error, loudly, and the workflow check below is the only remaining question.
            //
            // The two value bags (overrides, tag types) are still JSON, and a truncated or half-written one is a
            // corrupt row that must fail exactly this slot rather than arriving downstream as a silent null.
            GenerateSpec? gen = null;
            EditSpec? edit = null;
            string? parseError = null;
            try
            {
                if (sr.IsEdit)
                {
                    edit = EditSpecOf(sr);
                }
                else
                {
                    gen = GenerateSpecOf(sr);
                }
            }
            catch (JsonException ex)
            {
                // NOT `_log.LogError(ex, …)`. A JsonException quotes the offending document — path, line, byte
                // position and the text around it. These two bags are not user text, but the exception's shape is the
                // same and this is not the place to bet on it. Line and position are all a reader needs.
                _log.LogError("Job {JobId} slot {Index}: a stored value bag is not readable JSON (line {Line}, position {Position}).",
                    rec.JobId, sr.SlotIndex, ex.LineNumber, ex.BytePositionInLine);
                parseError = "unreadable stored request: " + ex.Message;
            }

            Dictionary<string, string>? marks = sr.Marks.Count == 0
                ? null
                : sr.Marks.ToDictionary(m => m.Token, m => m.Kind.ToWire(), StringComparer.Ordinal);
            // Rebuild the sampler-provenance subset from the persisted marks so a resumed slot still dashes its
            // auto-generated chips in history.
            HashSet<string>? generatedTokens = sr.Marks.Any(m => m.Generated)
                ? new HashSet<string>(sr.Marks.Where(m => m.Generated).Select(m => m.Token), StringComparer.Ordinal)
                : null;

            // A background slot that was mid-render at a crash comes back FRESH, not resuming: the renderer very likely
            // restarted too, so its old prompt id is gone, and a background render is cheap to redo on the next idle
            // window. Dropping the prompt id here makes it re-render rather than resume-poll a prompt the backend lost
            // (which would fail the slot). Foreground slots still resume — after a graceful restart the backend may
            // still hold their prompt, and their work is what a user is actively waiting on.
            string? comfyPromptId = sr.IsBackground ? null : sr.ComfyPromptId;

            RenderSlot slot = new()
            {
                Job = job,
                Index = sr.SlotIndex,
                Gen = gen,
                Edit = edit,
                RehydrateFallback = parseError is null ? null : sr,
                IsBackground = sr.IsBackground,
                ComfyPromptId = comfyPromptId,
                ImageId = sr.ImageId,
                Width = sr.Width ?? 0,
                Height = sr.Height ?? 0,
                EditResult = sr.Edit is { } e ? new EditResult { Changed = e.Changed, ChangeScore = e.ChangeScore } : null,
                Error = parseError ?? sr.Error,
                EffectivePrompt = sr.EffectivePrompt,
                ModelPrompt = sr.ModelPrompt,
                ModelManifest = Deser<RenderModelManifest>(sr.ModelManifestJson),
                RenderDimensions = Deser<RenderDimensions>(sr.RenderDimensionsJson),
                RawPrompt = sr.RawPrompt,
                RawNegativePrompt = sr.RawNegativePrompt,
                Marks = marks,
                GeneratedTokens = generatedTokens,
                // Only a terminal row may retain its historical timestamp for serialization. Every non-terminal slot
                // resumes under this new process with an unknown local start, so it must not contribute a timing sample
                // or a restart-gap ETA countdown.
                GenStartedAt = sr.State is JobSlotState.Done or JobSlotState.Error or JobSlotState.Cancelled
                    && sr.GenStartedAtUtc is { } g
                        ? new DateTimeOffset(DateTime.SpecifyKind(g, DateTimeKind.Utc))
                        : null,
                ExpectedGenSeconds = sr.ExpectedGenSeconds,
                State = parseError is not null ? SlotState.Error : RenderPhases.Live(sr.State),
            };
            job.Slots.Add(slot);
        }

        lock (_lock)
        {
            _jobs[job.JobId] = job;
            foreach (RenderSlot s in job.Slots)
            {
                if (s.Terminal)
                {
                    continue;
                }

                if (s.Gen is null && s.Edit is null)
                {
                    s.State = SlotState.Error;
                    s.Error = "lost on restart";
                    continue;
                }

                // A slot with no workflow can never render, and left Queued it keeps its job Active forever, so it is
                // failed here with a reason. The workflow is its own column, so this only fires for a row migrated
                // without one.
                string? workflow = s.IsEdit ? s.Edit?.Workflow : s.Gen?.Workflow;
                if (string.IsNullOrWhiteSpace(workflow))
                {
                    s.State = SlotState.Error;
                    s.Error = "unrunnable: this slot recorded no workflow";
                    continue;
                }
                // Resume onto the slot's OWN tier: a background slot rehydrates to the background queue and re-gates on
                // the idle delay (the boot clock starts fresh), rather than jumping the foreground line.
                Dictionary<long, Queue<RenderSlot>> tier = s.IsBackground ? _bgByOwner : _byOwner;
                if (!tier.TryGetValue(job.Owner, out Queue<RenderSlot>? q))
                {
                    q = new Queue<RenderSlot>();
                    tier[job.Owner] = q;
                }

                q.Enqueue(s);
                resumed++;
            }
        }

        await AfterSlotAsync(job);   // finalize immediately if everything was already terminal
        return resumed;
    }

    /// <summary>Parse a persisted payload. Null/empty is a legitimately-absent value and answers null; anything else
    /// must BE valid JSON, so a parse failure THROWS for the caller to attribute to a specific slot. It deliberately
    /// does not resolve to null on failure: null is already the "nothing was stored" answer, and folding the two
    /// together erases which of them happened at the only point that still knows.</summary>
    private static T? Deser<T>(string? json)
        => string.IsNullOrEmpty(json) ? default : JsonSerializer.Deserialize<T>(json);

    #endregion

    #region shared helpers

    /// <summary>The tags and artists <paramref name="owner"/> has banned for workflow <paramref name="model"/>, read from
    /// the store (the ban repository is request-scoped, so it is resolved per-read like the history repository). A store
    /// failure THROWS: rendering with an unknown ban set would hand the user back the very tag they banned, so the slot
    /// must fail loudly instead.</summary>
    private async Task<(HashSet<string> Tags, HashSet<string> Artists)> BannedKeysAsync(long owner, string model, CancellationToken ct)
    {
        // Waits out an unreachable database. Rendering WITHOUT the ban list is not a degraded mode worth having —
        // it would put back exactly the tags the user asked never to see — and failing the slot for it throws away
        // queued work over a condition that resolves itself. A fresh scope per attempt (the repository is scoped).
        IReadOnlyList<BannedToken> bans = await AwaitingDatabaseAsync(async c =>
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IBannedTokenRepository>().GetForModelAsync(owner, model, c);
        }, "reading the ban list", ct);
        return BanKeys(bans);
    }

    /// <summary>
    /// The tag types this render may emit: <paramref name="requested"/> when the slot carries a mask of its own,
    /// otherwise the one <paramref name="owner"/> has stored (the user repository is request-scoped, so it is resolved
    /// per-read like the bans). An unset column resolves to the default.
    ///
    /// Both paths THROW on an invalid value rather than falling back to the default, because generating under a mask we
    /// could not confirm is how a type the user switched off ends up in their prompt. A slot's mask was already vetted
    /// at the API boundary, so a bad one here means a hand-edited or corrupted stored value.
    /// </summary>
    private async Task<IReadOnlyList<string>> AllowedTagTypesAsync(
        long owner, IReadOnlyList<string>? requested, CancellationToken ct)
    {
        if (requested is not null)
        {
            // Normalized rather than passed through: this canonicalises the order and rejects an unknown name, and the
            // wire list is what the tag model is told stays ALLOWED.
            if (!GenerationTagTypes.TryNormalize(requested, out IReadOnlyList<string>? types, out string? error))
            {
                throw new InvalidOperationException($"Slot carries an invalid generation mask: {error}");
            }

            return types;
        }

        // Waits out an unreachable database, like the ban list: falling back to the default mask would silently
        // generate tag kinds the user switched off, and failing the slot would throw away queued work over an
        // outage. A fresh scope per attempt (the repository is scoped).
        User? user = await AwaitingDatabaseAsync(async c =>
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(owner, c);
        }, "reading the generation mask", ct);
        return GenerationTagTypes.Resolve(user?.GenerationTagTypes);
    }

    /// <summary>
    /// What the tag predictor is conditioned on for <paramref name="raw"/>, plus the inert keys the caller must also
    /// ban. The seed is the prompt as the IMAGE MODEL will see it (finalized), minus two kinds of segment:
    ///
    ///   '@artist' — a style, not a subject; conditioning on it makes the predictor sample that artist's pet subjects.
    ///   '!tag'    — an INERT TAG: the same policy made per-tag. A dominant, common subject drags the whole sampled set
    ///               into its own corpus neighbourhood ("#pig" buries the fantasy tags under barnyard ones), so '!pig'
    ///               keeps the pig in the picture and lets the rest of the prompt steer the sample.
    ///
    /// Inert keys come off the RAW prompt: finalization has already eaten the '!' that distinguishes them. They are
    /// returned rather than just dropped because hiding a tag from the seed is only half the job — see the ban union at
    /// the call site.
    /// </summary>
    internal static (string Seed, HashSet<string> SuppressKeys) TagSeed(string? raw, WorkflowTagging tagging) =>
        PromptParse.TagSeed(raw, tagging);

    /// <summary>
    /// The tags the random-prompt call must ban: the user's tag bans, their negated tags, the inert ('!') and guide
    /// ('~') suppress keys, AND the artist bans + negated artists (the tag model can emit artist-type names, so the
    /// one sampler that produces an artist must honour the artist bans too). This is exactly the set handed to
    /// <see cref="ITagModelClient.GenerateAsync"/>, extracted so the inert/guide guarantee (#166) is testable: nothing
    /// between the seed build and the model may re-introduce a suppressed key.
    /// </summary>
    internal static HashSet<string> RandomPromptBannedTags(
        (HashSet<string> Tags, HashSet<string> Artists) bans,
        (HashSet<string> Tags, HashSet<string> Artists) negKeys,
        IReadOnlySet<string> suppressKeys)
    {
        HashSet<string> banned = new(bans.Tags, StringComparer.Ordinal);
        banned.UnionWith(negKeys.Tags);
        banned.UnionWith(suppressKeys);
        banned.UnionWith(bans.Artists);
        banned.UnionWith(negKeys.Artists);
        return banned;
    }

    /// <summary>
    /// Resolve the prompt DSL for every item into concrete render slots: a Comfy-compatible <c>{a|b}</c> choice picks
    /// one option, while <c>{{a|b}}</c> fans one item into one slot per combo. Choices inside separate fan-out combos are
    /// picked independently. The resolved prompt/instruction is what each slot renders and records. Group-free items
    /// pass through by identity, so this remains a no-op for the common case and an already-resolved reload/requeue.
    /// </summary>
    internal static IReadOnlyList<RenderItem> ExpandPromptGroups(IReadOnlyList<RenderItem> items)
    {
        List<RenderItem> expanded = [];
        foreach (RenderItem item in items)
        {
            if (item.Gen is { ResolvePromptSyntax: true } gen && gen.Prompt.IndexOfAny(GroupChars) >= 0)
            {
                foreach (GeneratedTagGroup g in TagGroup.Parse(gen.Prompt).Generate())
                {
                    expanded.Add(RenderItem.ForGenerate(gen with { Prompt = g.RawResolved }, item.Background));
                }
            }
            else if (item.Edit is { ResolvePromptSyntax: true } edit && edit.Instruction.IndexOfAny(GroupChars) >= 0)
            {
                foreach (GeneratedTagGroup g in TagGroup.Parse(edit.Instruction).Generate())
                {
                    expanded.Add(RenderItem.ForEdit(edit with { Instruction = g.RawResolved }, item.Background));
                }
            }
            else
            {
                expanded.Add(item);
            }
        }

        return expanded;
    }

    /// <summary>The characters that open a prompt-DSL group — a cheap gate so a group-free prompt is never re-parsed.</summary>
    private static readonly char[] GroupChars = ['{', '['];

    /// <summary>Split banned tokens into the canonical tag/artist key sets the random samplers honour (the tag model
    /// zeroes these during sampling; RandomArtist rejects them). A key is canonicalized exactly like a prompt token —
    /// marker stripped, lowercased, spaces to underscores — so a ban typed free-hand into Settings as "Wet Shirt" still
    /// matches the "wet_shirt" the model would sample.</summary>
    internal static (HashSet<string> Tags, HashSet<string> Artists) BanKeys(IEnumerable<BannedToken> bans)
    {
        HashSet<string> tags = new(StringComparer.Ordinal);
        HashSet<string> artists = new(StringComparer.Ordinal);
        foreach (BannedToken b in bans)
        {
            string key = PromptFinalizer.Normalize(b.Name).TrimStart('#', '@');
            if (key.Length == 0)
            {
                continue;
            }

            _ = (b.Kind == TokenKind.Artist ? artists : tags).Add(key);
        }

        return (tags, artists);
    }

    /// <summary>Source bytes for an edit/reference id: an in-memory upload first (the user's own source/reference/mask,
    /// which is never persisted), then the DB blob, else the legacy backend view fetch. Throws
    /// <see cref="RenderInputNotFoundException"/> only for a definitive legacy 404; renderer outages wait and retry.
    /// <para>An upload is process-local, so a slot that was queued but never submitted before a restart lands here with
    /// nothing to find; that surfaces as a slot error naming the missing source rather than a silent failure.</para></summary>
    private async Task<byte[]> GetImageBytesAsync(string id, CancellationToken ct) => (await GetImageMediaAsync(id, ct)).Bytes;

    /// <summary>Source bytes AND the content type for an edit/reference id, by the same upload-first/DB/legacy path as
    /// <see cref="GetImageBytesAsync"/>. The content type is a reference's authoritative media kind (see
    /// <see cref="ReferenceKinds.Classify"/>); a legacy backend view-ref predates content-type tracking and is always
    /// an image.</summary>
    private async Task<(byte[] Bytes, string ContentType)> GetImageMediaAsync(string id, CancellationToken ct)
    {
        if (_uploads.Get(id) is { } upload)
        {
            return (upload.Bytes, upload.ContentType);
        }
        // Waits out an unreachable database rather than reporting the source as missing. "Not found" and "could not
        // be looked up" are opposite facts, and only one of them is the user's to act on: failing the slot here would
        // tell them their source image doesn't exist because the server is out of range.
        ImageBlob? blob = await AwaitingDatabaseAsync(c => _blobs.GetAsync(id, c), $"loading input image {id}", ct);
        if (blob is not null)
        {
            return (blob.Bytes, blob.ContentType);
        }

        TimeSpan retryDelay = TimeSpan.FromSeconds(5);
        while (true)
        {
            LegacyImageFetchResult legacy = await _comfy.FetchLegacyImageAsync(id, ct);
            if (legacy.State == LegacyImageFetchState.Found)
            {
                return (legacy.Bytes ?? throw new InvalidOperationException(
                    "The renderer reported a found legacy image without bytes."), ReferenceKindNames.ImageMime + "png");
            }

            if (legacy.State == LegacyImageFetchState.NotFound)
            {
                throw new RenderInputNotFoundException(id);
            }

            // Accepted work is durable and must survive a renderer restart. The client logged why this lookup could
            // not answer; retry with capped backoff until it can distinguish found from definitively missing.
            await Task.Delay(retryDelay, ct);
            retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 60));
        }
    }

    #endregion
}
