using ImageGen.Api.Endpoints;
using ImageGen.Application.Services;
using ImageGen.Domain.Entities;

namespace ImageGen.Tests;

/// <summary>The autocomplete "pin my bookmarks" merge: bookmarks come first, a token that is both bookmarked and ranked
/// appears once (as the pin), and the endpoint's limit still holds.</summary>
public sealed class PinBookmarkMergeTests
{
    private static ForgeApi.TagSuggestionItem Ranked(string name) => new(name, 100, 0, false, new ForgeApi.Ranking(0.1, null));
    private static ForgeApi.TagSuggestionItem Pin(string name) => new(name, 100, 0, true, Ranking: null);

    [Fact]
    public void Pins_come_first_then_the_ranked_remainder()
    {
        List<ForgeApi.TagSuggestionItem> pinned = [Pin("bbb"), Pin("aaa")];   // caller-supplied order is preserved
        List<ForgeApi.TagSuggestionItem> ranked = [Ranked("ccc"), Ranked("ddd")];

        List<ForgeApi.TagSuggestionItem> merged = ForgeApi.MergePinnedFirst(pinned, ranked, 10);

        Assert.Equal(["bbb", "aaa", "ccc", "ddd"], merged.Select(m => m.Name));
        Assert.Equal([true, true, false, false], merged.Select(m => m.Bookmarked));
    }

    [Fact]
    public void A_token_that_is_both_bookmarked_and_ranked_appears_once_as_the_pin()
    {
        List<ForgeApi.TagSuggestionItem> pinned = [Pin("smile")];
        List<ForgeApi.TagSuggestionItem> ranked = [Ranked("smile"), Ranked("frown")];   // "smile" also came back ranked

        List<ForgeApi.TagSuggestionItem> merged = ForgeApi.MergePinnedFirst(pinned, ranked, 10);

        Assert.Equal(["smile", "frown"], merged.Select(m => m.Name));
        Assert.True(merged[0].Bookmarked);   // kept as the pin, not the ranked duplicate
    }

    [Fact]
    public void Dedup_is_case_insensitive_mirroring_the_catalog()
    {
        List<ForgeApi.TagSuggestionItem> pinned = [Pin("Smile")];
        List<ForgeApi.TagSuggestionItem> ranked = [Ranked("smile")];

        List<ForgeApi.TagSuggestionItem> merged = ForgeApi.MergePinnedFirst(pinned, ranked, 10);

        Assert.Single(merged);
        Assert.Equal("Smile", merged[0].Name);
    }

    [Fact]
    public void The_limit_still_holds_and_pins_win_the_slots()
    {
        List<ForgeApi.TagSuggestionItem> pinned = [Pin("a"), Pin("b"), Pin("c")];
        List<ForgeApi.TagSuggestionItem> ranked = [Ranked("d"), Ranked("e")];

        List<ForgeApi.TagSuggestionItem> merged = ForgeApi.MergePinnedFirst(pinned, ranked, 4);

        Assert.Equal(["a", "b", "c", "d"], merged.Select(m => m.Name));   // capped at 4; "e" drops, not a pin
    }

    [Fact]
    public void More_matching_bookmarks_than_the_limit_returns_only_bookmarks()
    {
        List<ForgeApi.TagSuggestionItem> pinned = [Pin("a"), Pin("b"), Pin("c")];
        List<ForgeApi.TagSuggestionItem> ranked = [Ranked("d")];

        List<ForgeApi.TagSuggestionItem> merged = ForgeApi.MergePinnedFirst(pinned, ranked, 2);

        Assert.Equal(["a", "b"], merged.Select(m => m.Name));   // the user asked for their bookmarks first
    }
}

/// <summary>The pin-bookmarks toggle persists per user and starts off, exactly as the account expects.</summary>
[Collection("db")]
public sealed class PinBookmarkSuggestionsPersistenceTests(TestDatabaseFixture fixture)
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private UserService Service() => new(fixture.Users, TimeProvider.System);

    [Fact]
    public async Task Toggle_starts_off_and_round_trips_per_user()
    {
        UserService svc = Service();
        User? user = await svc.RegisterAsync("pin_user", "password1", "", Ct);
        Assert.NotNull(user);
        Assert.False(user.PinBookmarkSuggestions);   // off by default — autocomplete is unchanged until it is turned on

        await svc.SetPinBookmarkSuggestionsAsync(user.Id, true, Ct);
        User? reloaded = await svc.GetByIdAsync(user.Id, Ct);
        Assert.NotNull(reloaded);
        Assert.True(reloaded.PinBookmarkSuggestions);

        await svc.SetPinBookmarkSuggestionsAsync(user.Id, false, Ct);
        reloaded = await svc.GetByIdAsync(user.Id, Ct);
        Assert.NotNull(reloaded);
        Assert.False(reloaded.PinBookmarkSuggestions);
    }
}
