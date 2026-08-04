using ImageGen.Web.ViewModels;

namespace ImageGen.Tests;

/// <summary>
/// The card's prompt chips. A marked tag/artist becomes an interactive chip whose Key is the canonical underscored name
/// the bookmark/ban store keys on and whose Kind is exactly "tag"/"artist". Everything else is plain natural-language
/// text, kept VERBATIM: chips render in prompt order, consecutive plain segments stay one chip (never split on their
/// commas, never alphabetized, never pushed past the tags), and a bookmark/ban/category only STYLES a chip rather than
/// moving it. (The marker-form prompt copy/Reload submit rides the record blob, not the chips.)
/// </summary>
public sealed class PromptChipTests
{
    private static ImageDetailViewModel Card(string prompt, params (string token, string kind)[] marks) => new()
    {
        Entry = new ImageDetailView("img1", prompt, "Anima", "anima", "square", DateTime.UtcNow,
            marks.ToDictionary(m => m.token, m => m.kind)),
        MarkerPrompt = "",
        IsBookmarked = false,
    };

    /// <summary>A marked chip shows its display text and keys on the canonical token; chips stay in prompt order and the
    /// trailing prose is its own plain chip.</summary>
    [Fact]
    public void A_marked_chip_shows_display_text_keys_on_canonical_token_and_holds_prompt_order()
    {
        var chips = Card("bad anatomy, some artist, a plain phrase", ("bad_anatomy", "tag"), ("some_artist", "artist")).Chips;

        Assert.Equal(3, chips.Count);
        Assert.Equal(("bad anatomy", "tag", "bad_anatomy"), (chips[0].Text, chips[0].Kind, chips[0].Key));
        Assert.Equal(("some artist", "artist", "some_artist"), (chips[1].Text, chips[1].Kind, chips[1].Key));
        Assert.Equal("a plain phrase", chips[2].Text);
        Assert.Null(chips[2].Kind);   // plain text: not a token, nothing to bookmark or ban
    }

    /// <summary>Models with keep_artist_marker leave '@' in the stored prompt; the key must not carry it.</summary>
    [Fact]
    public void An_artist_that_kept_its_marker_still_keys_on_the_bare_token()
    {
        var chips = Card("@some artist", ("some_artist", "artist")).Chips;

        Assert.Equal("some_artist", chips[0].Key);
        Assert.Equal("artist", chips[0].Kind);
    }

    /// <summary>score_ tags keep the underscores the finalizer preserved; the trailing plain word is its own chip.</summary>
    [Fact]
    public void Score_tags_keep_the_underscores_the_finalizer_preserved()
    {
        var chips = Card("score_9, masterpiece", ("score_9", "tag")).Chips;

        Assert.Equal(("score_9", "tag", "score_9"), (chips[0].Text, chips[0].Kind, chips[0].Key));
        Assert.Null(chips[1].Kind);
    }

    /// <summary>A ban / bookmark lands on the chip whose canonical key matches — and only STYLES it, leaving the chip in
    /// its written position rather than moving it to the front or back.</summary>
    [Fact]
    public void Bans_and_bookmarks_style_the_matching_chip_without_moving_it()
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

        var chips = vm.Chips;
        Assert.Equal("bad_anatomy", chips[0].Key);   // prompt order: the banned tag stays first
        Assert.True(chips[0].Banned);
        Assert.False(chips[0].Bookmarked);
        Assert.Equal("greg_rutkowski", chips[1].Key);
        Assert.True(chips[1].Bookmarked);
        Assert.False(chips[1].Banned);
    }

    /// <summary>Chips render in the order the prompt was written — not reordered by state or type. Plain text at the
    /// FRONT stays at the front (not pushed behind the tags), and a bookmarked tag does not jump to the head.</summary>
    [Fact]
    public void Chips_render_in_prompt_order_with_plain_text_kept_in_place()
    {
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
                ["old_tag"] = 6,
                ["smile"] = 0,
                ["some_girl"] = 4,
                ["some_show"] = 3,
                ["absurdres"] = 5,
                ["starred_tag"] = 0,
            },
            BookmarkedTags = new HashSet<string>(StringComparer.Ordinal) { "starred_tag" },
        };

        Assert.Equal(
            ["a plain phrase", "old tag", "smile", "some girl", "some show", "absurdres", "some artist", "starred tag"],
            vm.Chips.Select(c => c.Text));
        Assert.Null(vm.Chips[0].Kind);          // the leading prose is one plain chip, still at the front
        Assert.True(vm.Chips[^1].Bookmarked);   // bookmarked only styles; it does not move to the head
    }

    /// <summary>A ban / bookmark styles a chip but never reorders it: the banned tokens keep their written place instead
    /// of sinking to the end, and the plain phrase stays in the MIDDLE where it was typed.</summary>
    [Fact]
    public void Banned_and_bookmarked_chips_keep_their_written_place()
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
                ["smile"] = 0,
                ["banned_meta"] = 5,
                ["starred_tag"] = 0,
            },
            BannedArtists = new HashSet<string>(StringComparer.Ordinal) { "banned_artist" },
            BannedTags = new HashSet<string>(StringComparer.Ordinal) { "banned_meta" },
            BookmarkedTags = new HashSet<string>(StringComparer.Ordinal) { "starred_tag" },
        };

        Assert.Equal(
            ["banned artist", "smile", "a plain phrase", "banned meta", "starred tag"],
            vm.Chips.Select(c => c.Text));
        Assert.True(vm.Chips[0].Banned);     // banned artist stays first, not sunk to the end
        Assert.Null(vm.Chips[2].Kind);       // plain text stays in the middle, not pushed past the tags
        Assert.True(vm.Chips[3].Banned);
        Assert.True(vm.Chips[4].Bookmarked);
    }

    /// <summary>Chips of the same type are NOT alphabetized — they hold the order the prompt was written in.</summary>
    [Fact]
    public void Chips_are_not_alphabetized()
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

        Assert.Equal(
            ["zebra print", "smile", "apron", "zoe artist", "alice artist"],
            vm.Chips.Select(c => c.Text));
    }

    /// <summary>A non-tag (natural-language) prompt has no marks, so the WHOLE prompt is one plain chip, verbatim — not
    /// split on its commas and not reordered. This is the reported FLUX repro: "this is a, test prompt".</summary>
    [Fact]
    public void A_non_tag_prompt_renders_as_one_verbatim_chip()
    {
        var chip = Assert.Single(Card("this is a, test prompt").Chips);

        Assert.Equal("this is a, test prompt", chip.Text);
        Assert.Null(chip.Kind);
    }

    /// <summary>Longer non-tag prose with several commas is still one verbatim chip.</summary>
    [Fact]
    public void A_non_tag_prose_prompt_is_not_split_or_reordered()
    {
        var chip = Assert.Single(Card("a wide shot, at dusk, dramatic lighting").Chips);

        Assert.Equal("a wide shot, at dusk, dramatic lighting", chip.Text);
    }

    /// <summary>Plain prose mixed INTO a booru prompt is preserved verbatim and in place: the run before a tag is one
    /// chip (commas intact), the tag is its own chip, and the run after is its own chip — none split, none alphabetized,
    /// none pushed to the end.</summary>
    [Fact]
    public void Plain_prose_inside_a_tag_prompt_stays_verbatim_and_in_place()
    {
        var chips = Card("a knight in the rain, holding a sword, long hair, at night", ("long_hair", "tag")).Chips;

        Assert.Equal(3, chips.Count);
        Assert.Equal(("a knight in the rain, holding a sword", (string?)null), (chips[0].Text, chips[0].Kind));
        Assert.Equal(("long hair", "tag"), (chips[1].Text, chips[1].Kind));
        Assert.Equal(("at night", (string?)null), (chips[2].Text, chips[2].Kind));
    }

    /// <summary>A blank prompt renders the single "(no prompt)" placeholder chip.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_prompt_shows_the_no_prompt_placeholder(string prompt)
    {
        var chip = Assert.Single(Card(prompt).Chips);
        Assert.Equal("(no prompt)", chip.Text);
    }
}
