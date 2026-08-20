using ImageGen.Application.Images;
using ImageGen.Application.Media;
using ImageGen.Application.Platform;
using ImageGen.Application.Prompting.Tags;
using ImageGen.Application.Rendering;
using ImageGen.Application.Snapshots;
using ImageGen.Application.Tags;
using ImageGen.Application.Workflows;
using ImageGen.Domain.Logging;
using ImageGen.Domain.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;

namespace ImageGen.Tests;

public sealed class RenderInterruptAsyncTests
{
    [Fact]
    public async Task Running_cancel_awaits_interrupt_without_blocking_the_caller_thread()
    {
        IComfyClient comfy = DispatchProxy.Create<IComfyClient, InterruptProxy>();
        InterruptProxy interrupt = (InterruptProxy)(object)comfy;
        RenderOrchestrator queue = Queue(comfy);
        RenderJob job = new()
        {
            JobId = "job",
            Owner = 7,
            MachineName = Environment.MachineName,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        RenderSlot slot = new()
        {
            Job = job,
            Index = 0,
            Gen = new GenerateSpec("model", "prompt", null, null),
            ComfyPromptId = "upstream-prompt",
        };
        job.Slots.Add(slot);
        FieldInfo running = typeof(RenderOrchestrator).GetField("_running", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(RenderOrchestrator).FullName, "_running");
        running.SetValue(queue, slot);

        Task<bool> cancel = queue.CancelRunningAsync();

        Assert.True(slot.CancelRequested);
        Assert.False(cancel.IsCompleted);
        Assert.Equal(1, interrupt.Calls);

        interrupt.Complete();
        Assert.True(await cancel.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static RenderOrchestrator Queue(IComfyClient comfy)
    {
        IUploadStore uploads = Proxy<IUploadStore>();
        return new RenderOrchestrator(
            comfy, Proxy<IWorkflowCatalog>(), Proxy<ITagModelClient>(), Proxy<ITagCatalog>(),
            Proxy<IMediaProcessor>(), Proxy<IJobRepository>(), uploads,
            new ImageVisibilityService(uploads, Proxy<IImageVisibilityRepository>()),
            Proxy<IImageBlobRepository>(), Proxy<IImageFrameRepository>(), Proxy<IGenTimingRepository>(),
            Proxy<ISnapshot<GenTimingAverages>>(), Proxy<IUserLogService>(), new AvailableDatabase(),
            new RenderOptions(() => TimeSpan.Zero), Proxy<IServiceScopeFactory>(),
            NullLogger<RenderOrchestrator>.Instance);
    }

    private static T Proxy<T>() where T : class => DispatchProxy.Create<T, ThrowingProxy>();

    public class ThrowingProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException($"Unexpected test call to {targetMethod?.Name}.");
    }

    public class InterruptProxy : DispatchProxy
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public void Complete() => _completion.SetResult();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IComfyClient.InterruptAsync))
            {
                Calls++;
                return _completion.Task;
            }

            throw new NotSupportedException($"Unexpected test call to {targetMethod?.Name}.");
        }
    }

    private sealed class AvailableDatabase : IDatabaseAvailability
    {
        public bool IsUnavailable(Exception ex) => false;
    }
}
