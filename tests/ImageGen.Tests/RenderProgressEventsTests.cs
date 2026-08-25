using System.Text;
using ImageGen.Application.Rendering;

namespace ImageGen.Tests;

/// <summary>The process-wide ComfyUI socket is also a privacy boundary: text and binary previews must reach only the
/// job owner, unknown prompt ids must fail closed, and general status frames retain their harmless broadcast shape.</summary>
public sealed class RenderProgressEventsTests
{
    private sealed class Routes(params (string ComfyId, long Owner, string JobId)[] entries) : IRenderProgressRouteResolver
    {
        private readonly Dictionary<string, RenderProgressRoute> _routes = entries.ToDictionary(
            e => e.ComfyId, e => new RenderProgressRoute(e.Owner, e.JobId), StringComparer.Ordinal);

        public RenderProgressRoute? ResolveProgressRoute(string comfyPromptId) =>
            _routes.TryGetValue(comfyPromptId, out RenderProgressRoute route) ? route : null;

        public void Remove(string comfyPromptId) => _ = _routes.Remove(comfyPromptId);
    }

    private static string TextOf(RenderProgressFrame frame) => Encoding.UTF8.GetString(frame.Bytes);

    [Fact]
    public void Prompt_text_is_translated_and_delivered_only_to_its_owner()
    {
        RenderProgressEvents events = new(new Routes(("comfy-1", 7, "job-42")));
        using RenderProgressSubscription mine = events.Subscribe(7);
        using RenderProgressSubscription theirs = events.Subscribe(8);

        events.PublishText("{\"type\":\"progress\",\"data\":{\"prompt_id\":\"comfy-1\"}}", "comfy-1");

        Assert.True(mine.Reader.TryRead(out RenderProgressFrame? frame));
        Assert.False(frame.Binary);
        Assert.Contains("job-42", TextOf(frame), StringComparison.Ordinal);
        Assert.DoesNotContain("comfy-1", TextOf(frame), StringComparison.Ordinal);
        Assert.False(theirs.Reader.TryRead(out _));
    }

    [Fact]
    public void Binary_preview_is_delivered_only_to_its_owner()
    {
        RenderProgressEvents events = new(new Routes(("comfy-1", 7, "job-42")));
        using RenderProgressSubscription mine = events.Subscribe(7);
        using RenderProgressSubscription theirs = events.Subscribe(8);
        byte[] preview = [0, 0, 0, 1, 0, 0, 0, 1, 0xff, 0xd8, 0xff];

        events.PublishBinary(preview, "comfy-1");

        Assert.True(mine.Reader.TryRead(out RenderProgressFrame? frame));
        Assert.True(frame.Binary);
        Assert.Equal(preview, frame.Bytes);
        Assert.False(theirs.Reader.TryRead(out _));
    }

    [Fact]
    public void Page_subscriber_recovers_the_latest_active_preview_with_its_job_context()
    {
        RenderProgressEvents events = new(new Routes(("comfy-1", 7, "job-42")));
        byte[] preview = [0, 0, 0, 1, 0, 0, 0, 2, 0x89, 0x50, 0x4e, 0x47];

        // The preview happened before this page connected.
        events.PublishBinary(preview, "comfy-1");
        using RenderProgressSubscription recovered = events.Subscribe(7);
        using RenderProgressSubscription theirs = events.Subscribe(8);

        Assert.True(recovered.Reader.TryRead(out RenderProgressFrame? context));
        Assert.False(context.Binary);
        Assert.Contains("job-42", TextOf(context), StringComparison.Ordinal);
        Assert.True(recovered.Reader.TryRead(out RenderProgressFrame? frame));
        Assert.True(frame.Binary);
        Assert.Equal(preview, frame.Bytes);
        Assert.False(theirs.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData("execution_success")]
    [InlineData("execution_error")]
    [InlineData("execution_interrupted")]
    public void Finished_execution_does_not_replay_a_stale_preview(string terminalType)
    {
        RenderProgressEvents events = new(new Routes(("comfy-1", 7, "job-42")));
        events.PublishBinary(new byte[] { 1, 2, 3 }, "comfy-1");
        events.PublishText($"{{\"type\":\"{terminalType}\",\"data\":{{\"prompt_id\":\"comfy-1\"}}}}", "comfy-1");

        using RenderProgressSubscription recovered = events.Subscribe(7);

        Assert.False(recovered.Reader.TryRead(out _));
    }

    [Fact]
    public void Terminal_frame_retires_preview_even_when_the_job_route_was_already_removed()
    {
        Routes routes = new(("comfy-1", 7, "job-42"));
        RenderProgressEvents events = new(routes);
        events.PublishBinary(new byte[] { 1, 2, 3 }, "comfy-1");
        routes.Remove("comfy-1");

        events.PublishText("{\"type\":\"execution_success\",\"data\":{\"prompt_id\":\"comfy-1\"}}", "comfy-1");
        using RenderProgressSubscription recovered = events.Subscribe(7);

        Assert.False(recovered.Reader.TryRead(out _));
    }

    [Fact]
    public void Unknown_prompt_text_and_binary_fail_closed()
    {
        RenderProgressEvents events = new(new Routes());
        using RenderProgressSubscription subscriber = events.Subscribe(7);

        events.PublishText("{\"data\":{\"prompt_id\":\"unknown\"}}", "unknown");
        events.PublishBinary(new byte[] { 1, 2, 3 }, "unknown");

        Assert.False(subscriber.Reader.TryRead(out _));
    }

    [Fact]
    public void General_backend_status_is_broadcast_to_authenticated_subscribers()
    {
        RenderProgressEvents events = new(new Routes());
        using RenderProgressSubscription first = events.Subscribe(7);
        using RenderProgressSubscription second = events.Subscribe(8);

        events.PublishText("{\"type\":\"status\"}", comfyPromptId: null);

        Assert.True(first.Reader.TryRead(out RenderProgressFrame? one));
        Assert.True(second.Reader.TryRead(out RenderProgressFrame? two));
        Assert.Equal(TextOf(one), TextOf(two));
    }

    [Fact]
    public void Disposing_a_subscription_completes_and_removes_it()
    {
        RenderProgressEvents events = new(new Routes());
        RenderProgressSubscription subscription = events.Subscribe(7);

        subscription.Dispose();
        events.PublishText("status", comfyPromptId: null);

        Assert.True(subscription.Reader.Completion.IsCompleted);
        Assert.False(subscription.Reader.TryRead(out _));
    }
}
