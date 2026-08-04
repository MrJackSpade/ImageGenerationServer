//TODO: CHECK FOR FALLBACKS
namespace ImageGen.Domain.Entities;

/// <summary>
/// One successful generation/edit's actual render time, recorded per machine. The duration is the ComfyUI
/// execution time only — measured from when the prompt is submitted to the backend (the gen has started) to when
/// the image is produced, so it EXCLUDES the time the job spent waiting in the fair queue. Stored per
/// <see cref="MachineName"/> because each box (204/206) renders at a different speed; the UI's ETA for a model is
/// the average of its last few records on the machine that's rendering.
/// </summary>
public sealed class GenTiming
{
    public long Id { get; init; }
    /// <summary>The machine that rendered it (`Environment.MachineName`) — keeps each box's speed separate.</summary>
    public required string MachineName { get; init; }
    /// <summary>The workflow configuration id (the `model` the client submitted).</summary>
    public required string ConfigId { get; init; }
    /// <summary>True for an edit, false for a generation.</summary>
    public bool IsEdit { get; init; }
    /// <summary>Render time in milliseconds (submit → image ready; queue wait excluded).</summary>
    public required int DurationMs { get; init; }
    public DateTime CreatedAtUtc { get; init; }
}
