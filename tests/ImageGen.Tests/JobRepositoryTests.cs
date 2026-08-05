using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;

namespace ImageGen.Tests;

[Collection("db")]
public sealed class JobRepositoryTests(TestDatabaseFixture fixture)
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    /// <summary>A job this instance cannot bring back must be finalized as failed, not left Active forever. An Active
    /// row with no live queue behind it is unfinishable AND uncancellable (Cancel only knows in-memory jobs), so it
    /// would sit "running" forever with nothing rendering.</summary>
    [Fact]
    public async Task Fail_finalizes_the_job_and_its_unfinished_slots()
    {
        User user = await fixture.NewUserAsync("job-fail");
        string jobId = Guid.NewGuid().ToString("N");
        await fixture.Jobs.UpsertAsync(Job(user.Id, jobId, slots:
        [
            Slot(jobId, 0, JobSlotState.Done, imageId: "produced-image"),
            Slot(jobId, 1, JobSlotState.Running),
            Slot(jobId, 2, JobSlotState.Queued),
        ]), Ct);

        await fixture.Jobs.FailAsync(jobId, "could not be resumed after restart", Ct);

        JobRecord? after = await fixture.Jobs.GetAsync(jobId, Ct);
        Assert.NotNull(after);
        Assert.Equal(JobStatus.Error, after.Status);
        Assert.NotNull(after.FinishedAtUtc);

        // The slot that actually produced an image keeps its result; only the unfinished ones are failed.
        Assert.Equal(JobSlotState.Done, after.Slots.Single(s => s.SlotIndex == 0).State);
        Assert.Equal("produced-image", after.Slots.Single(s => s.SlotIndex == 0).ImageId);
        Assert.Equal(JobSlotState.Error, after.Slots.Single(s => s.SlotIndex == 1).State);
        Assert.Equal(JobSlotState.Error, after.Slots.Single(s => s.SlotIndex == 2).State);
        Assert.Equal("could not be resumed after restart", after.Slots.Single(s => s.SlotIndex == 2).Error);
    }

    /// <summary>
    /// A stranded job the user cancels resolves as CANCELLED, not Error — the row has to be able to say which
    /// happened, because a reader that finds Error cannot tell a crash from a user pressing Cancel. Produced images
    /// are untouched, exactly as with a failure.
    /// </summary>
    [Fact]
    public async Task Cancel_resolves_the_unfinished_slots_as_cancelled_not_failed()
    {
        User user = await fixture.NewUserAsync("job-cancel");
        string jobId = Guid.NewGuid().ToString("N");
        await fixture.Jobs.UpsertAsync(Job(user.Id, jobId, slots:
        [
            Slot(jobId, 0, JobSlotState.Done, imageId: "produced-image"),
            Slot(jobId, 1, JobSlotState.Queued),
        ]), Ct);

        await fixture.Jobs.CancelAsync(jobId, Ct);

        JobRecord? after = await fixture.Jobs.GetAsync(jobId, Ct);
        Assert.NotNull(after);
        Assert.Equal(JobStatus.Cancelled, after.Status);
        Assert.NotNull(after.FinishedAtUtc);
        Assert.Equal(JobSlotState.Done, after.Slots.Single(s => s.SlotIndex == 0).State);
        Assert.Equal("produced-image", after.Slots.Single(s => s.SlotIndex == 0).ImageId);
        Assert.Equal(JobSlotState.Cancelled, after.Slots.Single(s => s.SlotIndex == 1).State);
        Assert.Equal("cancelled", after.Slots.Single(s => s.SlotIndex == 1).Error);
    }

    /// <summary>Failing is idempotent and never re-finalizes a job that already resolved on its own.</summary>
    [Fact]
    public async Task Fail_leaves_an_already_finalized_job_alone()
    {
        User user = await fixture.NewUserAsync("job-fail-done");
        string jobId = Guid.NewGuid().ToString("N");
        JobRecord job = Job(user.Id, jobId, slots: [Slot(jobId, 0, JobSlotState.Done, imageId: "img")]);
        job.Status = JobStatus.Done;
        job.FinishedAtUtc = DateTime.UtcNow;
        await fixture.Jobs.UpsertAsync(job, Ct);

        await fixture.Jobs.FailAsync(jobId, "could not be resumed after restart", Ct);

        JobRecord? after = await fixture.Jobs.GetAsync(jobId, Ct);
        Assert.NotNull(after);
        Assert.Equal(JobStatus.Done, after.Status);
        Assert.Equal(JobSlotState.Done, after.Slots.Single().State);
    }

    /// <summary>An active job is only visible to the instance that owns it — the rehydrate query another box runs must
    /// not return it (invariant #4: instances share data, never live job control).</summary>
    [Fact]
    public async Task Active_jobs_are_listed_only_for_the_owning_machine()
    {
        User user = await fixture.NewUserAsync("job-owner");
        string jobId = Guid.NewGuid().ToString("N");
        await fixture.Jobs.UpsertAsync(
            Job(user.Id, jobId, machine: "BOX-A", slots: [Slot(jobId, 0, JobSlotState.Queued)]), Ct);

        IReadOnlyList<JobRecord> mine = await fixture.Jobs.ListActiveForMachineAsync("BOX-A", Ct);
        IReadOnlyList<JobRecord> theirs = await fixture.Jobs.ListActiveForMachineAsync("BOX-B", Ct);

        Assert.Contains(mine, j => j.JobId == jobId);
        Assert.DoesNotContain(theirs, j => j.JobId == jobId);
    }

    /// <summary>
    /// The number that sizes the Recent strip: how many images the user's NEWEST job produced. Produced, not Total —
    /// a job that made 3 of 5 must count 3, or the strip pads out with images from before that batch started.
    /// </summary>
    [Fact]
    public async Task The_latest_batch_counts_the_images_it_actually_produced()
    {
        User user = await fixture.NewUserAsync("job-latest-batch");
        string older = Guid.NewGuid().ToString("N");
        string newest = Guid.NewGuid().ToString("N");
        DateTime noon = new DateTime(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);

        JobRecord olderJob = Job(user.Id, older, slots:
            [Slot(older, 0, JobSlotState.Done, imageId: "old-1"), Slot(older, 1, JobSlotState.Done, imageId: "old-2")]);
        olderJob.CreatedAtUtc = noon;
        JobRecord newestJob = Job(user.Id, newest, slots:
        [
            Slot(newest, 0, JobSlotState.Done, imageId: "new-1"),
            Slot(newest, 1, JobSlotState.Done, imageId: "new-2"),
            Slot(newest, 2, JobSlotState.Done, imageId: "new-3"),
            Slot(newest, 3, JobSlotState.Error),     // produced nothing
            Slot(newest, 4, JobSlotState.Queued),    // not yet
        ]);
        newestJob.CreatedAtUtc = noon.AddMinutes(1);

        await fixture.Jobs.UpsertAsync(olderJob, Ct);
        await fixture.Jobs.UpsertAsync(newestJob, Ct);

        Assert.Equal(3, await fixture.Jobs.CountLatestBatchImagesAsync(user.Id, Ct));
    }

    /// <summary>Another user's batch never sizes this user's strip, and a user who has never generated counts zero.</summary>
    [Fact]
    public async Task The_latest_batch_is_scoped_to_the_user()
    {
        User alice = await fixture.NewUserAsync("job-latest-alice");
        User bob = await fixture.NewUserAsync("job-latest-bob");
        string jobId = Guid.NewGuid().ToString("N");
        await fixture.Jobs.UpsertAsync(
            Job(alice.Id, jobId, slots: [Slot(jobId, 0, JobSlotState.Done, imageId: "a-1")]), Ct);

        Assert.Equal(1, await fixture.Jobs.CountLatestBatchImagesAsync(alice.Id, Ct));
        Assert.Equal(0, await fixture.Jobs.CountLatestBatchImagesAsync(bob.Id, Ct));
    }

    /// <summary>
    /// The sweep drops slots whose image the user deleted, and takes the job row once nothing is left. A slot that
    /// still has its blob, and a slot that never produced one, must both survive — dropping either would erase a real
    /// generation from the user's job view.
    /// </summary>
    [Fact]
    public async Task Sweep_drops_only_slots_whose_image_is_gone()
    {
        User user = await fixture.NewUserAsync("job-sweep");
        string jobId = Guid.NewGuid().ToString("N");
        string liveImage = await fixture.Blobs.AddAsync(
            new NewImageBlob([1, 2, 3, 4], "image/png", 64, 64, ImageBlobKind.Generated), Ct);

        await fixture.Jobs.UpsertAsync(Job(user.Id, jobId, slots:
        [
            Slot(jobId, 0, JobSlotState.Done, imageId: liveImage),        // blob present  -> keep
            Slot(jobId, 1, JobSlotState.Done, imageId: "deleted-image"),  // blob gone     -> drop
            Slot(jobId, 2, JobSlotState.Error),                           // never made one -> keep
        ]), Ct);

        await fixture.Jobs.SweepDeletedImageSlotsAsync(jobId, Ct);

        JobRecord? after = await fixture.Jobs.GetAsync(jobId, Ct);
        Assert.NotNull(after);
        Assert.Equal([0, 2], after.Slots.Select(s => s.SlotIndex).OrderBy(i => i).ToArray());
    }

    /// <summary>When the sweep removes the last slot, the job row goes with it rather than lingering as an empty shell.</summary>
    [Fact]
    public async Task Sweep_takes_the_job_row_once_its_last_slot_is_gone()
    {
        User user = await fixture.NewUserAsync("job-sweep-empty");
        string jobId = Guid.NewGuid().ToString("N");
        await fixture.Jobs.UpsertAsync(Job(user.Id, jobId, slots:
        [
            Slot(jobId, 0, JobSlotState.Done, imageId: "deleted-a"),
            Slot(jobId, 1, JobSlotState.Done, imageId: "deleted-b"),
        ]), Ct);

        await fixture.Jobs.SweepDeletedImageSlotsAsync(jobId, Ct);

        Assert.Null(await fixture.Jobs.GetAsync(jobId, Ct));
    }

    /// <summary>A job whose images are all still stored is left completely alone — the sweep is not a no-op test only
    /// in the sense that it must also not fire.</summary>
    [Fact]
    public async Task Sweep_leaves_an_intact_job_untouched()
    {
        User user = await fixture.NewUserAsync("job-sweep-intact");
        string jobId = Guid.NewGuid().ToString("N");
        string a = await fixture.Blobs.AddAsync(new NewImageBlob([1], "image/png", 8, 8, ImageBlobKind.Generated), Ct);
        string b = await fixture.Blobs.AddAsync(new NewImageBlob([2], "image/png", 8, 8, ImageBlobKind.Generated), Ct);

        await fixture.Jobs.UpsertAsync(Job(user.Id, jobId, slots:
        [
            Slot(jobId, 0, JobSlotState.Done, imageId: a),
            Slot(jobId, 1, JobSlotState.Done, imageId: b),
        ]), Ct);

        await fixture.Jobs.SweepDeletedImageSlotsAsync(jobId, Ct);

        JobRecord? after = await fixture.Jobs.GetAsync(jobId, Ct);
        Assert.NotNull(after);
        Assert.Equal(2, after.Slots.Count);
    }

    /// <summary>A slot's background (idle-time) flag survives the write-through and comes back per slot, so a job
    /// resumed after a restart re-gates its background work on the idle delay instead of jumping the foreground line.</summary>
    [Fact]
    public async Task Background_flag_round_trips_per_slot()
    {
        User user = await fixture.NewUserAsync("job-bg");
        string jobId = Guid.NewGuid().ToString("N");
        await fixture.Jobs.UpsertAsync(Job(user.Id, jobId, slots:
        [
            new JobSlotRecord { JobId = jobId, SlotIndex = 0, State = JobSlotState.Queued, Workflow = "test-workflow", IsBackground = true },
            new JobSlotRecord { JobId = jobId, SlotIndex = 1, State = JobSlotState.Queued, Workflow = "test-workflow", IsBackground = false },
        ]), Ct);

        JobRecord? after = await fixture.Jobs.GetAsync(jobId, Ct);
        Assert.NotNull(after);
        Assert.True(after.Slots.Single(s => s.SlotIndex == 0).IsBackground);
        Assert.False(after.Slots.Single(s => s.SlotIndex == 1).IsBackground);
    }

    private static JobRecord Job(long userId, string jobId, string machine = "BOX-A", List<JobSlotRecord>? slots = null) => new()
    {
        JobId = jobId,
        UserId = userId,
        MachineName = machine,
        Model = "sdxl",
        Prompt = "a prompt",
        Total = slots?.Count ?? 0,
        CreatedAtUtc = DateTime.UtcNow,
        Slots = slots ?? [],
    };

    private static JobSlotRecord Slot(string jobId, int index, JobSlotState state, string? imageId = null) => new()
    {
        JobId = jobId,
        SlotIndex = index,
        State = state,
        ImageId = imageId,
        Workflow = "test-workflow",
    };
}
