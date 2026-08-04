using System.Text.Json;
using ImageGen.Application.Images;
using ImageGen.Application.Media;
using ImageGen.Application.Platform;
using ImageGen.Application.Prompting;
using ImageGen.Application.Tags;
using ImageGen.Application.Workflows;
using ImageGen.Domain;
using ImageGen.Domain.Entities;
using ImageGen.Domain.Logging;
using ImageGen.Domain.Repositories;
using Microsoft.Extensions.DependencyInjection;

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
public sealed class RenderOrchestrator
{
    /// <summary>How many consecutive polls the backend must fail to list a submitted prompt (while no result has
    /// landed) before it is declared LOST. Debounces the history-flush race; NOT a render deadline.</summary>
    private const int LivenessVanishThreshold = 3;

    private readonly object _lock = new();
    private readonly Dictionary<string, RenderJob> _jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<long, Queue<RenderSlot>> _byOwner = new();
    private readonly Dictionary<long, long> _lastServed = new();
    private long _servedSeq;
    private readonly Dictionary<string, RenderSlot> _comfyToSlot = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _signal = new(0);
    private readonly string _machine = Environment.MachineName;
    private RenderSlot? _running;

    private readonly IComfyClient _comfy;
    private readonly IWorkflowCatalog _catalog;
    private readonly ITagModelClient _tagModel;
    private readonly ITagCatalog _tags;
    private readonly IMediaProcessor _media;
    private readonly IJobRepository _jobRepo;
    private readonly IUploadStore _uploads;
    private readonly IImageBlobRepository _blobs;
    private readonly IImageFrameRepository _frames;
    private readonly IGenTimingRepository _timings;
    private readonly IUserLogService _userLog;
    private readonly IDatabaseAvailability _db;
    private readonly RenderOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RenderOrchestrator> _log;

    /// <summary>Construct the orchestrator with its collaborators. All are singletons except the history repository,
    /// which is resolved per-write from <paramref name="scopeFactory"/>.</summary>
    public RenderOrchestrator(
        IComfyClient comfy, IWorkflowCatalog catalog, ITagModelClient tagModel, ITagCatalog tags,
        IMediaProcessor media, IJobRepository jobRepo, IUploadStore uploads, IImageBlobRepository blobs,
        IImageFrameRepository frames,
        IGenTimingRepository timings, IUserLogService userLog, IDatabaseAvailability databaseAvailability,
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
        _blobs = blobs;
        _frames = frames;
        _timings = timings;
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
        var job = new RenderJob
        {
            JobId = Guid.NewGuid().ToString("N"),
            Owner = owner,
            MachineName = _machine,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        for (var i = 0; i < items.Count; i++)
        {
            var gen = items[i].Gen;
            var edit = items[i].Edit;
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
                var norm = _comfy.NormalizeForQueue(edit.Workflow, RenderKind.Edit, edit.Overrides);
                if (norm.Notice is not null) { edit = edit with { Overrides = AsDict(norm.Overrides) }; notice = norm.Notice; }
            }
            else if (gen is not null)
            {
                var norm = _comfy.NormalizeForQueue(gen.Workflow, RenderKind.Generate, gen.Overrides);
                if (norm.Notice is not null) { gen = gen with { Overrides = AsDict(norm.Overrides) }; notice = norm.Notice; }
            }
            // Seed is a generation parameter decided at this boundary: fill a fresh one unless the caller pinned it, so
            // the exact seed is persisted with the request and is what the workflow builds with.
            if (gen is not null) gen = gen with { Overrides = WithSeed(gen.Overrides) };
            if (edit is not null) edit = edit with { Overrides = WithSeed(edit.Overrides) };
            job.Slots.Add(new RenderSlot { Job = job, Index = i, Gen = gen, Edit = edit, Notice = notice });
        }

        lock (_lock) _jobs[job.JobId] = job;   // visible to Get()/owner lookups now; NOT yet schedulable

        // The comment above is a rule, so the result is checked. PersistAsync waits out an unreachable database, so a
        // false here means the write was REJECTED — and a job that exists only in memory must not become schedulable:
        // it would render, and the record of it would die with the process. Refusing a new submission is acceptable;
        // accepting one we cannot write down is not. The job is dropped again so nothing is left half-accepted.
        if (!await PersistAsync(job))
        {
            lock (_lock) _jobs.Remove(job.JobId);
            throw new RenderStorageException("This generation could not be recorded, so it was not started.");
        }

        lock (_lock)
        {
            if (!_byOwner.TryGetValue(owner, out var q)) { q = new Queue<RenderSlot>(); _byOwner[owner] = q; }
            foreach (var s in job.Slots) q.Enqueue(s);
        }
        if (job.Slots.Count > 0) _signal.Release(job.Slots.Count);
        return job;
    }

    /// <summary>The overrides with a fresh RNG <c>seed</c> filled in unless the caller pinned one — so the generation
    /// seed is decided here, persisted with the request, and single-sourced for the build.</summary>
    private static Dictionary<string, JsonElement> WithSeed(Dictionary<string, JsonElement>? overrides)
    {
        var d = overrides is null ? new Dictionary<string, JsonElement>() : new Dictionary<string, JsonElement>(overrides);
        if (!d.ContainsKey("seed"))
            d["seed"] = JsonSerializer.SerializeToElement(Random.Shared.NextInt64(1, long.MaxValue));
        return d;
    }

    private static Dictionary<string, JsonElement>? AsDict(IReadOnlyDictionary<string, JsonElement>? d) =>
        d is null ? null : new Dictionary<string, JsonElement>(d);

    #endregion

    #region reads

    /// <summary>The user's still-active jobs (not yet finalized), oldest first.</summary>
    public List<RenderJob> ActiveForOwner(long owner)
    {
        lock (_lock)
            return _jobs.Values.Where(j => j.Owner == owner && !j.AllTerminal).OrderBy(j => j.CreatedAt).ToList();
    }

    /// <summary>Every still-active job on this instance, all owners, oldest first — the cross-user queue view.</summary>
    public List<RenderJob> AllActive()
    {
        lock (_lock)
            return _jobs.Values.Where(j => !j.AllTerminal).OrderBy(j => j.CreatedAt).ToList();
    }

    /// <summary>A live (in-memory) job by id, or null (a finalized job is read from <see cref="IJobRepository"/>).</summary>
    public RenderJob? Get(string jobId) { lock (_lock) return _jobs.GetValueOrDefault(jobId); }

    /// <summary>This instance's render work right now, counted under one lock so the numbers agree with each other.
    /// See <see cref="WorkloadSnapshot"/> for why in-flight and executing are separate counts.</summary>
    public WorkloadSnapshot Workload()
    {
        lock (_lock)
        {
            var active = _jobs.Values.Where(j => !j.AllTerminal).ToList();
            var waiting = active.SelectMany(j => j.Slots).Count(s => !s.Terminal && !ReferenceEquals(s, _running));
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
            var active = _jobs.Values.Where(j => !j.AllTerminal).ToList();
            var pending = active.SelectMany(j => j.Slots).Where(s => !s.Terminal).ToList();
            var running = _running;

            // The in-flight slot is priced from its own measurement only once it HAS one — the expected time and start
            // instant are assigned at submit, so a slot the worker has picked but not yet submitted has neither. That
            // one is priced from the workflow average like anything else waiting, rather than silently counting zero.
            double? runningRemaining = null;
            if (running is { ExpectedGenSeconds: { } expected, GenStartedAt: { } started })
                runningRemaining = Math.Max(0, expected - (DateTimeOffset.UtcNow - started).TotalSeconds);

            var waiting = pending
                .Where(s => runningRemaining is null || !ReferenceEquals(s, running))
                .Select(s => s.Model)
                .ToList();
            return new OutstandingSnapshot(
                active.Count, active.Count(j => j.Owner == viewer), pending.Count, runningRemaining, waiting);
        }
    }

    /// <summary>Reverse-map a backend prompt id to our jobId (or null) — lets the /ws proxy translate upstream frames.</summary>
    public string? JobIdForComfy(string? comfyPromptId)
    {
        if (string.IsNullOrEmpty(comfyPromptId)) return null;
        lock (_lock) return _comfyToSlot.TryGetValue(comfyPromptId, out var s) ? s.Job.JobId : null;
    }

    /// <summary>The user who owns the job behind a backend prompt id (or null) — lets /ws forward only own progress.</summary>
    public long? OwnerForComfy(string? comfyPromptId)
    {
        if (string.IsNullOrEmpty(comfyPromptId)) return null;
        lock (_lock) return _comfyToSlot.TryGetValue(comfyPromptId, out var s) ? s.Job.Owner : (long?)null;
    }

    /// <summary>Approximate count of image slots that will run before this job's first queued slot.</summary>
    public int JobsAhead(RenderJob job)
    {
        lock (_lock)
        {
            // The job the worker is on is the head of the line — nothing is ahead of it — whether its slot is being
            // prepared, waiting in the backend's queue, or executing.
            if (_running is not null && ReferenceEquals(_running.Job, job)) return 0;
            if (!job.Slots.Exists(s => s.State == SlotState.Queued)) return 0;
            var queuedAhead = _byOwner.Values.Sum(q => q.Count(s => s.Job.CreatedAt < job.CreatedAt));
            return queuedAhead + (_running is not null ? 1 : 0);
        }
    }

    #endregion

    #region cancel

    /// <summary>Cancel a whole job: drop the slots still waiting, and ask the worker to abandon the one it holds.
    /// Returns false if unknown.</summary>
    public bool Cancel(string jobId)
    {
        RenderJob? job;
        var interrupt = false;
        lock (_lock)
        {
            if (!_jobs.TryGetValue(jobId, out job)) return false;
            foreach (var s in job.Slots)
            {
                if (s.Terminal) continue;
                // Whose slot it is, not what state it is in. The worker's slot may be mid-prompt-build or mid-submit
                // and is NOT necessarily Running; marking it terminal from here would be overwritten by the result the
                // worker then lands. So it is asked to stop (it checks before submitting and on every poll) while the
                // rest are dropped outright. Only interrupt the backend if the prompt is actually out there — firing
                // an interrupt with nothing of ours in flight would kill whatever else is on that GPU.
                if (ReferenceEquals(s, _running)) { s.CancelRequested = true; interrupt = s.Submitted; }
                else { s.State = SlotState.Cancelled; s.Error = "cancelled"; }
            }
            if (_byOwner.TryGetValue(job.Owner, out var q))
                _byOwner[job.Owner] = new Queue<RenderSlot>(q.Where(s => s.State == SlotState.Queued));
        }
        if (interrupt)
        {
            // The cancel itself has already succeeded — these slots are terminal in our state regardless of what the
            // backend does next, so a failed interrupt does not undo it and must not fail the caller. What it must not
            // do is vanish: an empty catch here would let a backend that no longer honours interrupts leave the GPU
            // rendering cancelled work with nothing anywhere to say so.
            try { _comfy.InterruptAsync(CancellationToken.None).GetAwaiter().GetResult(); }
            catch (Exception ex) { _log.LogError(ex, "Job {JobId} was cancelled but the backend interrupt failed; its render may still be running.", jobId); }
        }
        _ = AfterSlotAsync(job);   // persist + finalize if everything is now terminal
        return true;
    }

    /// <summary>
    /// Cancel a job this instance OWNS whose row is still Active but which no worker holds — one stranded by a crash,
    /// or by a rehydrate pass that never reached it. Nothing is rendering it (that is what stranded means), so the row
    /// is simply failed. Returns false when the job is live here (<see cref="Cancel"/> handles those), belongs to
    /// another instance (invariant #4 — only its owner may advance it), or has already resolved.
    /// </summary>
    public async Task<bool> CancelStrandedAsync(string jobId, CancellationToken ct)
    {
        lock (_lock) { if (_jobs.ContainsKey(jobId)) return false; }   // live here — not stranded
        var rec = await _jobRepo.GetAsync(jobId, ct);
        if (rec is null || rec.Status != JobStatus.Active) return false;
        if (!string.Equals(rec.MachineName, _machine, StringComparison.OrdinalIgnoreCase)) return false;
        await _jobRepo.CancelAsync(jobId, ct);
        return true;
    }

    /// <summary>
    /// Cancel every unfinished job on this instance, or just one owner's. Returns how many jobs were cancelled.
    /// <para>Server-side deliberately: the queue page shows 25 rows of a list it re-polls every 2s, so a client-side
    /// loop over the rendered rows would clear only the visible page and race the poll rebuilding it.</para>
    /// <para>The render on the GPU is stopped without any separate interrupt call, and — this is the point — without
    /// the risk of one. <see cref="Cancel"/> interrupts only when the slot the worker holds belongs to the job being
    /// cancelled, so cancelling a set stops the in-flight image exactly when that image is part of the set. Firing an
    /// unconditional interrupt for "cancel mine" would kill whatever else was on that GPU, which for a cross-user box
    /// means killing another user's image while claiming to have cancelled only your own.</para>
    /// <para>Stranded rows are included: still Active in the database but held by no worker (a crash, or a rehydrate
    /// that never reached them). They are precisely what someone reaching for "cancel everything" wants gone, and
    /// nothing else will ever clear them.</para>
    /// </summary>
    public async Task<int> CancelAllAsync(long? owner, CancellationToken ct)
    {
        var live = owner is { } o ? ActiveForOwner(o) : AllActive();
        var cancelled = live.Count(j => Cancel(j.JobId));

        var rows = await _jobRepo.ListActiveForMachineAsync(_machine, ct);
        foreach (var rec in rows)
        {
            if (owner is { } only && rec.UserId != only) continue;
            if (await CancelStrandedAsync(rec.JobId, ct)) cancelled++;   // re-checks live/owner/status itself
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
            if (_jobs.ContainsKey(jobId)) return new RequeueOutcome(RequeueStatus.StillActive);

        var rec = await _jobRepo.GetAsync(jobId, ct);
        if (rec is null) return new RequeueOutcome(RequeueStatus.UnknownJob);
        // Owner-checked, unlike /cancel/{id}. Cancel destroys work; requeue CREATES it, under an owner, and the
        // scheduler is fair round-robin per owner — so an unchecked requeue would let one user push work into
        // another's queue share.
        if (rec.UserId != owner) return new RequeueOutcome(RequeueStatus.NotOwner);

        var missing = rec.Slots
            .Where(s => s.ImageId is null && s.State is JobSlotState.Error or JobSlotState.Cancelled)
            .OrderBy(s => s.SlotIndex)
            .ToList();
        if (missing.Count == 0) return new RequeueOutcome(RequeueStatus.NothingMissing);

        var items = new List<RenderItem>(missing.Count);
        var edits = new List<EditSpec>();
        foreach (var sr in missing)
        {
            var which = $"image {sr.SlotIndex + 1}";
            // One check, on a real column: a column either has a workflow in it or it does not. Deserializing a blob
            // instead would force a second question — is the object usable? — because System.Text.Json ignores members
            // it doesn't recognise, so a request written under an older property name yields a spec with a null
            // workflow and no error.
            if (string.IsNullOrWhiteSpace(sr.Workflow))
                return new RequeueOutcome(RequeueStatus.Unrunnable, Reason: $"{which} didn't record the workflow that would remake it");
            try
            {
                if (sr.IsEdit)
                {
                    var edit = EditSpecOf(sr);
                    edits.Add(edit);
                    items.Add(RenderItem.ForEdit(edit));
                }
                else
                {
                    items.Add(RenderItem.ForGenerate(GenerateSpecOf(sr)));
                }
            }
            catch (JsonException ex)
            {
                return new RequeueOutcome(RequeueStatus.Unrunnable, Reason: $"{which}'s stored parameters are unreadable: {ex.Message}");
            }
        }

        if (await FirstMissingEditInputAsync(edits, ct) is { } gone)
            return new RequeueOutcome(RequeueStatus.Unrunnable, Reason: gone);

        var job = await EnqueueJobAsync(owner, items);
        return new RequeueOutcome(RequeueStatus.Requeued, job.JobId, items.Count);
    }

    /// <summary>The first edit input across these specs that can no longer be found, phrased for the user, or null
    /// when every one still resolves. One database round trip for the whole set — existence only, never bytes.</summary>
    private async Task<string?> FirstMissingEditInputAsync(IReadOnlyList<EditSpec> edits, CancellationToken ct)
    {
        if (edits.Count == 0) return null;

        // (id, what it is) in the order they're reported, so the message names the input the user recognises.
        var inputs = new List<(string Id, string What)>();
        foreach (var e in edits)
        {
            inputs.Add((e.ImageId, "source image"));
            if (!string.IsNullOrWhiteSpace(e.MaskImageId)) inputs.Add((e.MaskImageId, "inpaint mask"));
            if (!string.IsNullOrWhiteSpace(e.LastFrameImageId)) inputs.Add((e.LastFrameImageId, "end frame"));
            foreach (var r in e.ReferenceImageIds ?? []) if (!string.IsNullOrWhiteSpace(r)) inputs.Add((r, "reference image"));
        }

        var unresolved = inputs.Where(i => _uploads.Get(i.Id) is null).Select(i => i.Id).Distinct(StringComparer.Ordinal).ToList();
        var stored = unresolved.Count == 0
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : await _blobs.GetContentTypesAsync(unresolved, ct);

        foreach (var (id, what) in inputs)
            if (_uploads.Get(id) is null && !stored.ContainsKey(id))
                return $"its {what} is gone — an uploaded input lives only in the process that received it and is never stored";
        return null;
    }

    /// <summary>Abandon the single image the worker is on (its job's other slots keep their place). Returns false when
    /// the worker has nothing. If the prompt hasn't reached the backend yet there is nothing to interrupt — the worker
    /// is told to stop and drops it before submitting.</summary>
    public bool CancelRunning()
    {
        RenderSlot? s;
        bool interrupt;
        lock (_lock)
        {
            s = _running;
            if (s is not null) s.CancelRequested = true;
            interrupt = s?.Submitted == true;
        }
        if (s is null) return false;
        if (interrupt)
        {
            // As in Cancel: the slot is already flagged to stop on our side, so a failed interrupt does not undo the
            // cancel — but it does mean the GPU is still on it, and that has to be recorded rather than dropped.
            try { _comfy.InterruptAsync(CancellationToken.None).GetAwaiter().GetResult(); }
            catch (Exception ex) { _log.LogError(ex, "Running slot was cancelled but the backend interrupt failed; its render may still be running."); }
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
            var delay = TimeSpan.FromSeconds(5);
            while (!ct.IsCancellationRequested && !await RehydrateAsync(ct))
            {
                _log.LogWarning("Rehydrate will retry in {Delay}s.", delay.TotalSeconds);
                try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { return; }
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 300));
            }
        }, ct);

        while (!ct.IsCancellationRequested)
        {
            var slot = PickNext();
            if (slot is null)
            {
                try { await _signal.WaitAsync(ct); } catch (OperationCanceledException) { break; }
                continue;   // drained on the next PickNext
            }
            await RunSlotAsync(slot, ct);
            lock (_lock) _running = null;
            // A slot that comes back NON-terminal was held, not finished — the database was out of reach. It is
            // accepted work and its job is still Active, so it goes back on the queue rather than being stranded in
            // memory with nothing that will ever pick it up again. The wait lives inside the next attempt's first
            // database call, so this cannot spin.
            if (!slot.Terminal && !ct.IsCancellationRequested) Requeue(slot);
            await AfterSlotAsync(slot.Job);   // persist this slot's result; finalize the job if all slots are terminal
        }
    }

    /// <summary>Put a held slot back on its owner's queue and wake the worker. Its position is the back of that
    /// owner's line, which is the fair place for work that could not be done right now.</summary>
    private void Requeue(RenderSlot slot)
    {
        lock (_lock)
        {
            if (slot.Terminal) return;
            if (!_byOwner.TryGetValue(slot.Job.Owner, out var q)) { q = new Queue<RenderSlot>(); _byOwner[slot.Job.Owner] = q; }
            q.Enqueue(slot);
        }
        _signal.Release();
    }

    /// <summary>Fair round-robin via LEAST-RECENTLY-SERVED owner; ties break to the oldest queued slot's job.</summary>
    private RenderSlot? PickNext()
    {
        lock (_lock)
        {
            long? best = null;
            var bestTick = long.MaxValue;
            var bestHead = DateTimeOffset.MaxValue;
            foreach (var (owner, q) in _byOwner)
            {
                while (q.Count > 0 && q.Peek().State != SlotState.Queued) q.Dequeue();   // drop cancelled/stale heads
                if (q.Count == 0) continue;
                var tick = _lastServed.GetValueOrDefault(owner, 0L);
                var head = q.Peek().Job.CreatedAt;
                if (tick < bestTick || (tick == bestTick && head < bestHead))
                {
                    best = owner; bestTick = tick; bestHead = head;
                }
            }
            if (best is null) return null;
            var slot = _byOwner[best.Value].Dequeue();
            _lastServed[best.Value] = ++_servedSeq;
            // Picked, NOT running: the prompt still has to be built (tag sampling, image fetches) and submitted, and
            // the backend still has to start executing it. The slot stays Queued until the backend says otherwise —
            // see ObserveExecuting. What being picked does mean is that this slot is now the worker's.
            _running = slot;
            return slot;
        }
    }

    private async Task RunSlotAsync(RenderSlot slot, CancellationToken ct)
    {
        try
        {
            // A cancel can land between the pick and the submit — building a prompt does real work (tag sampling,
            // image fetches), and Cancel cannot mark the worker's slot terminal itself. Honour it BEFORE handing
            // anything to the backend, so a cancelled slot never becomes a render nobody is waiting for.
            if (slot.CancelRequested) { CancelSlot(slot); return; }

            string promptId;
            byte[]? src = null;
            var resuming = slot.Submitted;   // a rehydrated slot that was mid-render before a restart

            if (resuming)
            {
                promptId = slot.ComfyPromptId ?? throw new InvalidOperationException("A resuming slot must have a prompt id.");
                lock (_lock) _comfyToSlot[promptId] = slot;
            }
            else if (slot.IsEdit)
            {
                var edit = slot.RequireEdit();
                try { src = await GetImageBytesAsync(edit.ImageId, ct); }
                catch (HttpRequestException) { FailSlot(slot, $"source image '{edit.ImageId}' not found"); return; }
                var references = new List<byte[]>();
                foreach (var refId in edit.ReferenceImageIds ?? new List<string>())
                {
                    try { references.Add(await GetImageBytesAsync(refId, ct)); }
                    catch (HttpRequestException) { FailSlot(slot, $"reference image '{refId}' not found"); return; }
                }
                byte[]? maskBytes = null;
                if (!string.IsNullOrEmpty(edit.MaskImageId))
                {
                    try { maskBytes = await GetImageBytesAsync(edit.MaskImageId, ct); }
                    catch (HttpRequestException) { FailSlot(slot, $"mask image '{edit.MaskImageId}' not found"); return; }
                }
                byte[]? lastFrameBytes = null;
                if (!string.IsNullOrEmpty(edit.LastFrameImageId))
                {
                    try { lastFrameBytes = await GetImageBytesAsync(edit.LastFrameImageId, ct); }
                    catch (HttpRequestException) { FailSlot(slot, $"last-frame image '{edit.LastFrameImageId}' not found"); return; }
                }
                // Finalize the instruction for tag-speaking editors (inpaint), as the generate path does. Non-tag
                // editors have no tagging block, so Finalize passes the instruction through unchanged.
                var editInfo = _catalog.ResolveInfo(edit.Workflow);
                var editFinal = PromptFinalizer.Finalize(edit.Instruction, editInfo?.Tagging);
                // The instruction and its negative arrive in marker form and are stored verbatim, exactly as the generate
                // path stores its raw prompt — so an edited image's prompt comes back to the box the way it was written.
                slot.RawPrompt = edit.Instruction;
                slot.RawNegativePrompt = edit.NegativePrompt;
                slot.EffectivePrompt = editFinal.Rendered; slot.Marks = editFinal.Marks;
                await _userLog.LogAsync(slot.Job.Owner, "submit_edit", editFinal.Rendered, ct);
                // Finalize the negative with the SAME rules as the instruction/positive: the negative box shares the
                // tag/artist autocomplete, so its text arrives carrying '#'/'@' markers (and underscores). Without this
                // those markers leak raw into the negative conditioning and degrade output. Marks aren't kept (negatives
                // aren't bookmarkable). Comfy then appends this onto the model's default negative (ComposeNegative).
                var editNeg = PromptFinalizer.Finalize(edit.NegativePrompt, editInfo?.Tagging).Rendered;
                var editSubmit = await _comfy.SubmitEditAsync(src, editFinal.Rendered, editNeg, edit.Workflow, references, edit.Overrides, maskBytes, lastFrameBytes, ct);
                promptId = editSubmit.PromptId;
                slot.EtaSignature = editSubmit.Eta;
            }
            else
            {
                // Guard the discriminant once so the whole generate branch reads slot.Gen without re-asserting it.
                if (slot.Gen is null) throw new InvalidOperationException("Slot is not a generate slot.");
                var info = _catalog.ResolveInfo(slot.Gen.Workflow);
                // The RAW prompt is the source of truth. It is the marker-form string the user submitted ("#long_hair,
                // @greg_rutkowski"), and the random samplers below APPEND TO IT in that same dialect — so after they run
                // it still reads as something the user could have typed. The rendered prompt and the marks are then
                // derived from it, once, by the finalizer. One direction of transform: nothing downstream ever has to
                // invert a finalized prompt to guess back the markers and underscores it destroyed.
                var raw = slot.Gen.Prompt;
                // What the user put in the NEGATIVE is a standing exclusion for the random samplers: a tag they negated
                // must never be handed back to them as a randomly-chosen positive (same for a negated artist).
                var negKeys = PromptFinalizer.NegativeKeys(slot.Gen.NegativePrompt);
                // The user's standing bans for THIS workflow, read from the store right here at render time. A ban is a
                // server-side fact, so it is never taken from the request: a caller that omits it (an API-key client, a
                // browser holding a stale ban cache, a job resumed from before the ban) must not be able to generate its
                // way around one. Only fetched when a random sampler is actually going to run — bans bind auto-gen only.
                var bans = slot.Gen.RandomPrompt == true || slot.Gen.RandomArtist == true
                    ? await BannedKeysAsync(slot.Job.Owner, slot.Model, ct)
                    : (Tags: new HashSet<string>(StringComparer.Ordinal), Artists: new HashSet<string>(StringComparer.Ordinal));
                // Random-prompt: generate the whole prompt PER SLOT from the tag model, seeded by the user's typed tags,
                // but only when the model speaks tags. This does NOT fail soft: a tag model that is down or erroring
                // throws out of GenerateAsync and fails the slot (see the catch at the bottom of RunSlotAsync). This is
                // deliberate: silently rendering the typed seed instead of the generated prompt would produce an image
                // the user did not ask for and give no hint why.
                if (slot.Gen.RandomPrompt == true && info?.Tagging is { Tags: true })
                {
                    var (seed, suppressKeys) = TagSeed(raw, info.Tagging);
                    var bannedTags = bans.Tags;
                    bannedTags.UnionWith(negKeys.Tags);
                    // Inert ('!') and guide ('~') tags are both banned for this call. A tag hidden from the seed is one
                    // the predictor may freely sample ("!pig" would come back "!pig, ..., #pig"); a guide tag echoed
                    // back would be appended as a '#' and rendered, which is exactly what '~' promises cannot happen.
                    // Banning only masks the per-step output distribution; it does not condition, so neither is
                    // un-hidden or un-seeded by it.
                    bannedTags.UnionWith(suppressKeys);
                    // Artist bans bind THIS sampler too, not just the random-artist pick below. With artists in the
                    // generation mask the tag model emits artist-type names, so a ban held under Kind=Artist (or an
                    // artist the user negated) has to be suppressed in the same call — otherwise the one sampler that
                    // can produce an artist is the one that ignores the artist bans.
                    bannedTags.UnionWith(bans.Artists);
                    bannedTags.UnionWith(negKeys.Artists);
                    // The generation mask for this slot: the one submitted with it, or the owner's stored mask when the
                    // caller specified none. It rides on the SLOT (unlike the bans, which stay a server-side fact read
                    // fresh here) because it is a composer control now — the chips under the Random prompt slider — so a
                    // queued batch renders under the mask it was submitted with, not whatever the chips say by the time
                    // it comes up. Bounds THIS path only — tag autocomplete is unaffected by it.
                    var allowedTypes = await AllowedTagTypesAsync(slot.Job.Owner, slot.Gen.TagTypes, ct);
                    var gen = await _tagModel.GenerateAsync(seed, slot.Gen.Temperature, bannedTags, allowedTypes, ct);
                    var genOut = gen is null ? "(null)" : string.Join(", ", gen);
                    // The predictor's in/out goes to the PER-USER ENCRYPTED log and nowhere else. Duplicating it to
                    // the plaintext app log would be one toggle away from writing prompts to disk permanently once a
                    // file sink exists — and the encrypted line below already carries the same content, so that
                    // duplication would buy nothing but the risk.
                    await _userLog.LogAsync(slot.Job.Owner, "random_prompt", $"IN seed=[{seed}]  OUT=[{genOut}]", ct);
                    if (gen is { Count: > 0 })
                    {
                        // Appended on the canonical token in marker form: the finalizer renders it (folding underscores
                        // per the model's rules) and marks it, so the sampled names are chips exactly like the typed
                        // ones — '@' for the artist-type names, '#' for the rest, per the same catalog the chips take
                        // their category from.
                        var additions = PromptFinalizer.MarkSampled(gen, bannedTags, _tags.IsArtist);
                        if (additions.Count > 0)
                            raw = PromptFinalizer.Append(raw, string.Join(", ", additions));
                    }
                }
                // Random-artist: pick a fresh artist PER SLOT (so a batch gets a different one per image), model permitting.
                if (slot.Gen.RandomArtist == true && info?.Tagging is { Artists: true })
                {
                    var bannedArtists = bans.Artists;
                    bannedArtists.UnionWith(negKeys.Artists);
                    var artist = _tags.RandomArtist(bannedArtists.Count > 0 ? bannedArtists : null);
                    if (!string.IsNullOrEmpty(artist))
                        raw = PromptFinalizer.Append(raw, PromptMarkers.ArtistMarker + PromptFinalizer.Normalize(artist));
                }
                // The single derivation: the prompt the model renders and the marks that describe it both come from the
                // raw string we are about to store, so the three can never disagree.
                var final = PromptFinalizer.Finalize(raw, info?.Tagging);
                slot.RawPrompt = raw;
                // The negative is stored exactly as submitted. The random samplers never touch it (they only ever ADD a
                // positive), so verbatim here is simply what the user typed — null when they typed nothing, which is
                // what leaves the model's built-in default negative standing alone.
                slot.RawNegativePrompt = slot.Gen.NegativePrompt;
                slot.EffectivePrompt = final.Rendered;
                slot.Marks = final.Marks;
                await _userLog.LogAsync(slot.Job.Owner, "submit", final.Rendered, ct);
                // Finalize the negative with the same tag rules as the positive (the negative box shares the tag/artist
                // autocomplete, so its text carries '#'/'@' markers). Comfy appends this onto the model's default negative.
                var genNeg = PromptFinalizer.Finalize(slot.Gen.NegativePrompt, info?.Tagging).Rendered;
                var submit = await _comfy.SubmitGenerateAsync(final.Rendered, genNeg, slot.Gen.Workflow, slot.Gen.Aspect, slot.Gen.Overrides, slot.Gen.Loras, ct);
                promptId = submit.PromptId;
                slot.EtaSignature = submit.Eta;
            }

            if (!resuming)
            {
                // The prompt is with the backend — our fair-queue wait is over. Stamp submit time + expected render
                // seconds (this machine's recent average, or null the first time) for the ETA. This is not the same
                // as the slot RUNNING: the backend decides when it starts executing, and says so on the next poll.
                var startedAt = DateTimeOffset.UtcNow;
                double? expected = null;
                try
                {
                    // Param-matched ETA ONLY: the recent signature samples scaled to this request's resolution/steps/
                    // frames. No fallback — a config with no signature history yet shows NO ETA, rather than a flat
                    // per-model average that would be a wrong number for these params.
                    double? avgMs = slot.EtaSignature is { } sig
                        ? await _timings.EtaAverageMsAsync(_machine, slot.Model, sig, 10, ct)
                        : null;
                    expected = avgMs is double ms ? ms / 1000.0 : null;
                }
                catch (Exception ex)
                {
                    // The ETA is a decoration on a render that is already submitted; losing it must not fail the
                    // render. But a timings table that has stopped answering is worth knowing about; saying nothing
                    // would leave "no model ever shows an ETA" with no trail leading anywhere.
                    _log.LogWarning(ex, "ETA lookup failed for {Model}; this slot renders without one.", slot.Model);
                }
                lock (_lock) { slot.ComfyPromptId = promptId; _comfyToSlot[promptId] = slot; slot.GenStartedAt = startedAt; slot.ExpectedGenSeconds = expected; }
                await PersistAsync(slot.Job);   // record the promptId, so a restart resumes this render instead of redoing it
            }

            // Poll for the result; no deadline. Ends on completion, a user cancel, the backend losing the prompt, or shutdown.
            GeneratedImage? img = null;
            while (!ct.IsCancellationRequested)
            {
                if (slot.CancelRequested) break;
                await Task.Delay(1500, ct);
                img = await _comfy.PollResultAsync(promptId, ct);
                if (img is not null) break;
                var backend = await _comfy.GetQueueAsync(ct);
                if (backend is null) continue;                                // backend unreachable -> unknown, keep waiting
                // The one place a slot becomes (or stops being) "running": the backend's own account of what its GPU
                // is executing. A prompt merely sitting in its queue is still waiting, and says so.
                ObserveExecuting(slot, backend.Executing.Contains(promptId));
                if (backend.Has(promptId)) { slot.MissedLivenessChecks = 0; continue; }
                if (++slot.MissedLivenessChecks >= LivenessVanishThreshold)
                { FailSlot(slot, "the renderer no longer has this job (it likely restarted)"); return; }
            }
            if (img is null)
            {
                // Cancelled in the window between the pre-submit check and the prompt reaching the backend: Cancel saw
                // nothing to interrupt, so stop the render we went on to start rather than leave it running for nobody.
                // Guarded on the user's cancel, not on `ct` — a shutdown leaves the prompt alone so a restart resumes it.
                if (slot.CancelRequested && slot.Submitted)
                {
                    // The slot resolves as cancelled either way; the interrupt is what stops the GPU from finishing a
                    // render nobody will collect. If it does not land, say so — do not drop the reason.
                    try { await _comfy.InterruptAsync(CancellationToken.None); }
                    catch (Exception ex) { _log.LogError(ex, "Late cancel for slot {Index}: the backend interrupt failed; its render may still be running.", slot.Index); }
                }
                CancelSlot(slot);
                return;
            }

            // Success — record the actual render duration (submit -> image; queue wait excluded) for future ETAs.
            try
            {
                var ms = (int)Math.Clamp((DateTimeOffset.UtcNow - (slot.GenStartedAt ?? DateTimeOffset.UtcNow)).TotalMilliseconds, 0, int.MaxValue);
                var etaSig = slot.EtaSignature;
                await _timings.AddAsync(new GenTimingEntry(_machine, slot.Model, slot.IsEdit, ms,
                    etaSig?.Width, etaSig?.Height, etaSig?.Steps, etaSig?.Frames), ct);
            }
            catch (Exception ex)
            {
                // Telemetry, and the image is already rendered — this must not fail the slot. It must still be
                // recorded: these samples are what every future ETA is computed from, so losing them silently
                // degrades the ETAs of every later render with nothing to attribute it to.
                _log.LogWarning(ex, "Render-timing sample could not be recorded for {Model}.", slot.Model);
            }

            // The artifact's media type is the file ComfyUI wrote: a still (.png), the silent animated-webp clip most
            // video models save (.webp), or a real mp4 CONTAINER — MiniMax-H3 alone saves an mp4 with a baked-in stereo
            // AUDIO track (webp can't carry audio; see MiniMaxH3Workflows -> SaveVideo). Content type follows the
            // container; it rides through the blob and the serve path (/image/{id}, /image/{id}/mp4 pass-through) with
            // the audio intact and never re-transcoded.
            var isMp4 = img.Filename.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
            var isVideo = isMp4 || img.Filename.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);
            var contentType = isMp4 ? "video/mp4" : isVideo ? "image/webp" : "image/png";

            // A workflow that DECLARES video must have produced a clip. SaveAnimatedWEBP writes a .webp whether it was
            // handed one frame or forty, so the extension says nothing — a single-frame still can come back from a
            // workflow that asked for many frames and still read as "done", surfacing only later as an unreadable
            // source in an editor that consumes clips. A render that did not make the thing it exists to make is a
            // failed render. An mp4 is a video container by construction (CreateVideo), so it counts as a clip.
            var declared = _catalog.ResolveInfo(slot.IsEdit ? slot.RequireEdit().Workflow : slot.RequireGen().Workflow);
            if (declared?.ProducesVideo == true && !(isMp4 || _media.IsAnimatedWebp(img.Png)))
                throw new RenderValidationException(
                    "This is a video workflow and the render came back as a single frame, not a clip. "
                    + "The frame count reaching the graph is the thing to look at.");
            // An output whose header will not read is a FAILED render, not a 0x0 image. Substituting (0, 0) here would
            // write a fabricated size into the blob row and into history, where nothing downstream could tell it from a
            // real measurement. Let it throw: the handler at the bottom of this method fails the slot with the real reason.
            // ImageSharp reads a still/webp; an mp4 needs the container's own box tree (ImageSharp can't read it).
            var dims = isMp4 ? _media.IdentifyVideo(img.Png) : _media.Identify(img.Png);
            var (w, h) = (dims.Width, dims.Height);

            if (slot.IsEdit && src is not null)
            {
                // 1.0 ("fully changed") is the honest answer for a video, which has no still pHash to compare against.
                // For a still it has to be MEASURED: defaulting a failed comparison to 1.0 would wave the result
                // straight past the no-change gate — the one case the gate exists to catch — storing a declined edit
                // as a successful one. A comparison that cannot run fails the slot instead.
                double diff = isVideo ? 1.0 : _media.Difference(src, img.Png);
                // Some edits intentionally preserve composition (inpaint; pixel transforms). Their whole-image pHash
                // diff is tiny BY DESIGN and would trip the no-change gate, so those workflows opt out.
                bool preservesComposition = _catalog.ResolveInfo(slot.RequireEdit().Workflow)?.PreservesComposition ?? false;
                if (!preservesComposition && diff < _media.NoChangeThreshold) { SlotEditNoChange(slot, Math.Round(diff, 3)); return; }
                var editId = await StoreImageAsync(img.Png, contentType, w, h, ct);
                await PersistSpriteDataAsync(editId, img, ct);
                await WriteHistoryAsync(slot, editId, ct);
                SlotDone(slot, editId, w, h, changed: true, score: isVideo ? null : Math.Round(diff, 3));
                return;
            }
            var id = await StoreImageAsync(img.Png, contentType, w, h, ct);
            await PersistSpriteDataAsync(id, img, ct);
            await WriteHistoryAsync(slot, id, ct);
            SlotDone(slot, id, w, h);
        }
        catch (RenderValidationException ex) { FailSlot(slot, ex.Message); }
        // The user asked to stop. Shutdown ALSO cancels, and that must not resolve the slot: it is accepted work that
        // a restart resumes, and marking it cancelled because the process is going down discards it.
        catch (OperationCanceledException) when (slot.CancelRequested) { CancelSlot(slot); }
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
        catch (Exception ex) { FailSlot(slot, ex.Message); }
    }

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
            await _blobs.SetPaletteAsync(imageId, img.PaletteJson, ct);
        if (!string.IsNullOrEmpty(img.FrequenciesJson))
            await _blobs.SetFrequenciesAsync(imageId, img.FrequenciesJson, ct);
        if (img.LosslessFrames is { Count: > 0 } frames)
            await _frames.AddFramesAsync(imageId, frames, ct);
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
                throw new InvalidOperationException(
                    $"Slot {slot.Job.JobId}#{slot.Index} is not the worker's current slot; only it may be Running.");
            if (slot.Terminal) return;
            slot.State = executing ? SlotState.Running : SlotState.Queued;
        }
    }

    private void SlotDone(RenderSlot slot, string id, int w, int h, bool changed = true, double? score = null)
    {
        lock (_lock)
        {
            if (slot.Terminal) return;   // cancelled while the result was landing — don't resurrect it as Done
            slot.ImageId = id; slot.Width = w; slot.Height = h;
            slot.Changed = changed; slot.ChangeScore = score;
            slot.State = SlotState.Done;
        }
    }

    private void SlotEditNoChange(RenderSlot slot, double score)
    {
        lock (_lock)
        {
            if (slot.Terminal) return;
            slot.Changed = false; slot.ChangeScore = score; slot.State = SlotState.Done;
        }
    }

    private void FailSlot(RenderSlot slot, string error)
    {
        lock (_lock) { if (slot.Terminal) return; slot.Error = error; slot.State = SlotState.Error; }
    }

    /// <summary>Resolve a slot the user stopped. Terminal like <see cref="FailSlot"/>, but as its own state: nothing
    /// went wrong, and the reason string is kept as the detail rather than as the only place the difference lived.</summary>
    private void CancelSlot(RenderSlot slot)
    {
        lock (_lock) { if (slot.Terminal) return; slot.Error = "cancelled"; slot.State = SlotState.Cancelled; }
    }

    /// <summary>After a slot resolves: write the job through, and if every slot is now terminal, finalize the job and
    /// drop it from the active maps (the DB holds the finalized record).</summary>
    private async Task AfterSlotAsync(RenderJob job)
    {
        var finalize = false;
        lock (_lock)
            if (job.AllTerminal && job.FinishedAt is null) { job.FinishedAt = DateTimeOffset.UtcNow; finalize = true; }

        var persisted = await PersistAsync(job);

        // Finalizing means dropping the job from memory next — so if the write failed, memory is about to become the
        // only place the outcome ever existed. Keep the job resident (it is terminal, so it renders nothing) and let a
        // later write carry it, rather than silently losing the result and leaving the row Active to replay forever.
        if (finalize && !persisted)
        {
            lock (_lock) job.FinishedAt = null;   // not finished until it is written down
            return;
        }

        if (finalize)
        {
            // Now that the write-through is done and this job will never upsert again, drop any slot whose image the
            // user deleted while the batch was still running (the delete cascade has to leave live slots alone).
            try { await _jobRepo.SweepDeletedImageSlotsAsync(job.JobId, CancellationToken.None); }
            catch (Exception ex) { _log.LogWarning(ex, "Slot sweep failed for job {JobId}", job.JobId); }

            lock (_lock)
            {
                _jobs.Remove(job.JobId);
                foreach (var s in job.Slots) if (s.ComfyPromptId is { } c) _comfyToSlot.Remove(c);
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
    /// <para>Anything else still only logs: a render that produced a real image must not be failed for a bad write.</para>
    /// </summary>
    private async Task WriteHistoryAsync(RenderSlot slot, string imageId, CancellationToken ct)
    {
        try
        {
            var modelId = slot.Model;
            var friendly = _catalog.ResolveInfo(modelId)?.FriendlyName ?? modelId;
            // The raw (marker-form) prompt falls back to the submitted spec only for a slot that produced an image
            // without going through RunSlotAsync's prompt build — the same fallback shape the EffectivePrompt line uses.
            var raw = slot.RawPrompt ?? (slot.IsEdit ? slot.RequireEdit().Instruction : slot.RequireGen().Prompt);
            var rawNegative = slot.RawNegativePrompt ?? (slot.IsEdit ? slot.RequireEdit().NegativePrompt : slot.RequireGen().NegativePrompt);
            var prompt = slot.EffectivePrompt ?? raw;
            // What the user TYPED, which for a generate only the client knows (it resolves [a|b], {a|b} and the
            // artist lock before submitting) and so travels on the spec. An edit's instruction goes through no
            // sampler at all, so for those the submitted string IS the original.
            var original = slot.IsEdit ? slot.RequireEdit().Instruction : slot.RequireGen().OriginalPrompt;
            // A generate that reached here rendered, and a render only happens once NormalizeAspect has accepted the
            // aspect at submit (it throws on anything but square/landscape/portrait) — so a null here is not a missing
            // value to fill with "square", it is a broken invariant. Edits carry no aspect: "" is their real value.
            var aspect = slot.IsEdit ? "" : (slot.RequireGen().Aspect
                ?? throw new InvalidOperationException("A rendered generate reached history with no aspect, which NormalizeAspect should have made impossible at submit."));
            IReadOnlyList<Mark> marks = slot.Marks is not { Count: > 0 }
                ? Array.Empty<Mark>()
                : slot.Marks.Select(kv => new Mark(kv.Key, TokenKinds.Parse(kv.Value))).ToList();
            // The user LoRA stack this image was generated with (generates only). Recorded so the viewer lists them
            // and Reload reproduces the exact stack; empty for edits and for generations that used none.
            var genLoras = slot.IsEdit ? null : slot.RequireGen().Loras;
            IReadOnlyList<HistoryLora> loras = genLoras is not { Count: > 0 }
                ? Array.Empty<HistoryLora>()
                : genLoras.Select(l => new HistoryLora(l.Name, l.Weight)).ToList();

            var entry = new HistoryEntry
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
                using var scope = _scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IHistoryRepository>().AddAsync(entry, c);
            }, $"recording image {imageId} in history", ct);
        }
        catch (Exception ex) { _log.LogError(ex, "History write failed for image {ImageId} (job {JobId}).", imageId, slot.Job.JobId); }
    }

    #endregion

    #region persistence (write-through)

    /// <summary>Write the job through. Returns false if the write failed, so a caller about to DISCARD the in-memory
    /// job can decline to — on the finalizing write, memory holds the only copy of the outcome.</summary>
    private async Task<bool> PersistAsync(RenderJob job)
    {
        JobRecord rec;
        lock (_lock) rec = ToRecord(job);
        try
        {
            // Waits out an unreachable database rather than reporting the write as failed. Nothing re-drives a
            // dropped persist: AfterSlotAsync relies on a LATER write carrying it, and for the last slot of the last
            // job there is no later write — the job then sits resident and unpersisted while its row stays Active
            // forever. That is the zombie-Active-row mechanism, and it is this call that has to not give up.
            await AwaitingDatabaseAsync(
                ct => _jobRepo.UpsertAsync(rec, ct), $"persisting job {job.JobId}", CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Job persist failed for {JobId} ({Slots} slots, status {Status}).",
                job.JobId, rec.Slots.Count, rec.Status);
            return false;
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
        var delay = TimeSpan.FromSeconds(5);
        var since = DateTimeOffset.UtcNow;
        while (true)
        {
            try { return await operation(ct); }
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
        await AwaitingDatabaseAsync<object?>(async c => { await operation(c); return null; }, what, ct);

    /// <summary>Snapshot the in-memory job into its durable record. Called under _lock. Status derives from the slots:
    /// Active until all terminal, then Done if anything was produced, else Error.</summary>
    private JobRecord ToRecord(RenderJob j)
    {
        var rec = new JobRecord
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
        foreach (var s in j.Slots.OrderBy(s => s.Index))
            rec.Slots.Add(new JobSlotRecord
            {
                JobId = j.JobId,
                SlotIndex = s.Index,
                IsEdit = s.IsEdit,
                // A running slot persists as Queued. "Running" is a live fact about a GPU that this row cannot keep
                // true past the process's life, and writing it anyway would leave every crashed or orphaned job
                // claiming to render forever. Resuming needs no more than what's already here: non-terminal, plus the
                // ComfyPromptId to pick the poll back up.
                State = RenderPhases.Persisted(s.State),
                ComfyPromptId = s.ComfyPromptId,
                ImageId = s.ImageId,
                Width = s.Width == 0 ? null : s.Width,
                Height = s.Height == 0 ? null : s.Height,
                Changed = s.Changed,
                ChangeScore = s.ChangeScore,
                Error = s.Error,
                EffectivePrompt = s.EffectivePrompt,
                RawPrompt = s.RawPrompt,
                RawNegativePrompt = s.RawNegativePrompt,
                Marks = s.Marks is null ? [] : s.Marks.Select(kv => new Mark(kv.Key, TokenKinds.Parse(kv.Value))).ToList(),
                GenStartedAtUtc = s.GenStartedAt?.UtcDateTime,
                ExpectedGenSeconds = s.ExpectedGenSeconds,
                // The spec, field by field — stored as columns rather than one blob, with the ids left legible so the
                // database can join and cascade on them.
                Workflow = s.IsEdit ? s.RequireEdit().Workflow : s.RequireGen().Workflow,
                Prompt = s.IsEdit ? s.RequireEdit().Instruction : s.RequireGen().Prompt,
                NegativePrompt = s.IsEdit ? s.RequireEdit().NegativePrompt : s.RequireGen().NegativePrompt,
                Aspect = s.IsEdit ? null : s.RequireGen().Aspect,
                RandomArtist = s.IsEdit ? null : s.RequireGen().RandomArtist,
                RandomPrompt = s.IsEdit ? null : s.RequireGen().RandomPrompt,
                Temperature = s.IsEdit ? null : s.RequireGen().Temperature,
                TagTypesJson = s.IsEdit || s.RequireGen().TagTypes is null ? null : JsonSerializer.Serialize(s.RequireGen().TagTypes),
                OverridesJson = OverridesJsonOf(s),
                LorasJson = LorasJsonOf(s),
                SourceImageId = s.IsEdit ? s.RequireEdit().ImageId : null,
                MaskImageId = s.IsEdit ? s.RequireEdit().MaskImageId : null,
                LastFrameImageId = s.IsEdit ? s.RequireEdit().LastFrameImageId : null,
                ReferenceImageIds = s.IsEdit ? [.. s.RequireEdit().ReferenceImageIds ?? []] : [],
            });
        return rec;
    }

    /// <summary>The slot's exposed-parameter values as stored JSON, or null when it set none. An arbitrary bag keyed
    /// by parameter name — not a relation to anything — so it stays JSON, and plain: none of it is protected.</summary>
    private static string? OverridesJsonOf(RenderSlot s)
    {
        var overrides = s.IsEdit ? s.RequireEdit().Overrides : s.RequireGen().Overrides;
        return overrides is null ? null : JsonSerializer.Serialize(overrides);
    }

    /// <summary>The slot's user LoRA stack as stored JSON, or null when it used none (generates only — an edit has no
    /// LoRA stack). A plain per-slot value bag, like <see cref="OverridesJsonOf"/>, so a resumed batch keeps its LoRAs.</summary>
    private static string? LorasJsonOf(RenderSlot s)
    {
        if (s.IsEdit) return null;
        var loras = s.RequireGen().Loras;
        return loras is not { Count: > 0 } ? null : JsonSerializer.Serialize(loras);
    }

    /// <summary>Rebuild a slot's generate spec from its typed columns. No deserialization contract to get wrong: a
    /// column that went missing is a database error, not a silently-null property.</summary>
    private static GenerateSpec GenerateSpecOf(JobSlotRecord sr) => new(
        sr.Workflow ?? "",
        sr.Prompt ?? "",
        sr.NegativePrompt,
        sr.Aspect,
        sr.RandomArtist,
        sr.RandomPrompt,
        sr.Temperature,
        Deser<Dictionary<string, JsonElement>>(sr.OverridesJson),
        Deser<List<string>>(sr.TagTypesJson),
        Loras: Deser<List<LoraSelection>>(sr.LorasJson));

    /// <summary>Rebuild a slot's edit spec from its typed columns and its reference child rows.</summary>
    private static EditSpec EditSpecOf(JobSlotRecord sr) => new(
        sr.Workflow ?? "",
        sr.Prompt ?? "",
        sr.SourceImageId ?? "",
        sr.NegativePrompt,
        [.. sr.ReferenceImageIds],
        Deser<Dictionary<string, JsonElement>>(sr.OverridesJson),
        sr.MaskImageId,
        sr.LastFrameImageId);

    /// <summary>Reload this instance's still-active jobs and resume them: a mid-render slot keeps its
    /// prompt id and is re-queued to RESUME polling; an unsubmitted slot renders fresh; a slot whose request payload
    /// was lost is failed so the job can still finalize. Returns false on any failure — the caller retries until a
    /// pass succeeds, and jobs already in memory are skipped so retries cannot duplicate.</summary>
    private async Task<bool> RehydrateAsync(CancellationToken ct)
    {
        IReadOnlyList<JobRecord> active;
        try { active = await _jobRepo.ListActiveForMachineAsync(_machine, ct); }
        catch (Exception ex) { _log.LogError(ex, "Job rehydrate failed; will retry."); return false; }
        if (active.Count == 0) return true;

        var resumed = 0;
        try
        {
            foreach (var rec in active)
            {
                lock (_lock)
                {
                    if (_jobs.ContainsKey(rec.JobId)) continue;   // already live — a retry after a partial pass
                }

                // One unresumable job must not sink the pass. Rehydration is ordered oldest-first, so a row that
                // throws every time (a malformed slot set, a command timeout on a huge batch) would otherwise be
                // retried at the head of the queue forever, and every job behind it would stay Active with nothing
                // running — unfinishable, and uncancellable because Cancel only knows in-memory jobs. Fail that one
                // job and carry on: a job this instance cannot bring back is over, and its row should say so.
                try
                {
                    resumed += await RehydrateJobAsync(rec);
                }
                // An unreachable database is not a property of THIS job — it affects every job equally, and the pass
                // itself cannot continue without it. Rethrow so the outer retry waits for the connection instead of
                // converting recoverable work into permanently failed work, one job at a time. (FailAsync needs the
                // database too, so trying to fail the job here would throw anyway.)
                catch (Exception ex) when (_db.IsUnavailable(ex))
                {
                    _log.LogWarning(ex, "Rehydrate reached job {JobId} with the database unreachable; the pass will retry.", rec.JobId);
                    throw;
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Job {JobId} could not be resumed; marking it failed.", rec.JobId);
                    await _jobRepo.FailAsync(rec.JobId, "could not be resumed after restart", ct);
                    // The throw may have landed after the job was published to the maps, so drop it AND any slots of
                    // it already queued — the row now says failed, and no worker may pick it up.
                    lock (_lock)
                    {
                        _jobs.Remove(rec.JobId);
                        if (_byOwner.TryGetValue(rec.UserId, out var q))
                            _byOwner[rec.UserId] = new Queue<RenderSlot>(
                                q.Where(s => !string.Equals(s.Job.JobId, rec.JobId, StringComparison.Ordinal)));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Job rehydrate interrupted mid-pass; the remainder will retry.");
            if (resumed > 0) _signal.Release(resumed);
            return false;
        }

        if (resumed > 0) _signal.Release(resumed);
        _log.LogInformation("Rehydrated {Jobs} active job(s), {Slots} slot(s) resumed.", active.Count, resumed);
        return true;
    }

    /// <summary>Rebuild one persisted job into the in-memory queue and return how many of its slots were re-queued.
    /// Throws if the record cannot be turned into a runnable job — the caller fails that job rather than retrying it
    /// forever.</summary>
    private async Task<int> RehydrateJobAsync(JobRecord rec)
    {
        var resumed = 0;
        var job = new RenderJob
        {
            JobId = rec.JobId,
            Owner = rec.UserId,
            MachineName = rec.MachineName,
            CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(rec.CreatedAtUtc, DateTimeKind.Utc)),
        };
        foreach (var sr in rec.Slots.OrderBy(s => s.SlotIndex))
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
                if (sr.IsEdit) edit = EditSpecOf(sr);
                else gen = GenerateSpecOf(sr);
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
            var marks = sr.Marks.Count == 0
                ? null
                : sr.Marks.ToDictionary(m => m.Token, m => m.Kind.ToWire(), StringComparer.Ordinal);

            var slot = new RenderSlot
            {
                Job = job,
                Index = sr.SlotIndex,
                Gen = gen,
                Edit = edit,
                ComfyPromptId = sr.ComfyPromptId,
                ImageId = sr.ImageId,
                Width = sr.Width ?? 0,
                Height = sr.Height ?? 0,
                Changed = sr.Changed,
                ChangeScore = sr.ChangeScore,
                Error = parseError ?? sr.Error,
                EffectivePrompt = sr.EffectivePrompt,
                RawPrompt = sr.RawPrompt,
                RawNegativePrompt = sr.RawNegativePrompt,
                Marks = marks,
                GenStartedAt = sr.GenStartedAtUtc is { } g ? new DateTimeOffset(DateTime.SpecifyKind(g, DateTimeKind.Utc)) : null,
                ExpectedGenSeconds = sr.ExpectedGenSeconds,
                State = parseError is not null ? SlotState.Error : RenderPhases.Live(sr.State),
            };
            job.Slots.Add(slot);
        }

        lock (_lock)
        {
            _jobs[job.JobId] = job;
            if (!_byOwner.TryGetValue(job.Owner, out var q)) { q = new Queue<RenderSlot>(); _byOwner[job.Owner] = q; }
            foreach (var s in job.Slots)
            {
                if (s.Terminal) continue;
                if (s.Gen is null && s.Edit is null) { s.State = SlotState.Error; s.Error = "lost on restart"; continue; }

                // A slot with no workflow can never render, and left Queued it keeps its job Active forever, so it is
                // failed here with a reason. The workflow is its own column, so this only fires for a row migrated
                // without one.
                var workflow = s.IsEdit ? s.Edit?.Workflow : s.Gen?.Workflow;
                if (string.IsNullOrWhiteSpace(workflow))
                {
                    s.State = SlotState.Error;
                    s.Error = "unrunnable: this slot recorded no workflow";
                    continue;
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
        var bans = await AwaitingDatabaseAsync(async c =>
        {
            using var scope = _scopeFactory.CreateScope();
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
            if (!GenerationTagTypes.TryNormalize(requested, out var types, out var error))
                throw new InvalidOperationException($"Slot carries an invalid generation mask: {error}");
            return types;
        }

        // Waits out an unreachable database, like the ban list: falling back to the default mask would silently
        // generate tag kinds the user switched off, and failing the slot would throw away queued work over an
        // outage. A fresh scope per attempt (the repository is scoped).
        var user = await AwaitingDatabaseAsync(async c =>
        {
            using var scope = _scopeFactory.CreateScope();
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
    internal static (string Seed, HashSet<string> SuppressKeys) TagSeed(string? raw, WorkflowTagging tagging)
    {
        // Finalize a copy in which every '~' guide tag is an ordinary '#'. The seed is the one place guide tags DO
        // belong, and rewriting them in place keeps each in the position the user wrote it and in the same rendered
        // form as its neighbours — appending their keys to a finished seed would reorder the prompt and mix
        // underscored keys into a seed whose other tags had their underscores folded.
        var typed = PromptFinalizer.Finalize(PromptMarkers.GuidesAsTags(raw), tagging);
        var inertKeys = PromptMarkers.InertKeys(raw);
        var guideKeys = PromptMarkers.GuideKeys(raw);
        var hidden = typed.Marks.Where(kv => kv.Value == TokenKinds.Artist).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        hidden.UnionWith(inertKeys);
        var seed = string.Join(", ", PromptMarkers.Segments(typed.Rendered)
                                                 .Where(seg => !hidden.Contains(PromptMarkers.Key(seg))));

        // Both kinds must be banned for this call, for opposite reasons that land in the same place: an inert tag is
        // hidden from the seed, so the predictor may freely sample it back; a guide tag IS the seed, and anything it
        // echoed would be appended as a '#' tag and rendered — the one outcome '~' exists to prevent.
        var suppress = new HashSet<string>(inertKeys, StringComparer.Ordinal);
        suppress.UnionWith(guideKeys);
        return (seed, suppress);
    }

    /// <summary>Split banned tokens into the canonical tag/artist key sets the random samplers honour (the tag model
    /// zeroes these during sampling; RandomArtist rejects them). A key is canonicalized exactly like a prompt token —
    /// marker stripped, lowercased, spaces to underscores — so a ban typed free-hand into Settings as "Wet Shirt" still
    /// matches the "wet_shirt" the model would sample.</summary>
    internal static (HashSet<string> Tags, HashSet<string> Artists) BanKeys(IEnumerable<BannedToken> bans)
    {
        var tags = new HashSet<string>(StringComparer.Ordinal);
        var artists = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in bans)
        {
            var key = PromptFinalizer.Normalize(b.Name).TrimStart('#', '@');
            if (key.Length == 0) continue;
            (b.Kind == TokenKind.Artist ? artists : tags).Add(key);
        }
        return (tags, artists);
    }

    /// <summary>Source bytes for an edit/reference id: an in-memory upload first (the user's own source/reference/mask,
    /// which is never persisted), then the DB blob, else the legacy backend view fetch. Throws
    /// <see cref="HttpRequestException"/> when none has it, which the caller turns into a "not found".
    /// <para>An upload is process-local, so a slot that was queued but never submitted before a restart lands here with
    /// nothing to find; that surfaces as a slot error naming the missing source rather than a silent failure.</para></summary>
    private async Task<byte[]> GetImageBytesAsync(string id, CancellationToken ct)
    {
        if (_uploads.Get(id) is { } upload) return upload.Bytes;
        // Waits out an unreachable database rather than reporting the source as missing. "Not found" and "could not
        // be looked up" are opposite facts, and only one of them is the user's to act on: failing the slot here would
        // tell them their source image doesn't exist because the server is out of range.
        var blob = await AwaitingDatabaseAsync(c => _blobs.GetAsync(id, c), $"loading input image {id}", ct);
        if (blob is not null) return blob.Bytes;
        return await _comfy.FetchLegacyImageAsync(id, ct);
    }

    #endregion
}
