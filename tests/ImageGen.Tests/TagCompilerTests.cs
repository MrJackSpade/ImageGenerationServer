using ImageGen.Application.Prompting.Tags;
using ImageGen.Application.Rendering;
using ImageGen.Application.Workflows;
using ImageGen.Domain;

namespace ImageGen.Tests;

/// <summary>
/// Issue #157's layered compiler, tested at EACH stage independently: the unwrap (text → typed tags with kind/strength/
/// ordinal), the group generation (<c>[a|b]</c> choice + <c>{a|b}</c> explode → resolved combos), and the per-model
/// rendering. Randomness is injected so choice resolution is deterministic in the tests.
/// </summary>
public sealed class TagCompilerTests
{
    private static readonly WorkflowTagging Booru = new(Tags: true, Artists: true, KeepArtistMarker: false, UnderscoresToSpaces: false);


    [Theory]
    [InlineData("#long_hair", TagKind.Tag, "long_hair")]
    [InlineData("@greg_rutkowski", TagKind.Artist, "greg_rutkowski")]
    [InlineData("!pig", TagKind.Inert, "pig")]
    [InlineData("~1boy", TagKind.Guide, "1boy")]
    [InlineData("plain phrase", TagKind.Plain, "plain_phrase")]
    public void A_segment_unwraps_to_its_kind_and_key(string seg, TagKind kind, string key)
    {
        ParsedTag t = One(seg);
        Assert.Equal(kind, t.Kind);
        Assert.Equal(key, t.Key);
    }

    [Theory]
    [InlineData("#(long_hair:1.2)", 1.2)]
    [InlineData("(#long_hair:1.2)", 1.2)]   // strength is read wherever the marker sits
    [InlineData("(long_hair)", 1.1)]        // emphasis is ×1.1
    [InlineData("long_hair", 1.0)]          // no emphasis
    public void A_tags_strength_is_read_from_its_weight(string seg, double strength) =>
        Assert.Equal(strength, One(seg).Emphasis.Weight, 3);

    [Fact]
    public void Ordinals_are_dense_and_in_order()
    {
        IReadOnlyList<ParsedTag> tags = TagParser.Parse("#a, , #b, #c");   // the empty segment is dropped
        Assert.Equal([0, 1, 2], tags.Select(t => t.Ordinal));
        Assert.Equal(["a", "b", "c"], tags.Select(t => t.Key));
    }

    [Fact]
    public void The_base_text_keeps_its_casing_for_rendering_while_the_key_is_canonical()
    {
        ParsedTag t = One("#(Long Hair:1.2)");
        Assert.Equal("Long Hair", t.BaseText);   // rendered as typed
        Assert.Equal("long_hair", t.Key);        // matched canonically
    }


    [Fact]
    public void An_explode_group_multiplies_into_one_combo_per_option()
    {
        IReadOnlyList<GeneratedTagGroup> gs = TagGroup.Parse("{red|blue} hair").Generate();
        Assert.Equal(["red hair", "blue hair"], gs.Select(g => g.RawResolved));
    }

    [Fact]
    public void Explode_groups_take_the_cartesian_product()
    {
        IReadOnlyList<GeneratedTagGroup> gs = TagGroup.Parse("{a|b}, {c|d}").Generate();
        Assert.Equal(["a, c", "a, d", "b, c", "b, d"], gs.Select(g => g.RawResolved));
        Assert.Equal(4, TagGroup.Parse("{a|b}, {c|d}").ComboCount);
    }

    [Theory]
    [InlineData(0, "x hair")]
    [InlineData(1, "y hair")]
    public void A_choice_group_picks_one_option(int chosen, string expected) =>
        Assert.Equal(expected, Assert.Single(TagGroup.Parse("[x|y] hair").Generate(_ => chosen)).RawResolved);

    [Fact]
    public void A_choice_does_not_multiply_the_combos() => Assert.Equal(1, TagGroup.Parse("[x|y|z]").ComboCount);

    [Fact]
    public void A_choice_inside_each_explode_combo_is_resolved_independently()
    {
        int call = 0;
        // First combo picks option 0 (x), second picks option 1 (y) — independent per combo, matching the old client.
        IReadOnlyList<GeneratedTagGroup> gs = TagGroup.Parse("{a|b} [x|y]").Generate(_ => call++);
        Assert.Equal(["a x", "b y"], gs.Select(g => g.RawResolved));
    }

    [Fact]
    public void A_choice_nested_inside_an_explode_option_resolves_after_the_explode()
    {
        IReadOnlyList<GeneratedTagGroup> gs = TagGroup.Parse("{a|[x|y]}").Generate(_ => 0);
        Assert.Equal(["a", "x"], gs.Select(g => g.RawResolved));   // combo 'a' verbatim; combo '[x|y]' picks x
    }

    [Theory]
    [InlineData("[long_hair]")]   // de-emphasis — no top-level '|', so NOT a choice group
    [InlineData("{solo}")]        // no '|' — not an explode group
    [InlineData("a plain, #tag")]
    public void Text_with_no_alternation_yields_exactly_one_verbatim_combo(string raw)
    {
        GeneratedTagGroup g = Assert.Single(TagGroup.Parse(raw).Generate());
        Assert.Equal(raw, g.RawResolved);
    }


    [Fact]
    public void A_resolved_group_renders_for_both_models()
    {
        GeneratedTagGroup g = Assert.Single(TagGroup.Parse("#1girl, !pig, ~1boy, @greg_rutkowski").Generate());

        Assert.Equal("1girl, pig, greg_rutkowski", g.ToImageModel(Booru));   // guide gone, markers stripped
        (string seed, HashSet<string> suppress) = g.ToTagModel(Booru);
        Assert.Equal("1girl, 1boy", seed);                                    // artist+inert dropped, guide kept as a tag
        Assert.Equal(["1boy", "pig"], suppress.Order());
        Assert.Equal(TokenKinds.Artist, g.Marks(Booru)["greg_rutkowski"]);
    }

    [Fact]
    public void Generate_then_render_matches_a_hand_expanded_explode()
    {
        // The whole pipeline: '{a|b}' explodes, each combo renders as the image model would see it.
        List<string> images = [.. TagGroup.Parse("#1girl, {#red_hair|#blue_hair}").Generate().Select(g => g.ToImageModel(Booru))];
        Assert.Equal(["1girl, red_hair", "1girl, blue_hair"], images);
    }

    /// <summary>Enqueue-time cutover: a generate item's `{a|b}` fans into one render item per combo, resolved.</summary>
    [Fact]
    public void Enqueue_expands_an_explode_prompt_into_one_item_per_combo()
    {
        IReadOnlyList<RenderItem> items = RenderOrchestrator.ExpandGenerateGroups(
            [RenderItem.ForGenerate(new GenerateSpec("wf", "#1girl, {#red|#blue}", null, "square"))]);

        Assert.Equal(["#1girl, #red", "#1girl, #blue"], items.Select(i => i.Gen?.Prompt));
    }

    /// <summary>A group-free generate item is passed through untouched (no needless re-parse).</summary>
    [Fact]
    public void Enqueue_leaves_a_group_free_generate_item_untouched()
    {
        RenderItem one = RenderItem.ForGenerate(new GenerateSpec("wf", "#1girl, #solo", null, "square"));
        Assert.Same(one, Assert.Single(RenderOrchestrator.ExpandGenerateGroups([one])));
    }

    /// <summary>Edit items are never group-expanded — only generate prompts fan into slots.</summary>
    [Fact]
    public void Enqueue_never_expands_an_edit_item()
    {
        RenderItem edit = RenderItem.ForEdit(new EditSpec("wf", "make it [red|blue]", "img1"));
        Assert.Same(edit, Assert.Single(RenderOrchestrator.ExpandGenerateGroups([edit])));
    }

    /// <summary>#166: an inert ('!') tag renders to the image model but is kept ENTIRELY out of the tag model — never
    /// seeded, and added to the suppress set so the predictor cannot echo it back. A guide ('~') is the mirror: seeded
    /// as a tag yet also suppressed. An artist is never seeded.</summary>
    [Fact]
    public void Inert_and_guide_tags_are_kept_out_of_the_tag_model_seed_or_suppressed()
    {
        GeneratedTagGroup g = Assert.Single(TagGroup.Parse("#1girl, !solo, ~1boy, @artgerm").Generate());
        (string seed, HashSet<string> suppress) = g.ToTagModel(Booru);

        Assert.Equal("1girl, 1boy", seed);       // guide kept as a seed tag; inert + artist never seeded
        Assert.DoesNotContain("solo", seed);     // the inert key is NOT in the seed
        Assert.DoesNotContain("artgerm", seed);  // the artist is not seeded
        Assert.Contains("solo", suppress);       // inert IS suppressed
        Assert.Contains("1boy", suppress);       // guide IS suppressed
    }

    /// <summary>#166: the orchestrator's random-prompt path unions the inert/guide suppress keys into the banned-tags
    /// set it hands to <c>ITagModelClient.GenerateAsync</c> — so nothing between the seed build and the model call can
    /// re-introduce a tag the seed deliberately hid.</summary>
    [Fact]
    public void Random_prompt_bans_the_inert_and_guide_keys_it_hid_from_the_seed()
    {
        (string seed, HashSet<string> suppress) = RenderOrchestrator.TagSeed("#1girl, !solo, ~1boy, @artgerm", Booru);
        static HashSet<string> Empty() => new(StringComparer.Ordinal);

        HashSet<string> banned = RenderOrchestrator.RandomPromptBannedTags((Empty(), Empty()), (Empty(), Empty()), suppress);

        Assert.Contains("solo", banned);       // inert reaches the banned set passed to GenerateAsync
        Assert.Contains("1boy", banned);       // guide too
        Assert.DoesNotContain("solo", seed);   // and it stayed out of the seed
        Assert.Contains("1boy", seed);         // while the guide is seeded as a tag
    }

    /// <summary>Parse one segment, failing the test if it is empty.</summary>
    private static ParsedTag One(string seg) => TagParser.ParseSegment(seg, 0) ?? throw new Xunit.Sdk.XunitException($"'{seg}' parsed to nothing");
}
