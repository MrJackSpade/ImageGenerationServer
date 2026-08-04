namespace ImageGen.Domain.Repositories;

/// <summary>
/// The newer and older neighbours of a history entry in the user's newest-first history, for the detail view's
/// prev/next navigation. Either id is null when the entry sits at that end of the history.
/// </summary>
/// <param name="NewerId">Gateway image id of the next-newer entry, or null at the newest end.</param>
/// <param name="OlderId">Gateway image id of the next-older entry, or null at the oldest end.</param>
public sealed record HistoryNeighbors(string? NewerId, string? OlderId);
