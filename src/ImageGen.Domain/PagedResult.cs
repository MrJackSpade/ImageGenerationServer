namespace ImageGen.Domain;

/// <summary>A page of results plus the total row count for the query (for paging UI).</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);
