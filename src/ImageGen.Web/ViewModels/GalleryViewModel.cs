using ImageGen.Domain.Repositories;

namespace ImageGen.Web.ViewModels;

public sealed class GalleryViewModel
{
    public required IReadOnlyList<HistoryItemView> Items { get; init; }
    public required int Page { get; init; }
    public required int PageSize { get; init; }
    public required int Total { get; init; }

    /// <summary>What the search box holds ("" when unfiltered). <see cref="Total"/> counts MATCHES when it is set.</summary>
    public required string Search { get; init; }

    /// <summary>The selected workflow's configuration id ("" for all workflows).</summary>
    public required string Workflow { get; init; }

    /// <summary>Narrow the grid to images this user has never opened. A query-string parameter like the others, so
    /// the filtered view survives a reload and a bookmark.</summary>
    public bool UnviewedOnly { get; init; }

    /// <summary>The workflow filter's options: what the user has actually generated with, most-used first.</summary>
    public required IReadOnlyList<HistoryWorkflowUse> Workflows { get; init; }
}
