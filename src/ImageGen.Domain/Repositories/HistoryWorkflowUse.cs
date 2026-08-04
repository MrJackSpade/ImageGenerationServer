//TODO: CHECK FOR FALLBACKS
namespace ImageGen.Domain.Repositories;

/// <summary>
/// One workflow configuration the user has actually generated with, and how many of their images came out of it —
/// the options the history page's workflow filter offers. Read off the history itself rather than the workflow
/// catalog, so the filter can never offer a choice that matches nothing (and still lists a workflow that has since
/// been removed from the catalog but whose images are still in the history).
/// </summary>
/// <param name="ModelId">The configuration id, as <c>HistoryQuery.Model</c> takes it.</param>
/// <param name="ModelFriendly">Its display name, as of the user's most recent generation with it.</param>
/// <param name="Count">How many of the user's history entries it produced.</param>
public sealed record HistoryWorkflowUse(string ModelId, string ModelFriendly, int Count);
