namespace ImageGen.Application.Images;

/// <summary>
/// <see cref="IUploadStore"/> holding uploads in this process's memory for as long as the process lives.
/// <para>
/// An upload is an input to a render that has been ACCEPTED, and the queue it waits in is durable and unbounded — a
/// batch can sit for hours before its slot comes up. So this store keeps what it is given: nothing here may expire,
/// be evicted, or be reclaimed while the process runs. A byte budget that dropped the least-recently-used past it
/// would let queue depth silently destroy queued work — a bulk submission would evict its own earlier sources long
/// before the worker reached them, and accepted jobs would fail with "source image not found" for inputs the app
/// itself had thrown away. A render input is not cache.
/// </para>
/// <para>Registered as a singleton — it IS the storage, so there is exactly one per process.</para>
/// </summary>
public sealed class InMemoryUploadStore : IUploadStore
{
    private const string GuidFormat = "N";
    private readonly object _gate = new();
    private readonly Dictionary<string, UploadedImage> _index = new(StringComparer.Ordinal);
    private long _bytes;

    /// <summary>Uploads currently resident (diagnostics).</summary>
    public int Count { get { lock (_gate) return _index.Count; } }

    /// <summary>Bytes currently resident (diagnostics).</summary>
    public long Bytes { get { lock (_gate) return _bytes; } }

    public string Add(UploadedImage image)
    {
        string id = Guid.NewGuid().ToString(GuidFormat);   // same shape as a generated image id, so callers can't tell them apart
        lock (_gate)
        {
            _index[id] = image;
            _bytes += image.Bytes.LongLength;
        }
        return id;
    }

    public UploadedImage? Get(string id)
    {
        lock (_gate) return _index.GetValueOrDefault(id);
    }
}
