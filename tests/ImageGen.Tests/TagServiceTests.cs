using ImageGen.Application.Services;
using ImageGen.Domain;
using ImageGen.Domain.Entities;

namespace ImageGen.Tests;

[Collection("db")]
public sealed class TagServiceTests(TestDatabaseFixture fixture)
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private TagService NewService() => new(fixture.TagDisplays, fixture.History);

    /// <summary>
    /// A tag card resolves to the manual pick, else the user's most recent generation carrying the tag, else nothing
    /// (a placeholder). Mirrors the artist card; the additive multi-tag case is pinned by the repository test.
    /// </summary>
    [Fact]
    public async Task ResolveMany_falls_back_to_latest_gen_then_honors_a_pick()
    {
        User user = await fixture.NewUserAsync("tag-display");
        // Two gens for "snow"; the newer is the fallback display image.
        await fixture.History.AddAsync(Gen(user.Id, "snow-old", "snow", new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc)), Ct);
        await fixture.History.AddAsync(Gen(user.Id, "snow-new", "snow", new DateTime(2026, 1, 2, 9, 0, 0, DateTimeKind.Utc)), Ct);
        TagService svc = NewService();

        IReadOnlyDictionary<string, string> fallback = await svc.ResolveManyAsync(user.Id, ["snow"], Ct);
        Assert.Equal("snow-new", fallback["snow"]);   // newest gen with the tag

        // Pick the older one explicitly -> the manual pick wins.
        Assert.True(await svc.SetAsync(user.Id, "snow", "snow-old", DateTime.UtcNow, Ct));
        Assert.Equal("snow-old", (await svc.ResolveManyAsync(user.Id, ["snow"], Ct))["snow"]);

        // Clearing the pick reverts to the latest gen (no longer a placeholder).
        await svc.ClearAsync(user.Id, "snow", Ct);
        Assert.Equal("snow-new", (await svc.ResolveManyAsync(user.Id, ["snow"], Ct))["snow"]);
    }

    [Fact]
    public async Task Set_rejects_an_image_not_in_the_users_history()
    {
        User user = await fixture.NewUserAsync("tag-reject");
        Assert.False(await NewService().SetAsync(user.Id, "snow", "not-mine", DateTime.UtcNow, Ct));
    }

    [Fact]
    public async Task ResolveMany_is_per_user_and_absent_when_a_tag_has_neither_pick_nor_gen()
    {
        User alice = await fixture.NewUserAsync("tag-many-a");
        User bob = await fixture.NewUserAsync("tag-many-b");
        await fixture.History.AddAsync(Gen(alice.Id, "a-snow", "snow", new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc)), Ct);
        await fixture.History.AddAsync(Gen(bob.Id, "b-snow", "snow", new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc)), Ct);

        IReadOnlyDictionary<string, string> resolved = await NewService().ResolveManyAsync(alice.Id, ["snow", "unseen"], Ct);
        Assert.Equal("a-snow", resolved["snow"]);           // fallback to alice's own gen
        Assert.False(resolved.ContainsKey("unseen"));        // no gen, no pick -> absent (placeholder)
        Assert.DoesNotContain("b-snow", resolved.Values);    // never sees bob's image
    }

    private static HistoryEntry Gen(long userId, string imageId, string tag, DateTime createdAtUtc) => new()
    {
        UserId = userId,
        GatewayImageId = imageId,
        Prompt = "a prompt",
        ModelFriendly = "Test Model",
        ModelId = "test",
        Aspect = "square",
        CreatedAtUtc = createdAtUtc,
        Marks = [new Mark(tag, TokenKind.Tag)],
    };
}
