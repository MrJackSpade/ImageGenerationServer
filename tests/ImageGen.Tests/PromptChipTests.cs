//TODO: CHECK FOR FALLBACKS
using ImageGen.Web.ViewModels;

namespace ImageGen.Tests;

/// <summary>
/// The card's prompt chips are what the user clicks to bookmark or ban a token, so a chip's Key must be the canonical
/// underscored name the store keys on, and its Kind exactly "tag"/"artist". (The marker-form prompt the copy/Reload
/// buttons submit no longer comes from the chips — it rides the record blob from PromptMarkers; see PromptMarkersTests.)
/// </summary>
public sealed class PromptChipTests
{
    private static ImageDetailViewModel Card(string prompt, params (string token, string kind)[] marks) => new()
    {
        Entry = new ImageDetailView("img1", prompt, "Anima", "anima", "square", DateTime.UtcNow,
            marks.ToDictionary(m => m.token, m => m.kind)),
        MarkerPrompt = "",   // the stored HistoryEntry.RawPrompt; not what these pin
        IsBookmarked = false,
    };

    [Fact]
    public void A_marked_chip_shows_the_display_text_but_keys_on_the_canonical_token()
    {
        // Stored (finalized) prompt has spaces and no markers; the marks map keys are the underscored canonical names.
        // Display order is by type — artist before general tag before plain text — regardless of prompt order.
        var chips = Card("bad anatomy, some artist, a plain phrase", ("bad_anatomy", "tag"), ("some_artist", "artist")).Chips;

        Assert.Equal(3, chips.Count);
        Assert.Equal(("some artist", "artist", "some_artist"), (chips[0].Text, chips[0].Kind, chips[0].Key));
        Assert.Equal(("bad anatomy", "tag", "bad_anatomy"), (chips[1].Text, chips[1].Kind, chips[1].Key));
        Assert.Null(chips[2].Kind);   // plain text: not a token, nothing to bookmark or ban
    }

    [Fact]
    public void An_artist_that_kept_its_marker_still_keys_on_the_bare_token()
    {
        // Models with keep_artist_marker leave '@' in the stored prompt; the key must not carry it.
        var chips = Card("@some artist", ("some_artist", "artist")).Chips;

        Assert.Equal("some_artist", chips[0].Key);
        Assert.Equal("artist", chips[0].Kind);
    }

    [Fact]
    public void Score_tags_keep_the_underscores_the_finalizer_preserved()
    {
        var chips = Card("score_9, masterpiece", ("score_9", "tag")).Chips;

        Assert.Equal(("score_9", "tag", "score_9"), (chips[0].Text, chips[0].Kind, chips[0].Key));
        Assert.Null(chips[1].Kind);
    }

    [Fact]
    public void Bans_and_bookmarks_land_on_the_chip_that_matches_the_canonical_token()
    {
        var vm = new ImageDetailViewModel
        {
            Entry = new ImageDetailView("img1", "bad anatomy, greg rutkowski", "Anima", "anima", "square", DateTime.UtcNow,
                new Dictionary<string, string> { ["bad_anatomy"] = "tag", ["greg_rutkowski"] = "artist" }),
            MarkerPrompt = "",
            IsBookmarked = false,
            BannedTags = new HashSet<string>(StringComparer.Ordinal) { "bad_anatomy" },
            BookmarkedArtists = new HashSet<string>(StringComparer.Ordinal) { "greg_rutkowski" },
        };

        // The bookmarked artist displays first; the banned general tag follows.
        var chips = vm.Chips;
        Assert.True(chips[0].Bookmarked);
        Assert.False(chips[0].Banned);
        Assert.True(chips[1].Banned);
        Assert.False(chips[1].Bookmarked);
    }

    [Fact]
    public void Chips_display_bookmarked_first_then_by_type()
    {
        // Prompt order is deliberately scrambled: plain text, deprecated, general, character, copyright, meta, artist.
        var vm = new ImageDetailViewModel
        {
            Entry = new ImageDetailView("img1",
                "a plain phrase, old tag, smile, some girl, some show, absurdres, some artist, starred tag",
                "Anima", "anima", "square", DateTime.UtcNow,
                new Dictionary<string, string>
                {
                    ["old_tag"] = "tag",
                    ["smile"] = "tag",
                    ["some_girl"] = "tag",
                    ["some_show"] = "tag",
                    ["absurdres"] = "tag",
                    ["some_artist"] = "artist",
                    ["starred_tag"] = "tag",
                }),
            MarkerPrompt = "",
            IsBookmarked = false,
            TagTypeByToken = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["old_tag"] = 6,      // deprecated
                ["smile"] = 0,        // general
                ["some_girl"] = 4,    // character
                ["some_show"] = 3,    // copyright
                ["absurdres"] = 5,    // meta
                ["starred_tag"] = 0,  // general, but bookmarked
            },
            BookmarkedTags = new HashSet<string>(StringComparer.Ordinal) { "starred_tag" },
        };

        Assert.Equal(
            ["starred tag", "some artist", "absurdres", "some show", "some girl", "smile", "old tag", "a plain phrase"],
            vm.Chips.Select(c => c.Text));
    }

    /// <summary>
    /// Banned chips sort to the very END of the card, below every untouched chip — including plain text, and including
    /// chips whose TYPE would otherwise put them first. A ban is the user saying "not this", so it outranks the type
    /// order rather than being applied inside it: the banned artist here leads the type order and still displays last.
    /// </summary>
    [Fact]
    public void Banned_chips_display_last_whatever_their_type()
    {
        var vm = new ImageDetailViewModel
        {
            Entry = new ImageDetailView("img1", "banned artist, smile, a plain phrase, banned meta, starred tag",
                "Anima", "anima", "square", DateTime.UtcNow,
                new Dictionary<string, string>
                {
                    ["banned_artist"] = "artist",
                    ["smile"] = "tag",
                    ["banned_meta"] = "tag",
                    ["starred_tag"] = "tag",
                }),
            MarkerPrompt = "",
            IsBookmarked = false,
            TagTypeByToken = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["smile"] = 0,          // general
                ["banned_meta"] = 5,    // meta — would sort second, right after the artist
                ["starred_tag"] = 0,    // general, but bookmarked
            },
            BannedArtists = new HashSet<string>(StringComparer.Ordinal) { "banned_artist" },
            BannedTags = new HashSet<string>(StringComparer.Ordinal) { "banned_meta" },
            BookmarkedTags = new HashSet<string>(StringComparer.Ordinal) { "starred_tag" },
        };

        // bookmarked | untouched (by type) | banned (by type, at the end)
        Assert.Equal(
            ["starred tag", "smile", "a plain phrase", "banned artist", "banned meta"],
            vm.Chips.Select(c => c.Text));
    }

    /// <summary>
    /// Within one state and one type, chips order BY NAME rather than by where they happened to land in the prompt —
    /// so a tag sits in the same place on every card that carries it instead of moving with the prompt.
    /// </summary>
    [Fact]
    public void Chips_of_the_same_state_and_type_order_by_name()
    {
        var vm = new ImageDetailViewModel
        {
            Entry = new ImageDetailView("img1", "zebra print, smile, apron, zoe artist, alice artist",
                "Anima", "anima", "square", DateTime.UtcNow,
                new Dictionary<string, string>
                {
                    ["zebra_print"] = "tag",
                    ["smile"] = "tag",
                    ["apron"] = "tag",
                    ["zoe_artist"] = "artist",
                    ["alice_artist"] = "artist",
                }),
            MarkerPrompt = "",
            IsBookmarked = false,
            TagTypeByToken = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["zebra_print"] = 0,
                ["smile"] = 0,
                ["apron"] = 0,
            },
        };

        // Artists (type first) alphabetically, then the general tags alphabetically.
        Assert.Equal(
            ["alice artist", "zoe artist", "apron", "smile", "zebra print"],
            vm.Chips.Select(c => c.Text));
    }
}
