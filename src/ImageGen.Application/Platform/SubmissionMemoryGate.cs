namespace ImageGen.Application.Platform;

/// <summary>
/// The door check for work that will hold memory until it renders. Uploaded sources/references/masks live in RAM for
/// as long as their job waits in the queue and are never evicted, so the box's free memory is a real admission
/// constraint — and the ONLY correct moment to act on it is before the work is accepted.
/// <para>
/// This replaces the previous arrangement, where the upload store silently dropped the least-recently-used input past
/// a byte budget. That answered a full box by destroying work already accepted: a bulk submission evicted its own
/// earlier sources, and 16,831 jobs the API had returned a jobId for failed later with "source image not found".
/// Refusing at the door tells the caller the truth at the one moment they can still do something about it.
/// </para>
/// </summary>
/// <param name="memory">Reports the machine's available physical memory.</param>
/// <param name="minAvailableBytes">Free memory the box must have to accept new work.</param>
/// <param name="minAvailableBytes">
/// Read on every check rather than captured, because the floor is a machine setting the settings page can change
/// while the app is running.
/// </param>
public sealed class SubmissionMemoryGate(ISystemMemory memory, Func<long> minAvailableBytes)
{
    private readonly ISystemMemory _memory = memory;
    private readonly Func<long> _minAvailableBytes = minAvailableBytes;

    /// <summary>Free memory required to accept new work, in bytes.</summary>
    public long MinAvailableBytes => _minAvailableBytes();

    /// <summary>
    /// Null when the box has room to accept work; otherwise the message to return to the caller, naming both the
    /// measured figure and the floor so the refusal is actionable rather than a bare failure.
    /// </summary>
    public string? Refusal()
    {
        var available = _memory.AvailableBytes();
        var floor = MinAvailableBytes;
        if (available >= floor)
            return null;
        return $"The renderer is low on memory ({available / (1024 * 1024)} MB free, {floor / (1024 * 1024)} MB required) "
             + "and is not accepting new work. Queued renders hold their source images in memory until they run; "
             + "wait for the queue to drain and submit again.";
    }
}
