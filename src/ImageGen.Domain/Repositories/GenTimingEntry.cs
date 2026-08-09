using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Domain.Repositories;

/// <summary>
/// The parameters of a render that materially drive its time, captured with each timing sample so the ETA can be
/// computed from samples that rendered the EXACT same signature. <see cref="Width"/>×<see cref="Height"/> is the
/// resolved render size (pixels); <see cref="Steps"/>/<see cref="Frames"/> are the <c>EtaVariable</c>-marked params
/// (null when the workflow doesn't mark them). Render time is not a linear function of any of these (video attention
/// cost is superlinear in frames, and every model carries fixed overhead), so samples are never scaled toward a
/// different signature — a signature with no matching samples simply has no ETA.
/// </summary>
public readonly record struct EtaSignature(
    int Width,
    int Height,
    [property: AllowNullable("null = the workflow doesn't mark steps EtaVariable; matched null-to-null, distinct from a real 0")] int? Steps,
    [property: AllowNullable("null = the workflow doesn't mark frames EtaVariable; matched null-to-null, distinct from a real 0")] int? Frames);

/// <summary>
/// One successful render's measured duration, recorded per machine + workflow configuration. Captures ComfyUI
/// execution time only (queue wait excluded); it feeds the UI's per-model ETA, param-matched via the signature.
/// </summary>
/// <param name="MachineName">The instance that rendered.</param>
/// <param name="ConfigId">The workflow configuration id that was rendered.</param>
/// <param name="IsEdit">True for an edit render, false for a generation.</param>
/// <param name="DurationMs">Measured ComfyUI execution time in milliseconds.</param>
/// <param name="RenderWidth">Resolved render width (px), or null when not captured (pre-signature rows).</param>
/// <param name="RenderHeight">Resolved render height (px), or null.</param>
/// <param name="Steps">Sampler steps, or null when the workflow doesn't mark steps EtaVariable.</param>
/// <param name="Frames">Clip length in frames (0/absent for a still), or null.</param>
public sealed record GenTimingEntry(
    string MachineName, string ConfigId, bool IsEdit, int DurationMs,
    [property: AllowNullable("null = render width not captured (pre-signature rows); such rows never match any signature and price nothing")] int? RenderWidth = null,
    [property: AllowNullable("null = render height not captured (pre-signature rows); such rows never match any signature and price nothing")] int? RenderHeight = null,
    [property: AllowNullable("null = the workflow doesn't mark steps EtaVariable; distinct from a real 0")] int? Steps = null,
    [property: AllowNullable("null = a still (no frame count) or not captured; distinct from a real 0")] int? Frames = null);
