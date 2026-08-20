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

public sealed class RenderRehydrateCancellationTests
{
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
}
