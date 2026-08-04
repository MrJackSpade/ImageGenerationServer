using ImageGen.Application.Rendering;
using ImageGen.Domain;
using ImageGen.Domain.Entities;

namespace ImageGen.Tests;

/// <summary>
/// A ban is a SERVER-side fact: the orchestrator reads the user's banned tokens for the workflow it is about to render
/// and turns them into the exclusion sets the random samplers honour (the tag model zeroes banned ids every sampling
/// step; RandomArtist rejects banned picks). Nothing about that depends on the caller declaring its own bans, so these
/// lock the one place a stored ban row becomes an exclusion key.
/// </summary>
public sealed class BanKeyTests
{
    private static BannedToken Ban(string name, TokenKind kind) =>
        new() { UserId = 1, ModelId = "anima", Name = name, Kind = kind, SavedAtUtc = DateTime.UnixEpoch };

    [Fact]
    public void Bans_split_by_kind_into_the_sampler_exclusion_sets()
    {
        var (tags, artists) = RenderOrchestrator.BanKeys(
            [Ban("wet_shirt", TokenKind.Tag), Ban("bad_anatomy", TokenKind.Tag), Ban("some_artist", TokenKind.Artist)]);

        Assert.True(tags.SetEquals(["wet_shirt", "bad_anatomy"]));
        Assert.True(artists.SetEquals(["some_artist"]));
    }

    [Fact]
    public void A_ban_key_is_canonicalized_so_it_matches_the_sampled_token()
    {
        // The tag model samples canonical booru tokens ("wet_shirt"), but a ban can be typed into Settings free-hand
        // ("Wet Shirt") or arrive marker-prefixed from the chip UI. Both must still zero the same tag.
        var (tags, artists) = RenderOrchestrator.BanKeys([Ban("Wet Shirt", TokenKind.Tag), Ban("@Some Artist", TokenKind.Artist)]);

        Assert.Contains("wet_shirt", tags);
        Assert.Contains("some_artist", artists);
    }

    [Fact]
    public void No_bans_means_no_exclusions()
    {
        var (tags, artists) = RenderOrchestrator.BanKeys([]);
        Assert.Empty(tags);
        Assert.Empty(artists);
    }
}
