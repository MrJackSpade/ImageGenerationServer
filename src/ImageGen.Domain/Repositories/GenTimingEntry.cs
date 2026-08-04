namespace ImageGen.Domain.Repositories;

/// <summary>
/// The parameters of a render that materially drive its time, captured with each timing sample so the ETA can be
/// matched to THIS request rather than a flat per-model average. <see cref="Width"/>×<see cref="Height"/> is the
/// resolved render size (pixels); <see cref="Steps"/>/<see cref="Frames"/> are the <c>EtaVariable</c>-marked params
/// (null when the workflow doesn't mark them). Absent fields fall back to a per-model average, so nothing regresses.
/// </summary>
public readonly record struct EtaSignature(int Width, int Height, int? Steps, int? Frames)
{
    /// <summary>Relative render "work" — time is modelled as ~proportional to pixels × steps × frames. Missing/zero
    /// factors are treated as 1 so they neither zero the product nor scale it. Used to unit-cost the recent samples.</summary>
    public double Work() =>
        Math.Max(1.0, (double)Width * Height) * Math.Max(1, Steps ?? 1) * Math.Max(1, Frames ?? 1);
}

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
    int? RenderWidth = null, int? RenderHeight = null, int? Steps = null, int? Frames = null);
