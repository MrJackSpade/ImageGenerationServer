using ImageGen.Application.Images;
using ImageGen.Application.Media;
using ImageGen.Application.Platform;
using ImageGen.Application.Prompting.Tags;
using ImageGen.Application.Rendering;
using ImageGen.Application.Tags;
using ImageGen.Application.Workflows;
using ImageGen.Domain;
using ImageGen.Domain.Entities;
using ImageGen.Domain.Logging;
using ImageGen.Domain.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;

namespace ImageGen.Tests;

[Collection("db")]
public sealed class RenderRehydrateCancellationTests(TestDatabaseFixture fixture)
{
    [Fact]
    public async Task Enqueue_refuses_an_empty_item_list_before_touching_collaborators()
    {
        RenderOrchestrator queue = Queue(Proxy<IJobRepository>());

        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(
            () => queue.EnqueueJobAsync(owner: 7, items: []));

        Assert.Equal("items", ex.ParamName);
    }

    [Fact]
    public async Task A_stranded_cancel_cannot_be_republished_from_a_stale_rehydrate_list()
    {
        PausingJobRepository jobs = new();
        RenderOrchestrator queue = Queue(jobs);

        Task<bool> cancel = queue.CancelStrandedAsync(jobs.JobId, CancellationToken.None);
        await jobs.FirstGetEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The startup scan observes Active while cancellation owns the mutation gate, reproducing the stale-list race.
        Task<bool> rehydrate = queue.RehydrateAsync(CancellationToken.None);
        _ = jobs.ReleaseFirstGet.TrySetResult();

        Assert.True(await cancel.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await rehydrate.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(queue.AllActive());
        Assert.Equal(JobStatus.Cancelled, jobs.Status);
        Assert.Equal(2, jobs.GetCalls); // cancellation read + rehydrate's required fresh read inside the gate
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public async Task Corrupt_rehydrated_value_bag_fails_only_its_slot_and_preserves_valid_slots(int corruptIndex, bool edit)
    {
        User user = await fixture.NewUserAsync($"rehydrate-corrupt-{corruptIndex}-{edit}");
        string jobId = Guid.NewGuid().ToString("N");
        DateTime oldStart = DateTime.UtcNow.AddHours(-4);
        JobSlotRecord Slot(int index, JobSlotState state, string overridesJson, string? promptId = null,
            DateTime? startedAtUtc = null) => edit
                ? EditSlot(jobId, index, state, overridesJson, promptId, startedAtUtc)
                : GenerateSlot(jobId, index, state, overridesJson, promptId, startedAtUtc);
        JobSlotRecord[] slots =
        [
            Slot(0, corruptIndex == 0 ? JobSlotState.Queued : JobSlotState.Done,
                overridesJson: corruptIndex == 0 ? "{" : "{}"),
            Slot(1, corruptIndex == 1 ? JobSlotState.Queued : JobSlotState.Done,
                overridesJson: corruptIndex == 1 ? "{" : "{}"),
            Slot(2, JobSlotState.Queued, overridesJson: "{}", promptId: "resume-me", startedAtUtc: oldStart),
        ];
        await fixture.Jobs.UpsertAsync(ActiveJob(user.Id, jobId, slots), CancellationToken.None);

        RenderOrchestrator queue = Queue(fixture.Jobs);
        Assert.True(await queue.RehydrateAsync(CancellationToken.None));

        RenderJob live = Assert.IsType<RenderJob>(queue.Get(jobId));
        Assert.Equal("test-workflow", live.Model); // corrupt slot zero must remain safe for job-level display
        Assert.Equal("prompt-0", live.Prompt);
        Assert.Equal(SlotState.Error, live.Slots.Single(s => s.Index == corruptIndex).State);
        Assert.Contains("unreadable stored request", live.Slots.Single(s => s.Index == corruptIndex).Error);
        Assert.Equal(SlotState.Done, live.Slots.Single(s => s.Index == (corruptIndex == 0 ? 1 : 0)).State);
        RenderSlot resumed = live.Slots.Single(s => s.Index == 2);
        Assert.Equal(SlotState.Queued, resumed.State);
        Assert.Equal("resume-me", resumed.ComfyPromptId);
        Assert.Null(resumed.GenStartedAt); // never include the four-hour restart gap in ETA/timing

        JobRecord durable = Assert.IsType<JobRecord>(await fixture.Jobs.GetAsync(jobId, CancellationToken.None));
        Assert.Equal(JobStatus.Active, durable.Status);
        Assert.Equal(JobSlotState.Error, durable.Slots.Single(s => s.SlotIndex == corruptIndex).State);
        Assert.Equal(JobSlotState.Done, durable.Slots.Single(s => s.SlotIndex == (corruptIndex == 0 ? 1 : 0)).State);
        Assert.Equal(JobSlotState.Queued, durable.Slots.Single(s => s.SlotIndex == 2).State);
    }

    [Fact]
    public async Task Transient_final_persist_rejection_keeps_terminal_result_visible_until_retry_succeeds()
    {
        (string jobId, _) = await SeedTerminalActiveJobAsync("finalize-transient");
        RejectingJobRepository jobs = new(fixture.Jobs, terminalRejectCount: 1);
        RenderOrchestrator queue = Queue(jobs);

        Assert.True(await queue.RehydrateAsync(CancellationToken.None));
        RenderJob pending = Assert.IsType<RenderJob>(queue.Get(jobId));
        Assert.True(pending.AllTerminal);
        _ = Assert.NotNull(pending.FinishedAt);

        await EventuallyAsync(() => queue.Get(jobId) is null, TimeSpan.FromSeconds(5));
        JobRecord durable = Assert.IsType<JobRecord>(await fixture.Jobs.GetAsync(jobId, CancellationToken.None));
        Assert.Equal(JobStatus.Done, durable.Status);
        _ = Assert.NotNull(durable.FinishedAtUtc);
        Assert.Equal(2, jobs.TerminalAttempts);
    }

    [Fact]
    public async Task Persistent_final_persist_rejection_has_a_retry_driver_and_retains_terminal_visibility()
    {
        (string jobId, _) = await SeedTerminalActiveJobAsync("finalize-persistent");
        RejectingJobRepository jobs = new(fixture.Jobs, terminalRejectCount: int.MaxValue);
        RenderOrchestrator queue = Queue(jobs);

        Assert.True(await queue.RehydrateAsync(CancellationToken.None));
        await EventuallyAsync(() => jobs.TerminalAttempts >= 2, TimeSpan.FromSeconds(5));

        RenderJob pending = Assert.IsType<RenderJob>(queue.Get(jobId));
        Assert.True(pending.AllTerminal);
        _ = Assert.NotNull(pending.FinishedAt);
        JobRecord stillActive = Assert.IsType<JobRecord>(await fixture.Jobs.GetAsync(jobId, CancellationToken.None));
        Assert.Equal(JobStatus.Active, stillActive.Status);

        jobs.AllowTerminalWrites();
        await EventuallyAsync(() => queue.Get(jobId) is null, TimeSpan.FromSeconds(8));
        JobRecord durable = Assert.IsType<JobRecord>(await fixture.Jobs.GetAsync(jobId, CancellationToken.None));
        Assert.Equal(JobStatus.Done, durable.Status);
        _ = Assert.NotNull(durable.FinishedAtUtc);
    }

    [Fact]
    public void A_missing_local_start_time_produces_no_timing_sample()
    {
        Assert.Null(RenderOrchestrator.CompletedTimingMilliseconds(null, DateTimeOffset.UtcNow));
        Assert.Equal(2500, RenderOrchestrator.CompletedTimingMilliseconds(
            new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 20, 12, 0, 2, 500, TimeSpan.Zero)));
    }

    private async Task<(string JobId, long UserId)> SeedTerminalActiveJobAsync(string tag)
    {
        User user = await fixture.NewUserAsync(tag);
        string jobId = Guid.NewGuid().ToString("N");
        string imageId = await fixture.Blobs.AddAsync(
            new NewImageBlob([1, 2, 3, 4], "image/png", 16, 16, ImageBlobKind.Generated), CancellationToken.None);
        JobSlotRecord done = GenerateSlot(jobId, 0, JobSlotState.Done, overridesJson: "{}");
        done.ImageId = imageId;
        done.Width = 16;
        done.Height = 16;
        await fixture.Jobs.UpsertAsync(ActiveJob(user.Id, jobId,
            [done]), CancellationToken.None);
        return (jobId, user.Id);
    }

    private static JobRecord ActiveJob(long userId, string jobId, JobSlotRecord[] slots) => new()
    {
        JobId = jobId,
        UserId = userId,
        MachineName = Environment.MachineName,
        Model = "test-workflow",
        Prompt = "prompt-0",
        Total = slots.Length,
        Status = JobStatus.Active,
        CreatedAtUtc = DateTime.UtcNow,
        Slots = [.. slots],
    };

    private static JobSlotRecord GenerateSlot(string jobId, int index, JobSlotState state, string overridesJson,
        string? promptId = null, DateTime? startedAtUtc = null) => new()
    {
        JobId = jobId,
        SlotIndex = index,
        State = state,
        ComfyPromptId = promptId,
        GenStartedAtUtc = startedAtUtc,
        Workflow = "test-workflow",
        Prompt = $"prompt-{index}",
        OverridesJson = overridesJson,
        Generate = new GenerateSlotData { Aspect = "square" },
    };

    private static JobSlotRecord EditSlot(string jobId, int index, JobSlotState state, string overridesJson,
        string? promptId = null, DateTime? startedAtUtc = null) => new()
    {
        JobId = jobId,
        SlotIndex = index,
        IsEdit = true,
        State = state,
        ComfyPromptId = promptId,
        GenStartedAtUtc = startedAtUtc,
        Workflow = "test-workflow",
        Prompt = $"prompt-{index}",
        OverridesJson = overridesJson,
        Edit = new EditSlotData(),
    };

    private static async Task EventuallyAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Condition did not become true before the test deadline.");
            }

            await Task.Delay(50);
        }
    }

    private static RenderOrchestrator Queue(IJobRepository jobs)
    {
        IUploadStore uploads = Proxy<IUploadStore>();
        return new RenderOrchestrator(
            Proxy<IComfyClient>(), Proxy<IWorkflowCatalog>(), Proxy<ITagModelClient>(), Proxy<ITagCatalog>(),
            Proxy<IMediaProcessor>(), jobs, uploads,
            new ImageVisibilityService(uploads, Proxy<IImageVisibilityRepository>()),
            Proxy<IImageBlobRepository>(), Proxy<IImageFrameRepository>(), Proxy<IGenTimingRepository>(),
            Proxy<ImageGen.Application.Snapshots.ISnapshot<ImageGen.Application.Snapshots.GenTimingAverages>>(),
            Proxy<IUserLogService>(), new AvailableDatabase(), new RenderOptions(() => TimeSpan.Zero),
            Proxy<IServiceScopeFactory>(), NullLogger<RenderOrchestrator>.Instance);
    }

    private static T Proxy<T>() where T : class => DispatchProxy.Create<T, ThrowingProxy>();

    public class ThrowingProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException($"Unexpected test call to {targetMethod?.Name}.");
    }

    private sealed class AvailableDatabase : IDatabaseAvailability
    {
        public bool IsUnavailable(Exception ex) => false;
    }

    private sealed class PausingJobRepository : IJobRepository
    {
        private int _getCalls;
        public string JobId { get; } = "race-job";
        public JobStatus Status { get; private set; } = JobStatus.Active;
        public int GetCalls => Volatile.Read(ref _getCalls);
        public TaskCompletionSource FirstGetEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstGet { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<JobRecord>> ListActiveForMachineAsync(string machineName, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<JobRecord>>([Record(JobStatus.Active)]);

        public async Task<JobRecord?> GetAsync(string jobId, CancellationToken ct)
        {
            int call = Interlocked.Increment(ref _getCalls);
            if (call == 1)
            {
                _ = FirstGetEntered.TrySetResult();
                await ReleaseFirstGet.Task.WaitAsync(ct);
            }

            return Record(Status);
        }

        public Task CancelAsync(string jobId, CancellationToken ct)
        {
            Status = JobStatus.Cancelled;
            return Task.CompletedTask;
        }

        private JobRecord Record(JobStatus status) => new()
        {
            JobId = JobId,
            UserId = 7,
            MachineName = Environment.MachineName,
            Model = "test",
            Prompt = "test",
            Total = 0,
            Status = status,
            CreatedAtUtc = DateTime.UtcNow,
        };

        public Task UpsertAsync(JobRecord job, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> CountLatestBatchImagesAsync(long userId, CancellationToken ct) => throw new NotSupportedException();
        public Task<PagedResult<JobRecord>> ListPageAsync(string machineName, long viewerUserId, int page, int pageSize, CancellationToken ct) => throw new NotSupportedException();
        public Task FailAsync(string jobId, string reason, CancellationToken ct) => throw new NotSupportedException();
        public Task SweepDeletedImageSlotsAsync(string jobId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ImageRequestRecord?> GetRequestByImageAsync(string imageId, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class RejectingJobRepository(IJobRepository inner, int terminalRejectCount) : IJobRepository
    {
        private int _terminalAttempts;
        private int _allowTerminalWrites;
        public int TerminalAttempts => Volatile.Read(ref _terminalAttempts);

        public void AllowTerminalWrites() => Volatile.Write(ref _allowTerminalWrites, 1);

        public async Task UpsertAsync(JobRecord job, CancellationToken ct)
        {
            if (job.Status != JobStatus.Active)
            {
                int attempt = Interlocked.Increment(ref _terminalAttempts);
                if (Volatile.Read(ref _allowTerminalWrites) == 0 && attempt <= terminalRejectCount)
                {
                    throw new InvalidOperationException("controlled terminal write rejection");
                }
            }

            await inner.UpsertAsync(job, ct);
        }

        public Task<JobRecord?> GetAsync(string jobId, CancellationToken ct) => inner.GetAsync(jobId, ct);
        public Task<IReadOnlyList<JobRecord>> ListActiveForMachineAsync(string machineName, CancellationToken ct) =>
            inner.ListActiveForMachineAsync(machineName, ct);
        public Task<int> CountLatestBatchImagesAsync(long userId, CancellationToken ct) => inner.CountLatestBatchImagesAsync(userId, ct);
        public Task<PagedResult<JobRecord>> ListPageAsync(string machineName, long viewerUserId, int page, int pageSize, CancellationToken ct) =>
            inner.ListPageAsync(machineName, viewerUserId, page, pageSize, ct);
        public Task FailAsync(string jobId, string reason, CancellationToken ct) => inner.FailAsync(jobId, reason, ct);
        public Task CancelAsync(string jobId, CancellationToken ct) => inner.CancelAsync(jobId, ct);
        public Task SweepDeletedImageSlotsAsync(string jobId, CancellationToken ct) => inner.SweepDeletedImageSlotsAsync(jobId, ct);
        public Task<ImageRequestRecord?> GetRequestByImageAsync(string imageId, CancellationToken ct) => inner.GetRequestByImageAsync(imageId, ct);
    }
}
