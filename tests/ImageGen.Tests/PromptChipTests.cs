using ImageGen.Web.ViewModels;

namespace ImageGen.Tests;

/// <summary>
/// The card's prompt chips. A marked tag/artist becomes an interactive chip whose Key is the canonical underscored name
/// the bookmark/ban store keys on and whose Kind is exactly "tag"/"artist"; everything else is plain natural-language
/// text. The tag chips are GROUPED ahead of the prose and ordered by state (bookmarked, untouched, banned), then type
/// (artist, meta, copyright, character, general, deprecated), then name — so a tag lands in the same place on every card.
/// That ordering is tag-only: the plain prose is kept VERBATIM (consecutive plain segments stay one chip, never split on
/// their commas, never alphabetized) and emitted as the final group, in the order its runs were written.
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

    /// <summary>A marked chip shows its display text and keys on the canonical token. Chips group ahead of the prose and
    /// order by type (artist before general), and the trailing prose is its own plain chip, last.</summary>
    [Fact]
    public void A_marked_chip_shows_display_text_keys_on_canonical_token_and_groups_before_prose()
    {
        IReadOnlyList<PromptChip> chips = Card("bad anatomy, some artist, a plain phrase", ("bad_anatomy", "tag"), ("some_artist", "artist")).Chips;

        Assert.Equal(3, chips.Count);
        Assert.Equal(("some artist", "artist", "some_artist"), (chips[0].Text, chips[0].Kind, chips[0].Key));   // artist type sorts first
        Assert.Equal(("bad anatomy", "tag", "bad_anatomy"), (chips[1].Text, chips[1].Kind, chips[1].Key));      // then the general tag
        Assert.Equal("a plain phrase", chips[2].Text);
        Assert.Null(chips[2].Kind);   // plain text: not a token, and it groups last
    }

    /// <summary>Models with keep_artist_marker leave '@' in the stored prompt; the key must not carry it.</summary>
    [Fact]
    public void An_artist_that_kept_its_marker_still_keys_on_the_bare_token()
    {
        IReadOnlyList<PromptChip> chips = Card("@some artist", ("some_artist", "artist")).Chips;

        Assert.Equal("some_artist", chips[0].Key);
        Assert.Equal("artist", chips[0].Kind);
    }

    /// <summary>score_ tags keep the underscores the finalizer preserved; the trailing plain word is its own chip, last.</summary>
    [Fact]
    public void Score_tags_keep_the_underscores_the_finalizer_preserved()
    {
        IReadOnlyList<PromptChip> chips = Card("score_9, masterpiece", ("score_9", "tag")).Chips;

        Assert.Equal(("score_9", "tag", "score_9"), (chips[0].Text, chips[0].Kind, chips[0].Key));
        Assert.Null(chips[1].Kind);
    }

    /// <summary>State orders the chips: a bookmarked chip leads and a banned chip trails the untouched tags — but both are
    /// still chips and stay ahead of any prose.</summary>
    [Fact]
    public void Bookmarked_leads_and_banned_trails_within_the_chip_group()
    {
        ImageDetailViewModel vm = new()
        {
            Entry = new ImageDetailView("img1", "bad anatomy, greg rutkowski", "Anima", "anima", "square", DateTime.UtcNow,
                new Dictionary<string, string> { ["bad_anatomy"] = "tag", ["greg_rutkowski"] = "artist" }),
            MarkerPrompt = "",
            IsBookmarked = false,
            BannedTags = new HashSet<string>(StringComparer.Ordinal) { "bad_anatomy" },
            BookmarkedArtists = new HashSet<string>(StringComparer.Ordinal) { "greg_rutkowski" },
        };

        IReadOnlyList<PromptChip> chips = vm.Chips;
        Assert.Equal("greg_rutkowski", chips[0].Key);   // bookmarked leads
        Assert.True(chips[0].Bookmarked);
        Assert.False(chips[0].Banned);
        Assert.Equal("bad_anatomy", chips[1].Key);      // banned trails
        Assert.True(chips[1].Banned);
        Assert.False(chips[1].Bookmarked);
    }

    /// <summary>The #100 regression: a banned tag and a plain phrase together — the banned chip is still a chip and
    /// precedes the plain-text chip, which groups last. (Before, banned sorted into a state past the NL text.)</summary>
    [Fact]
    public void A_banned_tag_precedes_the_plain_text_chip()
    {
        ImageDetailViewModel vm = new()
        {
            Entry = new ImageDetailView("img1", "bad anatomy, a plain phrase", "Anima", "anima", "square", DateTime.UtcNow,
                new Dictionary<string, string> { ["bad_anatomy"] = "tag" }),
            MarkerPrompt = "",
            IsBookmarked = false,
            BannedTags = new HashSet<string>(StringComparer.Ordinal) { "bad_anatomy" },
        };

        IReadOnlyList<PromptChip> chips = vm.Chips;
        Assert.Equal(2, chips.Count);
        Assert.Equal("bad_anatomy", chips[0].Key);   // the banned chip comes first
        Assert.True(chips[0].Banned);
        Assert.Equal("a plain phrase", chips[1].Text);
        Assert.Null(chips[1].Kind);                   // the NL prose is last
    }

    /// <summary>Chips order by state then type, and the prose is grouped LAST regardless of where it was typed: leading
    /// plain text is pushed behind the tags, and a bookmarked tag jumps to the head.</summary>
    [Fact]
    public void Chips_group_before_prose_and_order_by_state_then_type()
    {
        ImageDetailViewModel vm = new()
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
                ["old_tag"] = 6,       // deprecated
                ["smile"] = 0,         // general
                ["some_girl"] = 4,     // character
                ["some_show"] = 3,     // copyright
                ["absurdres"] = 5,     // meta
                ["starred_tag"] = 0,   // general
            },
            BookmarkedTags = new HashSet<string>(StringComparer.Ordinal) { "starred_tag" },
        };

        Assert.Equal(
            // bookmarked first (starred tag); then untouched by type: artist, meta, copyright, character, general,
            // deprecated; then the prose last.
            ["starred tag", "some artist", "absurdres", "some show", "some girl", "smile", "old tag", "a plain phrase"],
            vm.Chips.Select(c => c.Text));
        Assert.True(vm.Chips[0].Bookmarked);   // bookmarked jumps to the head
        Assert.Null(vm.Chips[^1].Kind);        // the leading prose is pushed to the very end
    }

    /// <summary>Banned tags trail the untouched tags but still lead the prose, and prose typed in the MIDDLE is pulled out
    /// to the end as its own verbatim chip.</summary>
    [Fact]
    public void Banned_chips_trail_the_tags_and_prose_is_pulled_to_the_end()
    {
        ImageDetailViewModel vm = new()
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
                ["smile"] = 0,         // general
                ["banned_meta"] = 5,   // meta
                ["starred_tag"] = 0,   // general
            },
            BannedArtists = new HashSet<string>(StringComparer.Ordinal) { "banned_artist" },
            BannedTags = new HashSet<string>(StringComparer.Ordinal) { "banned_meta" },
            BookmarkedTags = new HashSet<string>(StringComparer.Ordinal) { "starred_tag" },
        };

        Assert.Equal(
            // bookmarked (starred tag); untouched general (smile); then banned by type (artist before meta); prose last.
            ["starred tag", "smile", "banned artist", "banned meta", "a plain phrase"],
            vm.Chips.Select(c => c.Text));
        Assert.True(vm.Chips[0].Bookmarked);
        Assert.True(vm.Chips[2].Banned);   // banned artist trails the untouched tag
        Assert.True(vm.Chips[3].Banned);
        Assert.Null(vm.Chips[^1].Kind);    // the middle prose is pulled to the end
    }

    /// <summary>Chips of the same type ARE alphabetized by canonical name — the ordering is a property of the token, so a
    /// tag sits in the same spot on every card.</summary>
    [Fact]
    public void Chips_of_the_same_type_are_ordered_by_name()
    {
        ImageDetailViewModel vm = new()
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
            // artists first (by name), then the general tags (by name)
            ["alice artist", "zoe artist", "apron", "smile", "zebra print"],
            vm.Chips.Select(c => c.Text));
    }

    /// <summary>A non-tag (natural-language) prompt has no marks, so the WHOLE prompt is one plain chip, verbatim — not
    /// split on its commas and not reordered. This is the reported FLUX repro: "this is a, test prompt".</summary>
    [Fact]
    public void A_non_tag_prompt_renders_as_one_verbatim_chip()
    {
        PromptChip chip = Assert.Single(Card("this is a, test prompt").Chips);

        Assert.Equal("this is a, test prompt", chip.Text);
        Assert.Null(chip.Kind);
    }

    /// <summary>Longer non-tag prose with several commas is still one verbatim chip.</summary>
    [Fact]
    public void A_non_tag_prose_prompt_is_not_split_or_reordered()
    {
        PromptChip chip = Assert.Single(Card("a wide shot, at dusk, dramatic lighting").Chips);

        Assert.Equal("a wide shot, at dusk, dramatic lighting", chip.Text);
    }

    /// <summary>Plain prose runs mixed INTO a booru prompt stay verbatim (commas intact, never split, never alphabetized)
    /// and group after the tag; separate runs keep the order they were written in.</summary>
    [Fact]
    public void Plain_prose_runs_stay_verbatim_and_group_after_the_tags()
    {
        IReadOnlyList<PromptChip> chips = Card("a knight in the rain, holding a sword, long hair, at night", ("long_hair", "tag")).Chips;

        Assert.Equal(3, chips.Count);
        Assert.Equal(("long hair", "tag"), (chips[0].Text, chips[0].Kind));                                       // the tag leads
        Assert.Equal(("a knight in the rain, holding a sword", null), (chips[1].Text, chips[1].Kind));   // first prose run, verbatim
        Assert.Equal(("at night", null), (chips[2].Text, chips[2].Kind));                                // second prose run, in written order
    }

    /// <summary>A blank prompt renders the single "(no prompt)" placeholder chip.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_prompt_shows_the_no_prompt_placeholder(string prompt)
    {
        PromptChip chip = Assert.Single(Card(prompt).Chips);
        Assert.Equal("(no prompt)", chip.Text);
    }
}
