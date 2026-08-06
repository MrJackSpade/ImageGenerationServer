using ImageGen.Domain;
using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;

namespace ImageGen.Tests;

/// <summary>
/// The unviewed filter is a predicate in the QUERY, not something applied to a page that has already come back.
///
/// <para>That distinction is the whole ticket. The grid pages in as you scroll, so a client-side filter would return
/// short pages, a total describing the wrong set, and a scroll that stalls the moment one page of 40 happens to be
/// entirely viewed. These tests are written so that a post-filtering implementation fails them: the viewed rows are
/// deliberately the NEWEST, so they fill page one on their own.</para>
/// </summary>
[Collection("db")]
public sealed class UnviewedHistoryFilterTests(TestDatabaseFixture fixture)
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTime Now = new(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_full_page_of_unviewed_comes_back_even_when_the_newest_are_all_viewed()
    {
        User user = await fixture.NewUserAsync("unviewed-page");
        // 12 images, newest first by CreatedAtUtc. The newest 5 get opened.
        for (int i = 0; i < 12; i++)
        {
            _ = await fixture.History.AddAsync(Entry(user.Id, $"uv-{i:00}", created: Now.AddMinutes(i)), Ct);
        }

        for (int i = 7; i < 12; i++)
        {
            await fixture.ImageViews.MarkViewedAsync(user.Id, $"uv-{i:00}", Now, Ct);
        }

        // Page size 5: post-filtering would take the newest five (all viewed), drop them all, and hand back nothing.
        PagedResult<HistoryEntry> page = await fixture.History.GetPageAsync(
            new HistoryQuery(user.Id, 1, 5, UnviewedOnly: true), Ct);

        Assert.Equal(5, page.Items.Count);
        Assert.All(page.Items, e => Assert.DoesNotContain(e.GatewayImageId, new[] { "uv-07", "uv-08", "uv-09", "uv-10", "uv-11" }));
    }

    [Fact]
    public async Task The_total_counts_only_unviewed()
    {
        User user = await fixture.NewUserAsync("unviewed-total");
        for (int i = 0; i < 9; i++)
        {
            _ = await fixture.History.AddAsync(Entry(user.Id, $"ut-{i:00}", created: Now.AddMinutes(i)), Ct);
        }

        for (int i = 0; i < 4; i++)
        {
            await fixture.ImageViews.MarkViewedAsync(user.Id, $"ut-{i:00}", Now, Ct);
        }

        PagedResult<HistoryEntry> filtered = await fixture.History.GetPageAsync(new HistoryQuery(user.Id, 1, 40, UnviewedOnly: true), Ct);
        PagedResult<HistoryEntry> all = await fixture.History.GetPageAsync(new HistoryQuery(user.Id, 1, 40), Ct);

        Assert.Equal(5, filtered.Total);
        Assert.Equal(9, all.Total);
    }

    [Fact]
    public async Task Paging_the_filtered_set_does_not_repeat_or_skip()
    {
        // The second page has to be the second page OF THE FILTERED SET. Offsetting into the unfiltered set instead
        // is the other way this goes wrong, and it looks fine on page one.
        User user = await fixture.NewUserAsync("unviewed-paging");
        for (int i = 0; i < 10; i++)
        {
            _ = await fixture.History.AddAsync(Entry(user.Id, $"up-{i:00}", created: Now.AddMinutes(i)), Ct);
        }

        foreach (int i in new[] { 1, 3, 5, 7, 9 })
        {
            await fixture.ImageViews.MarkViewedAsync(user.Id, $"up-{i:00}", Now, Ct);
        }

        PagedResult<HistoryEntry> p1 = await fixture.History.GetPageAsync(new HistoryQuery(user.Id, 1, 3, UnviewedOnly: true), Ct);
        PagedResult<HistoryEntry> p2 = await fixture.History.GetPageAsync(new HistoryQuery(user.Id, 2, 3, UnviewedOnly: true), Ct);

        List<string> ids = p1.Items.Concat(p2.Items).Select(e => e.GatewayImageId).ToList();
        Assert.Equal(5, ids.Count);
        Assert.Equal(5, ids.Distinct().Count());
        Assert.All(ids, id => Assert.Contains(id, new[] { "up-00", "up-02", "up-04", "up-06", "up-08" }));
    }

    [Fact]
    public async Task It_combines_with_the_workflow_filter()
    {
        User user = await fixture.NewUserAsync("unviewed-combined");
        _ = await fixture.History.AddAsync(Entry(user.Id, "uc-a", created: Now.AddMinutes(1), modelId: "alpha"), Ct);
        _ = await fixture.History.AddAsync(Entry(user.Id, "uc-b", created: Now.AddMinutes(2), modelId: "alpha"), Ct);
        _ = await fixture.History.AddAsync(Entry(user.Id, "uc-c", created: Now.AddMinutes(3), modelId: "beta"), Ct);
        await fixture.ImageViews.MarkViewedAsync(user.Id, "uc-b", Now, Ct);

        PagedResult<HistoryEntry> page = await fixture.History.GetPageAsync(
            new HistoryQuery(user.Id, 1, 40, Model: "alpha", UnviewedOnly: true), Ct);

        _ = Assert.Single(page.Items);
        Assert.Equal("uc-a", page.Items[0].GatewayImageId);
    }

    [Fact]
    public async Task Off_by_default_it_changes_nothing()
    {
        User user = await fixture.NewUserAsync("unviewed-default");
        _ = await fixture.History.AddAsync(Entry(user.Id, "ud-a", created: Now), Ct);
        await fixture.ImageViews.MarkViewedAsync(user.Id, "ud-a", Now, Ct);

        PagedResult<HistoryEntry> page = await fixture.History.GetPageAsync(new HistoryQuery(user.Id, 1, 40), Ct);

        _ = Assert.Single(page.Items);
    }

    private static HistoryEntry Entry(long userId, string imageId, DateTime created, string modelId = "test") => new()
    {
        UserId = userId,
        GatewayImageId = imageId,
        Prompt = "a prompt",
        ModelFriendly = "Test Model",
        ModelId = modelId,
        Aspect = "square",
        CreatedAtUtc = created,
        Marks = [],
    };
}
