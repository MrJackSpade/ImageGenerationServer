namespace ImageGen.Domain.Repositories;

/// <summary>
/// One successful render's measured duration, recorded per machine + workflow configuration. Captures ComfyUI
/// execution time only (queue wait excluded); it feeds the UI's per-model ETA.
/// </summary>
/// <param name="MachineName">The instance that rendered.</param>
/// <param name="ConfigId">The workflow configuration id that was rendered.</param>
/// <param name="IsEdit">True for an edit render, false for a generation.</param>
/// <param name="DurationMs">Measured ComfyUI execution time in milliseconds.</param>
public sealed record GenTimingEntry(string MachineName, string ConfigId, bool IsEdit, int DurationMs);
