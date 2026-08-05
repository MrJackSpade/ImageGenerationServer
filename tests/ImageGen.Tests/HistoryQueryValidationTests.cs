using ImageGen.Domain.Repositories;

namespace ImageGen.Tests;

/// <summary>
/// The page window is validated, not clamped: an out-of-range page or size is refused so a silently-corrected reply
/// can't read back to the caller as a satisfied one. Pure, no database — the contract lives on <see cref="HistoryQuery"/>.
/// </summary>
public sealed class HistoryQueryValidationTests
{
    [Theory]
    [InlineData(HistoryQuery.MinPage, HistoryQuery.MinPageSize)]
    [InlineData(1, 40)]
    [InlineData(5, HistoryQuery.MaxPageSize)]
    public void Valid_page_and_window_pass(int page, int pageSize) =>
        new HistoryQuery(1, page, pageSize).Validate();

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Page_below_one_is_refused(int page) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new HistoryQuery(1, page, 40).Validate());

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(HistoryQuery.MaxPageSize + 1)]
    [InlineData(10_000)]
    public void Window_outside_bounds_is_refused(int pageSize) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new HistoryQuery(1, 1, pageSize).Validate());
}
