using ImageGen.Application.Rendering;
using ImageGen.Domain;
using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;

namespace ImageGen.Tests;

/// <summary>
/// The ban store, exercised end-to-end the way the render worker uses it: it saves a ban, then reads it back for
/// (user, workflow) and turns it into the sampler exclusion sets. This is the whole path a banned tag now travels —
/// deterministic-encrypt on write, decrypt on read, canonicalize into a key — so a break anywhere along it (a cipher
/// mismatch, a key that no longer matches the sampled token) shows up here rather than as a banned tag coming back in
/// a generated prompt.
/// </summary>
[Collection("db")]
public sealed class BannedTokenRepositoryTests(TestDatabaseFixture fixture)
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static BannedToken Ban(long userId, string model, string name, TokenKind kind) =>
        new() { UserId = userId, ModelId = model, Name = name, Kind = kind, SavedAtUtc = DateTime.UtcNow };

    [Fact]
    public async Task A_saved_ban_comes_back_as_a_sampler_exclusion_for_that_workflow()
    {
        User user = await fixture.NewUserAsync("ban-roundtrip");
        _ = await fixture.Bans.AddAsync(Ban(user.Id, "anima", "wet_shirt", TokenKind.Tag), Ct);
        _ = await fixture.Bans.AddAsync(Ban(user.Id, "anima", "some_artist", TokenKind.Artist), Ct);

        (HashSet<string>? tags, HashSet<string>? artists) = RenderOrchestrator.BanKeys(await fixture.Bans.GetForModelAsync(user.Id, "anima", Ct));

        Assert.Contains("wet_shirt", tags);
        Assert.Contains("some_artist", artists);
    }

    [Fact]
    public async Task A_ban_typed_free_hand_still_matches_the_token_the_model_samples()
    {
        // Settings takes a free-hand name. The tag model samples canonical booru tokens, so "Wet Shirt" has to survive
        // the encrypt/decrypt round trip AND canonicalize to "wet_shirt", or the ban silently never fires.
        User user = await fixture.NewUserAsync("ban-freehand");
        _ = await fixture.Bans.AddAsync(Ban(user.Id, "anima", "Wet Shirt", TokenKind.Tag), Ct);

        (HashSet<string>? tags, _) = RenderOrchestrator.BanKeys(await fixture.Bans.GetForModelAsync(user.Id, "anima", Ct));

        Assert.Contains("wet_shirt", tags);
    }

    [Fact]
    public async Task A_ban_binds_only_the_workflow_it_was_saved_for()
    {
        User user = await fixture.NewUserAsync("ban-permodel");
        _ = await fixture.Bans.AddAsync(Ban(user.Id, "anima", "wet_shirt", TokenKind.Tag), Ct);

        (HashSet<string>? other, _) = RenderOrchestrator.BanKeys(await fixture.Bans.GetForModelAsync(user.Id, "pixelharness", Ct));

        Assert.Empty(other);
    }

    [Fact]
    public async Task Bans_are_isolated_per_user()
    {
        User alice = await fixture.NewUserAsync("ban-alice");
        User bob = await fixture.NewUserAsync("ban-bob");
        _ = await fixture.Bans.AddAsync(Ban(alice.Id, "anima", "wet_shirt", TokenKind.Tag), Ct);

        (HashSet<string>? bobs, _) = RenderOrchestrator.BanKeys(await fixture.Bans.GetForModelAsync(bob.Id, "anima", Ct));

        Assert.Empty(bobs);
    }

    [Fact]
    public async Task Removing_a_ban_lifts_the_exclusion()
    {
        User user = await fixture.NewUserAsync("ban-lift");
        _ = await fixture.Bans.AddAsync(Ban(user.Id, "anima", "wet_shirt", TokenKind.Tag), Ct);

        Assert.True(await fixture.Bans.RemoveAsync(new BannedTokenKey(user.Id, "anima", "wet_shirt", TokenKind.Tag), Ct));

        (HashSet<string>? tags, _) = RenderOrchestrator.BanKeys(await fixture.Bans.GetForModelAsync(user.Id, "anima", Ct));
        Assert.Empty(tags);
    }
}
