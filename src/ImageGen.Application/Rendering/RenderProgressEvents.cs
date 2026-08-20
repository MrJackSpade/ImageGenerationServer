using System.Text;
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
    /// <summary>Subscribe to frames owned by <paramref name="owner"/> plus general backend status messages.</summary>
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

    private sealed record Subscriber(long Owner, Channel<RenderProgressFrame> Channel);

    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, Subscriber> _subscribers = [];

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

        RenderProgressRoute? route = routes.ResolveProgressRoute(comfyPromptId);
        if (route is not { } r)
        {
            return; // unknown prompt: fail closed rather than expose another process/client's event
        }

        string translated = string.Equals(comfyPromptId, r.JobId, StringComparison.Ordinal)
            ? text
            : text.Replace(comfyPromptId, r.JobId, StringComparison.Ordinal);
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

        Publish(new RenderProgressFrame(bytes.ToArray(), Binary: true), route.Owner);
    }

    private void Publish(RenderProgressFrame frame, long? owner)
    {
        lock (_lock)
        {
            foreach (Subscriber subscriber in _subscribers.Values)
            {
                if (owner is null || subscriber.Owner == owner.Value)
                {
                    _ = subscriber.Channel.Writer.TryWrite(frame);
                }
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
