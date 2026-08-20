using ImageGen.Application.Rendering;
using ImageGen.Comfy;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace ImageGen.Tests;

/// <summary>Renderer HTTP outcomes must preserve the difference between "not there" and "could not answer". That
/// distinction keeps already-accepted work alive through a ComfyUI restart.</summary>
public sealed class ComfyReachabilityTests
{
    [Fact]
    public async Task A_history_http_error_is_logged_and_is_not_ordinary_not_ready()
    {
        CapturingLogger log = new();
        ComfyClient client = Client(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)), log);

        RenderPollResult result = await client.PollResultAsync("prompt-1", CancellationToken.None);

        Assert.Equal(RenderPollState.Unavailable, result.State);
        Assert.Equal(1, log.Warnings);
    }

    [Fact]
    public async Task A_history_transport_failure_keeps_the_poll_retryable()
    {
        CapturingLogger log = new();
        ComfyClient client = Client((_, _) => throw new HttpRequestException("renderer restarting"), log);

        RenderPollResult result = await client.PollResultAsync("prompt-1", CancellationToken.None);

        Assert.Equal(RenderPollState.Unavailable, result.State);
        Assert.Equal(1, log.Warnings);
    }

    [Fact]
    public async Task Missing_history_and_backend_execution_failure_remain_distinct()
    {
        ComfyClient missing = Client(Json("{}"));
        RenderPollResult pending = await missing.PollResultAsync("prompt-1", CancellationToken.None);
        Assert.Equal(RenderPollState.NotReady, pending.State);

        const string failed = """
            {"prompt-1":{"status":{"status_str":"error","messages":[["execution_error",{
              "node_type":"KSampler","node_id":"3","exception_type":"ValueError","exception_message":"bad input"
            }]]}}}
            """;
        ComfyClient backendFailure = Client(Json(failed));

        RenderValidationException ex = await Assert.ThrowsAsync<RenderValidationException>(
            () => backendFailure.PollResultAsync("prompt-1", CancellationToken.None));
        Assert.Contains("ValueError", ex.Message);
        Assert.Contains("bad input", ex.Message);
    }

    [Fact]
    public async Task Ready_history_still_downloads_and_returns_the_output()
    {
        byte[] expected = [1, 2, 3, 4];
        const string history = """
            {"prompt-1":{"outputs":{"9":{"images":[{
              "filename":"done.png","subfolder":"","type":"output"
            }]}}}}
            """;
        ComfyClient client = Client((request, _) => Task.FromResult(
            request.RequestUri?.AbsolutePath.StartsWith("/history/", StringComparison.Ordinal) == true
                ? Json(history)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(expected) }));

        RenderPollResult result = await client.PollResultAsync("prompt-1", CancellationToken.None);

        Assert.Equal(RenderPollState.Ready, result.State);
        Assert.Equal(expected, result.Image?.Png);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    public async Task Only_definitive_legacy_absence_is_reported_missing(HttpStatusCode status)
    {
        ComfyClient client = Client((_, _) => Task.FromResult(new HttpResponseMessage(status)));

        LegacyImageFetchResult result = await client.FetchLegacyImageAsync("old.png", CancellationToken.None);

        Assert.Equal(LegacyImageFetchState.NotFound, result.State);
    }

    [Fact]
    public async Task Legacy_transport_and_nonmissing_http_failures_are_retryable()
    {
        CapturingLogger transportLog = new();
        ComfyClient transport = Client((_, _) => throw new TaskCanceledException("socket timeout"), transportLog);
        LegacyImageFetchResult timedOut = await transport.FetchLegacyImageAsync("old.png", CancellationToken.None);

        CapturingLogger httpLog = new();
        ComfyClient http = Client(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)), httpLog);
        LegacyImageFetchResult unavailable = await http.FetchLegacyImageAsync("old.png", CancellationToken.None);

        Assert.Equal(LegacyImageFetchState.Unavailable, timedOut.State);
        Assert.Equal(LegacyImageFetchState.Unavailable, unavailable.State);
        Assert.Equal(1, transportLog.Warnings);
        Assert.Equal(1, httpLog.Warnings);
    }

    [Fact]
    public async Task A_found_legacy_image_carries_its_bytes()
    {
        byte[] bytes = [9, 8, 7];
        ComfyClient client = Client((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));

        LegacyImageFetchResult result = await client.FetchLegacyImageAsync("old.png", CancellationToken.None);

        Assert.Equal(LegacyImageFetchState.Found, result.State);
        Assert.Equal(bytes, result.Bytes);
    }

    private static ComfyClient Client(HttpResponseMessage response) =>
        Client((_, _) => Task.FromResult(response));

    private static ComfyClient Client(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        CapturingLogger? logger = null)
    {
        HttpClient http = new(new DelegateHandler(send));
        return new ComfyClient(
            new FixedFactory(http),
            new FixedEndpoint(),
            Uninitialized<WorkflowCatalog>(),
            Uninitialized<WorkflowRegistry>(),
            Proxy<ImageGen.Application.Media.IMediaProcessor>(),
            Proxy<ImageGen.Application.Snapshots.ISnapshot<ImageGen.Comfy.Snapshots.ComfyFilesByKind>>(),
            logger ?? new CapturingLogger());
    }

    private static T Uninitialized<T>() where T : class =>
        (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    private static T Proxy<T>() where T : class => DispatchProxy.Create<T, ThrowingProxy>();

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            send(request, ct);
    }

    private sealed class FixedFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class FixedEndpoint : IComfyEndpoint
    {
        public string BaseUrl => "http://comfy.test";
        public string GateToken => "test-token";
    }

    public class ThrowingProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException($"Unexpected test call to {targetMethod?.Name}.");
    }

    private sealed class CapturingLogger : ILogger<ComfyClient>
    {
        public int Warnings { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings++;
            }
        }
    }
}
