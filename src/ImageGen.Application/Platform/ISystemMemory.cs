//TODO: CHECK FOR FALLBACKS
namespace ImageGen.Application.Platform;

/// <summary>
/// How much physical memory the BOX has free right now. A port, because the answer is platform-specific and the
/// application layer must not care which platform it is on.
/// <para>
/// It exists for one decision: uploads are render inputs held in RAM for as long as their job waits in the queue, and
/// nothing may evict them (see <c>IUploadStore</c>). So the only honest place to refuse work is at the door — if the
/// box is already low, the submission is REJECTED and the caller is told, instead of being accepted and then failed
/// later for an input the app threw away.
/// </para>
/// </summary>
public interface ISystemMemory
{
    /// <summary>Physical memory currently available on this machine, in bytes.</summary>
    long AvailableBytes();
}
