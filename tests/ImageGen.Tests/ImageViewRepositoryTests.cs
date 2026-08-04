using ImageGen.Domain.Entities;

namespace ImageGen.Tests;

/// <summary>
/// The grids outline an image until the user has opened it. That state is a row per (user, image) rather than
/// anything the browser holds, because it has to survive a reload and be the same answer on every device the user
/// has — the previous behaviour was an in-memory set of "generated while this tab was open", which is neither.
/// </summary>
[Collection("db")]
public sealed class ImageViewRepositoryTests(TestDatabaseFixture fixture)
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTime Now = new(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Unviewed is the ABSENCE of a row, so a brand new image needs nothing written to be outlined.</summary>
    [Fact]
    public async Task An_image_with_no_record_is_unviewed()
    {
        var user = await fixture.NewUserAsync("view-absent");

        var viewed = await fixture.ImageViews.ViewedAsync(user.Id, ["never-opened"], Ct);

        Assert.Empty(viewed);
    }

    [Fact]
    public async Task Marking_viewed_is_remembered_and_scoped_to_the_asked_ids()
    {
        var user = await fixture.NewUserAsync("view-mark");
        await fixture.ImageViews.MarkViewedAsync(user.Id, "opened", Now, Ct);

        var viewed = await fixture.ImageViews.ViewedAsync(user.Id, ["opened", "not-opened"], Ct);

        Assert.Contains("opened", viewed);
        Assert.DoesNotContain("not-opened", viewed);
    }

    /// <summary>Opening an image again is not a new fact. Both entry points (the detail page and the lightbox's card
    /// fetch) mark on every load, so this runs constantly and must never fail or move the first-view time.</summary>
    [Fact]
    public async Task Marking_viewed_twice_is_idempotent_and_keeps_the_first_time()
    {
        var user = await fixture.NewUserAsync("view-twice");
        var later = Now.AddHours(3);

        await fixture.ImageViews.MarkViewedAsync(user.Id, "twice", Now, Ct);
        await fixture.ImageViews.MarkViewedAsync(user.Id, "twice", later, Ct);

        Assert.Contains("twice", await fixture.ImageViews.ViewedAsync(user.Id, ["twice"], Ct));
        Assert.Equal(Now, await ViewedAtAsync(user.Id, "twice"));
    }

    /// <summary>One user opening an image says nothing about another's. Per-user is the whole point.</summary>
    [Fact]
    public async Task Views_are_isolated_per_user()
    {
        var alice = await fixture.NewUserAsync("view-alice");
        var bob = await fixture.NewUserAsync("view-bob");
        await fixture.ImageViews.MarkViewedAsync(alice.Id, "shared-image", Now, Ct);

        Assert.Contains("shared-image", await fixture.ImageViews.ViewedAsync(alice.Id, ["shared-image"], Ct));
        Assert.Empty(await fixture.ImageViews.ViewedAsync(bob.Id, ["shared-image"], Ct));
    }

    /// <summary>
    /// "Mark all viewed" clears the backlog in one call — without it an outline meaning "unread" could only be
    /// cleared one image at a time. It covers exactly this user's own history, leaves already-viewed rows alone,
    /// and answers how many it newly covered so the UI reports what happened instead of guessing.
    /// </summary>
    [Fact]
    public async Task Mark_all_covers_this_users_history_and_only_theirs()
    {
        var alice = await fixture.NewUserAsync("view-all-alice");
        var bob = await fixture.NewUserAsync("view-all-bob");
        await fixture.History.AddAsync(Entry(alice.Id, "a1"), Ct);
        await fixture.History.AddAsync(Entry(alice.Id, "a2"), Ct);
        await fixture.History.AddAsync(Entry(bob.Id, "b1"), Ct);
        await fixture.ImageViews.MarkViewedAsync(alice.Id, "a1", Now, Ct);   // already seen

        var marked = await fixture.ImageViews.MarkAllViewedAsync(alice.Id, Now.AddDays(1), Ct);

        Assert.Equal(1, marked);   // a2 only — a1 was already viewed
        Assert.Equal(Now, await ViewedAtAsync(alice.Id, "a1"));   // the first view's time survives
        Assert.Contains("a2", await fixture.ImageViews.ViewedAsync(alice.Id, ["a2"], Ct));
        Assert.Empty(await fixture.ImageViews.ViewedAsync(bob.Id, ["b1"], Ct));

        Assert.Equal(0, await fixture.ImageViews.MarkAllViewedAsync(alice.Id, Now.AddDays(2), Ct));   // idempotent
    }

    /// <summary>Deleting an image takes its view record with it — the cascade's whole promise is that nothing
    /// referencing a deleted image is left behind.</summary>
    [Fact]
    public async Task Deleting_an_image_removes_its_view_record()
    {
        var user = await fixture.NewUserAsync("view-delete");
        await fixture.History.AddAsync(Entry(user.Id, "doomed"), Ct);
        await fixture.ImageViews.MarkViewedAsync(user.Id, "doomed", Now, Ct);

        Assert.True(await fixture.ImageDeletions.DeleteEverywhereAsync(user.Id, "doomed", Ct));

        Assert.Empty(await fixture.ImageViews.ViewedAsync(user.Id, ["doomed"], Ct));
    }

    private async Task<DateTime> ViewedAtAsync(long userId, string imageId)
    {
        await using var conn = await fixture.ConnectionFactory.OpenAsync(Ct);
        await using var cmd = conn.Command(
            "SELECT ViewedAtUtc FROM dbo.ImageView WHERE UserId = @u AND GatewayImageId = @i;");
        cmd.AddParam("@u", userId);
        cmd.AddParam("@i", imageId);
        // Convert, don't unbox: SQL Server hands back a DateTime, SQLite the ISO-8601 TEXT it stores.
        return DateTime.SpecifyKind(Convert.ToDateTime(await cmd.ExecuteScalarAsync(Ct)), DateTimeKind.Utc);
    }

    private static HistoryEntry Entry(long userId, string imageId) => new()
    {
        UserId = userId,
        GatewayImageId = imageId,
        Prompt = "a prompt",
        ModelFriendly = "Test Model",
        ModelId = "test",
        Aspect = "square",
        CreatedAtUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
    };
}
