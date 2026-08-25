using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace ImageGen.Application.Rendering;

/// <summary>The application job behind one backend prompt id. This is the routing fact the progress stream needs to
/// translate ComfyUI ids and enforce the same owner boundary as the job/image APIs.</summary>
public readonly record struct RenderProgressRoute(long Owner, string JobId);

/// <summary>Resolves an ephemeral ComfyUI prompt id to the application job that owns it.</summary>
public interface IRenderProgressRouteResolver
{
    /// <summary>The route for <paramref name="comfyPromptId"/>, or null when this process did not submit it.</summary>
    RenderProgressRoute? ResolveProgressRoute(string comfyPromptId);
}

/// <summary>One complete downstream WebSocket message. Binary frames are ComfyUI preview-image payloads; text frames
/// are its JSON events with any backend prompt id translated to an application job id.</summary>
public sealed record RenderProgressFrame(byte[] Bytes, bool Binary);

/// <summary>An owner-filtered subscription to the renderer's event stream.</summary>
public sealed class RenderProgressSubscription : IDisposable
{
    private Action? _unsubscribe;

    internal RenderProgressSubscription(ChannelReader<RenderProgressFrame> reader, Action unsubscribe)
    {
        Reader = reader;
        _unsubscribe = unsubscribe;
    }

    /// <summary>The frames routed to this owner. The reader completes when the subscription is disposed.</summary>
    public ChannelReader<RenderProgressFrame> Reader { get; }

    /// <inheritdoc />
    public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
}

/// <summary>The renderer adapter's write side of the one process-wide progress stream.</summary>
public interface IRenderProgressPublisher
{
    /// <summary>Publish one complete ComfyUI text message. <paramref name="comfyPromptId"/> is null for general backend
    /// status messages, which retain the existing broadcast-to-all-authenticated-clients behavior.</summary>
    void PublishText(string text, string? comfyPromptId);

    /// <summary>Publish one complete binary preview message associated with the prompt whose progress immediately
    /// preceded it. Unattributable frames are withheld.</summary>
    void PublishBinary(ReadOnlyMemory<byte> bytes, string? comfyPromptId);
}

/// <summary>The API's read side of the one process-wide progress stream.</summary>
public interface IRenderProgressStream
{
    /// <summary>Subscribe to frames owned by <paramref name="owner"/> plus general backend status messages. When that
    /// owner has an active preview, the subscription begins with its job context and latest frame so a page request
    /// recovers immediately instead of waiting for the renderer's next preview interval.</summary>
    RenderProgressSubscription Subscribe(long owner);
}

/// <summary>
/// In-process fan-out between the sole ComfyUI progress connection and any number of browser WebSockets. Keeping the
/// upstream connection singular is required by ComfyUI: reconnecting the same client id replaces its prior socket.
/// </summary>
internal sealed class RenderProgressEvents(IRenderProgressRouteResolver routes)
    : IRenderProgressPublisher, IRenderProgressStream
{
    /// <summary>Enough room for event bursts while bounding a stalled tab. Dropping the oldest frame is deliberate:
    /// progress and previews are snapshots, and every completion path also has the authoritative /jobs poll.</summary>
    private const int SubscriberCapacity = 32;

    /// <summary>Comfy event tokens used to retire a recovered preview, plus this gateway's replay context token.</summary>
    private static class EventTypes
    {
        public const string Member = "type";
        public const string PreviewReplay = "preview_replay";
        public const string Success = "execution_success";
        public const string Error = "execution_error";
        public const string Interrupted = "execution_interrupted";
    }

    private sealed record Subscriber(long Owner, Channel<RenderProgressFrame> Channel);
    private sealed record CachedPreview(
        string ComfyPromptId, string JobId, RenderProgressFrame Context, RenderProgressFrame Preview);

    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, Subscriber> _subscribers = [];
    /// <summary>One renderer prompt can be active per owner on this backend. Only its latest snapshot is retained:
    /// APNGs can be large, and recovery needs the newest denoising state, never every emitted step.</summary>
    private readonly Dictionary<long, CachedPreview> _latestPreviews = [];

    /// <inheritdoc />
    public RenderProgressSubscription Subscribe(long owner)
    {
        Guid id = Guid.NewGuid();
        Channel<RenderProgressFrame> channel = Channel.CreateBounded<RenderProgressFrame>(new BoundedChannelOptions(SubscriberCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false,
        });
        lock (_lock)
        {
            _subscribers.Add(id, new Subscriber(owner, channel));
            if (_latestPreviews.TryGetValue(owner, out CachedPreview? cached))
            {
                // A binary Comfy preview carries no prompt/job id. The browser associates it with the most recent
                // prompt-bearing text frame, so replay the pair in order or page recovery will discard the image.
                _ = channel.Writer.TryWrite(cached.Context);
                _ = channel.Writer.TryWrite(cached.Preview);
            }
        }

        return new RenderProgressSubscription(channel.Reader, () => Remove(id));
    }

    /// <inheritdoc />
    public void PublishText(string text, string? comfyPromptId)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrEmpty(comfyPromptId))
        {
            Publish(new RenderProgressFrame(Encoding.UTF8.GetBytes(text), Binary: false), owner: null);
            return;
        }

        bool terminal = EndsExecution(text);
        RenderProgressRoute? route = routes.ResolveProgressRoute(comfyPromptId);
        if (route is not { } r)
        {
            // The orchestrator may retire its prompt route before this listener consumes ComfyUI's terminal frame.
            // The cache still knows the backend id, so it can release the APNG without needing that route.
            if (terminal)
            {
                RetirePreview(comfyPromptId);
            }

            return; // unknown prompt: fail closed rather than expose another process/client's event
        }

        string translated = string.Equals(comfyPromptId, r.JobId, StringComparison.Ordinal)
            ? text
            : text.Replace(comfyPromptId, r.JobId, StringComparison.Ordinal);
        if (terminal)
        {
            RetirePreview(comfyPromptId);
        }

        Publish(new RenderProgressFrame(Encoding.UTF8.GetBytes(translated), Binary: false), r.Owner);
    }

    /// <inheritdoc />
    public void PublishBinary(ReadOnlyMemory<byte> bytes, string? comfyPromptId)
    {
        if (bytes.IsEmpty || string.IsNullOrEmpty(comfyPromptId)
            || routes.ResolveProgressRoute(comfyPromptId) is not { } route)
        {
            return;
        }

        RenderProgressFrame preview = new(bytes.ToArray(), Binary: true);
        RenderProgressFrame context = new(
            JsonSerializer.SerializeToUtf8Bytes(new { type = EventTypes.PreviewReplay, data = new { prompt_id = route.JobId } }),
            Binary: false);
        lock (_lock)
        {
            _latestPreviews[route.Owner] = new CachedPreview(comfyPromptId, route.JobId, context, preview);
            PublishLocked(preview, route.Owner);
        }
    }

    private void Publish(RenderProgressFrame frame, long? owner)
    {
        lock (_lock)
        {
            PublishLocked(frame, owner);
        }
    }

    /// <summary>Fan out one frame while <see cref="_lock"/> is held.</summary>
    private void PublishLocked(RenderProgressFrame frame, long? owner)
    {
        foreach (Subscriber subscriber in _subscribers.Values)
        {
            if (owner is null || subscriber.Owner == owner.Value)
            {
                _ = subscriber.Channel.Writer.TryWrite(frame);
            }
        }
    }

    /// <summary>Terminal Comfy events retire the replay snapshot immediately so finished jobs cannot hold APNG bytes
    /// or flash a stale preview on a later page request.</summary>
    private static bool EndsExecution(string text)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty(EventTypes.Member, out JsonElement type) || type.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            return type.ValueEquals(EventTypes.Success)
                || type.ValueEquals(EventTypes.Error)
                || type.ValueEquals(EventTypes.Interrupted);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Forget the snapshot for one completed backend prompt. Matching the backend id works even after the
    /// orchestrator has already retired the route that translated it.</summary>
    private void RetirePreview(string comfyPromptId)
    {
        lock (_lock)
        {
            long owner = 0;
            bool found = false;
            foreach ((long candidate, CachedPreview cached) in _latestPreviews)
            {
                if (string.Equals(cached.ComfyPromptId, comfyPromptId, StringComparison.Ordinal))
                {
                    owner = candidate;
                    found = true;
                    break;
                }
            }

            if (found)
            {
                _ = _latestPreviews.Remove(owner);
            }
        }
    }

    private void Remove(Guid id)
    {
        Channel<RenderProgressFrame>? channel = null;
        lock (_lock)
        {
            if (_subscribers.Remove(id, out Subscriber? subscriber))
            {
                channel = subscriber.Channel;
            }
        }

        _ = channel?.Writer.TryComplete();
    }
}
