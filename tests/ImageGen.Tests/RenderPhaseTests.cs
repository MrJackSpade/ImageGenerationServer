using ImageGen.Application.Rendering;
using ImageGen.Domain.Entities;

namespace ImageGen.Tests;

/// <summary>
/// "Running" means one thing: the GPU is generating this image right now. The box renders one image at a time, so at
/// most one job can ever be running, and anything else that is merely unfinished is WAITING.
///
/// <para>These pin that. A job that reads "running" while no GPU is holding it fills the queue with jobs all claiming
/// to render while the GPU sits idle, which looks like a stuck renderer rather than a lying status. Each test below is
/// one of the ways something not on a GPU could wrongly read as "running".</para>
/// </summary>
public sealed class RenderPhaseTests
{
    private static RenderJob JobWith(params SlotState[] states)
    {
        RenderJob job = new()
        {
            JobId = "j1",
            Owner = 1,
            MachineName = "TESTBOX",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        for (int i = 0; i < states.Length; i++)
        {
            job.Slots.Add(new RenderSlot
            {
                Job = job,
                Index = i,
                Gen = new GenerateSpec("anima", "a prompt", null, "square"),
                State = states[i],
                ImageId = states[i] == SlotState.Done ? $"img{i}" : null,
            });
        }

        return job;
    }

    /// <summary>A batch that has produced an image and is waiting for the rest is WAITING. A rule of "any slot has left
    /// Queued" would let one finished slot make a ten-image job read as running for as long as it sits in the queue.</summary>
    [Fact]
    public void A_batch_with_a_finished_slot_and_the_rest_waiting_is_queued()
    {
        RenderJob job = JobWith(SlotState.Done, SlotState.Queued, SlotState.Queued);

        Assert.Equal(RenderPhase.Queued, RenderPhases.Of(job));
    }

    /// <summary>Nor does a failed slot start a job rendering.</summary>
    [Fact]
    public void A_batch_with_a_failed_slot_and_the_rest_waiting_is_queued()
    {
        RenderJob job = JobWith(SlotState.Error, SlotState.Queued);

        Assert.Equal(RenderPhase.Queued, RenderPhases.Of(job));
    }

    /// <summary>A job is running only while one of its own slots is on the GPU.</summary>
    [Fact]
    public void A_job_with_an_executing_slot_is_running()
    {
        RenderJob job = JobWith(SlotState.Done, SlotState.Running, SlotState.Queued);

        Assert.Equal(RenderPhase.Running, RenderPhases.Of(job));
    }

    /// <summary>All slots resolved with at least one image: done. Nothing about it is running.</summary>
    [Fact]
    public void A_finished_batch_is_done_even_when_some_slots_failed()
    {
        RenderJob job = JobWith(SlotState.Done, SlotState.Error);

        Assert.Equal(RenderPhase.Done, RenderPhases.Of(job));
    }

    /// <summary>Every slot failed and nothing was produced: error.</summary>
    [Fact]
    public void A_batch_that_produced_nothing_is_error()
    {
        RenderJob job = JobWith(SlotState.Error, SlotState.Error);

        Assert.Equal(RenderPhase.Error, RenderPhases.Of(job));
    }

    /// <summary>A blob that landed but could not be linked into history remains addressable by id, yet the slot is a
    /// visible failure rather than a false success. This is the non-outage history-write path from #243.</summary>
    [Fact]
    public void A_history_write_defect_keeps_the_stored_image_but_does_not_mark_the_slot_done()
    {
        RenderJob job = JobWith(SlotState.Queued);
        RenderSlot slot = Assert.Single(job.Slots);

        RenderOrchestrator.ApplyHistoryWriteFailure(slot, "stored-image", 640, 480);

        Assert.Equal(SlotState.Error, slot.State);
        Assert.Equal("stored-image", slot.ImageId);
        Assert.Equal(640, slot.Width);
        Assert.Equal(480, slot.Height);
        Assert.Contains("could not be added to history", slot.Error);
    }

    /// <summary>A generation the user stopped is CANCELLED, not failed. Nothing went wrong — they did that.</summary>
    [Fact]
    public void A_stopped_generation_is_cancelled_not_error()
    {
        RenderJob job = JobWith(SlotState.Cancelled);

        Assert.Equal(RenderPhase.Cancelled, RenderPhases.Of(job));
    }

    /// <summary>
    /// A batch stopped part-way reads as cancelled WITH its count: ten asked for, three landed, then you stopped it.
    /// "done · 3/10" would claim it ran its course, making a deliberate stop look like a result.
    /// </summary>
    [Fact]
    public void A_batch_stopped_after_some_images_landed_is_cancelled_not_done()
    {
        RenderJob job = JobWith(SlotState.Done, SlotState.Done, SlotState.Done, SlotState.Cancelled, SlotState.Cancelled);

        Assert.Equal(RenderPhase.Cancelled, RenderPhases.Of(job));
        Assert.Equal(3, job.Produced);
    }

    /// <summary>A cancelled slot is terminal, so it does not hold its job open.</summary>
    [Fact]
    public void A_cancelled_slot_is_terminal()
    {
        RenderJob job = JobWith(SlotState.Done, SlotState.Cancelled);

        Assert.True(job.AllTerminal);
        Assert.Equal(2, job.Progress);
    }

    /// <summary>A merely FAILED slot does not outrank done: a partial failure reads as done-with-fewer, and only
    /// cancellation outranks done.</summary>
    [Fact]
    public void A_failed_slot_does_not_outrank_done_the_way_a_cancelled_one_does()
    {
        Assert.Equal(RenderPhase.Done, RenderPhases.Of(JobWith(SlotState.Done, SlotState.Error)));
        Assert.Equal(RenderPhase.Cancelled, RenderPhases.Of(JobWith(SlotState.Done, SlotState.Cancelled)));
    }

    /// <summary>
    /// An Active database row never reads as running. It is read precisely when the job is NOT in its owning
    /// instance's live set — it may be waiting to rehydrate, or stranded by a crash with nothing that will ever pick
    /// it up — and in every one of those cases no GPU is holding it.
    /// </summary>
    [Fact]
    public void An_active_durable_row_is_queued_never_running()
    {
        Assert.Equal(RenderPhase.Queued, RenderPhases.Of(JobStatus.Active));
        Assert.Equal(RenderPhase.Done, RenderPhases.Of(JobStatus.Done));
        Assert.Equal(RenderPhase.Error, RenderPhases.Of(JobStatus.Error));
        Assert.Equal(RenderPhase.Cancelled, RenderPhases.Of(JobStatus.Cancelled));
    }

    /// <summary>
    /// The durable status is derived from the same rule the client's phase is, so a row and the page it renders can
    /// never disagree about how a job ended. Two separate expressions would let a state like "cancelled" be added to
    /// one and not the other.
    /// </summary>
    [Fact]
    public void The_durable_status_agrees_with_the_phase_the_client_is_shown()
    {
        Assert.Equal(JobStatus.Active, RenderPhases.Persisted(JobWith(SlotState.Done, SlotState.Queued)));
        Assert.Equal(JobStatus.Done, RenderPhases.Persisted(JobWith(SlotState.Done, SlotState.Error)));
        Assert.Equal(JobStatus.Error, RenderPhases.Persisted(JobWith(SlotState.Error)));
        Assert.Equal(JobStatus.Cancelled, RenderPhases.Persisted(JobWith(SlotState.Done, SlotState.Cancelled)));
    }

    /// <summary>A cancelled slot round-trips through storage as itself — the whole point of it being a state rather
    /// than a string in the Error column.</summary>
    [Fact]
    public void A_cancelled_slot_round_trips_through_the_durable_state()
    {
        Assert.Equal(JobSlotState.Cancelled, RenderPhases.Persisted(SlotState.Cancelled));
        Assert.Equal(SlotState.Cancelled, RenderPhases.Live(JobSlotState.Cancelled));
        Assert.Equal(RenderPhase.Cancelled, RenderPhases.Of(JobSlotState.Cancelled));
    }

    /// <summary>A legacy slot row that still says Running reads as waiting: it was written by a process that is no
    /// longer holding that GPU.</summary>
    [Fact]
    public void A_legacy_running_slot_row_reads_as_queued()
    {
        Assert.Equal(RenderPhase.Queued, RenderPhases.Of(JobSlotState.Running));
        Assert.Equal(RenderPhase.Queued, RenderPhases.Of(JobSlotState.Queued));
        Assert.Equal(RenderPhase.Done, RenderPhases.Of(JobSlotState.Done));
        Assert.Equal(RenderPhase.Error, RenderPhases.Of(JobSlotState.Error));
    }

    /// <summary>And no new row can be written claiming it: a running slot persists as Queued, so the durable record
    /// cannot outlive the fact. Terminal states persist as themselves.</summary>
    [Fact]
    public void A_running_slot_is_never_persisted_as_running()
    {
        Assert.Equal(JobSlotState.Queued, RenderPhases.Persisted(SlotState.Running));
        Assert.Equal(JobSlotState.Queued, RenderPhases.Persisted(SlotState.Queued));
        Assert.Equal(JobSlotState.Done, RenderPhases.Persisted(SlotState.Done));
        Assert.Equal(JobSlotState.Error, RenderPhases.Persisted(SlotState.Error));
    }

    /// <summary>The wire spelling the clients switch on.</summary>
    [Fact]
    public void Phases_spell_out_to_the_wire_vocabulary()
    {
        Assert.Equal("queued", RenderPhase.Queued.Wire());
        Assert.Equal("running", RenderPhase.Running.Wire());
        Assert.Equal("done", RenderPhase.Done.Wire());
        Assert.Equal("error", RenderPhase.Error.Wire());
        Assert.Equal("cancelled", RenderPhase.Cancelled.Wire());
    }

    /// <summary>A prompt the backend is executing is running; one still sitting in the backend's own queue is not —
    /// it is waiting behind whatever is on the GPU, which is exactly what the user is told.</summary>
    [Fact]
    public void The_backend_queue_separates_executing_from_merely_pending()
    {
        BackendQueue q = new(
            Executing: new HashSet<string>(StringComparer.Ordinal) { "on-gpu" },
            Pending: new HashSet<string>(StringComparer.Ordinal) { "behind-it" });

        Assert.True(q.Executing.Contains("on-gpu"));
        Assert.False(q.Executing.Contains("behind-it"));
        // Liveness is the union: both are prompts the backend still has, so neither counts as lost.
        Assert.True(q.Has("on-gpu"));
        Assert.True(q.Has("behind-it"));
        Assert.False(q.Has("forgotten"));
    }
}
