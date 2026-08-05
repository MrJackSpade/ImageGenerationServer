using ImageGen.Application.Rendering;
using ImageGen.Application.Workflows;
using ImageGen.Domain;

namespace ImageGen.Tests;

/// <summary>
/// The #91 contract, pinned end to end: comma-segment text management in <see cref="PromptFinalizer.Finalize"/> is
/// gated STRICTLY behind the model's tagging block. A model that speaks no tags — <c>tg is null</c>, or a block with
/// <c>Tags == false &amp;&amp; Artists == false</c> — receives its prompt BYTE-FOR-BYTE: comma is sentence punctuation to
/// it, not a tag delimiter, so nothing is split, filtered, or re-joined on commas (not marker stripping, not underscore
/// folding, and not '~' guide removal). A model that speaks tags OR artists is where all of that applies.
///
/// These tests assert the CORRECT behavior. Where the current code still runs comma management ahead of the non-tag
/// gate (guide-tag removal via <c>PromptMarkers.WithoutGuides</c>), the non-tag cases carrying a '~' fail — which is the
/// reproduction, not a flaky test.
/// </summary>
public sealed class PromptFinalizerGatingTests
{
    /// <summary>The two ways a model declares it speaks no tags. Both must take the identical verbatim path.</summary>
    private static readonly WorkflowTagging?[] NonTag =
    [
        null,
        new WorkflowTagging(Tags: false, Artists: false, KeepArtistMarker: false, UnderscoresToSpaces: false),
    ];

    /// <summary>Anima-style block: tags + artists, keep '@', fold underscores (score_ excepted).</summary>
    private static readonly WorkflowTagging Anima = new(Tags: true, Artists: true, KeepArtistMarker: true, UnderscoresToSpaces: true);

    /// <summary>A block that speaks tags but not artists.</summary>
    private static readonly WorkflowTagging TagsOnly = new(Tags: true, Artists: false, KeepArtistMarker: false, UnderscoresToSpaces: false);

    /// <summary>A block that speaks artists but not tags.</summary>
    private static readonly WorkflowTagging ArtistsOnly = new(Tags: false, Artists: true, KeepArtistMarker: false, UnderscoresToSpaces: false);

    /// <summary>A natural-language prompt — commas as sentence punctuation, no markers — reaches a non-tag model exactly
    /// as typed. It is neither split nor re-joined on commas.</summary>
    [Theory]
    [InlineData("a lone dog on a hill")]                                     // no comma at all
    [InlineData("a wide shot of a castle, at dusk, dramatic lighting")]      // commas as prose
    [InlineData("portrait of a woman, 35mm lens, f/1.8, soft light")]        // realistic photographer prose
    [InlineData("a,b , c,  d")]                                              // irregular inter-comma spacing (NOT normalized to ", ")
    [InlineData("line one,\nline two")]                                      // a newline the reflow would have eaten
    [InlineData("  padded on both sides  ")]                                 // leading/trailing whitespace preserved
    [InlineData("1girl,")]                                                   // a trailing comma the reflow would have dropped
    [InlineData("a,,b")]                                                     // an empty middle segment the reflow would have dropped
    public void Non_tag_model_returns_a_plain_prompt_byte_for_byte(string prompt)
    {
        foreach (WorkflowTagging? tg in NonTag)
        {
            FinalizedPrompt f = PromptFinalizer.Finalize(prompt, tg);
            Assert.Equal(prompt, f.Rendered);
            Assert.Empty(f.Marks);   // nothing about a non-tag prompt is bookmarkable
        }
    }

    /// <summary>Markers are LITERAL TEXT to a non-tag model: '#', '@' and '!' are not stripped, and underscores are not
    /// folded to spaces. Only a tag model reads those as markers.</summary>
    [Theory]
    [InlineData("#literal, @plain, !bang")]        // '#'/'@'/'!' survive verbatim
    [InlineData("long_hair, blue_eyes")]           // underscores are NOT folded to spaces
    [InlineData("a &#039; b")]                     // an HTML entity's '#' is untouched too
    public void Non_tag_model_treats_markers_and_underscores_as_literal_text(string prompt)
    {
        foreach (WorkflowTagging? tg in NonTag)
            Assert.Equal(prompt, PromptFinalizer.Finalize(prompt, tg).Rendered);
    }

    /// <summary>The ticket's exact reproduction and its family: a comma segment BEGINNING with '~' must survive verbatim
    /// for a non-tag model. '~' means "guide tag" only inside the tagging gate; to a natural-language model it is an
    /// ordinary character. (Currently the guide strip runs ahead of the gate and deletes these segments.)</summary>
    [Theory]
    [InlineData("wide shot, ~5 meters away, dusk")]                 // ticket's example -> must NOT become "wide shot, dusk"
    [InlineData("~5 meters away, dusk")]                            // '~' segment first
    [InlineData("dusk, ~5 meters away")]                            // '~' segment last
    [InlineData("a, ~b, ~c, d")]                                    // several '~' segments
    [InlineData("a,  ~b, c")]                                       // whitespace before the '~' still leads the segment
    [InlineData("portrait, ~35mm lens, f/1.8, #nofilter")]         // '~' and '#' both as literal prose
    [InlineData("~a, ~b")]                                          // every segment starts with '~'
    [InlineData("~")]                                               // a lone '~'
    public void Non_tag_model_keeps_a_tilde_led_segment_verbatim(string prompt)
    {
        foreach (WorkflowTagging? tg in NonTag)
            Assert.Equal(prompt, PromptFinalizer.Finalize(prompt, tg).Rendered);
    }

    /// <summary>A '~' that is not at a segment's start is part of the word for every model — so a non-tag prompt with an
    /// interior '~' is verbatim (this already holds; pinned so a fix for the leading case can't regress it).</summary>
    [Theory]
    [InlineData("x~y interior, z")]
    [InlineData("hoshino_ruby~, plain phrase")]
    public void Non_tag_model_leaves_an_interior_tilde_alone(string prompt)
    {
        foreach (WorkflowTagging? tg in NonTag)
            Assert.Equal(prompt, PromptFinalizer.Finalize(prompt, tg).Rendered);
    }

    /// <summary>The ticket calls out BOTH the positive and the negative: a non-tag model's negative box is passed
    /// through verbatim as well (same method, same gate). A '~' segment in the negative must survive.</summary>
    [Fact]
    public void Non_tag_model_passes_a_negative_prompt_through_verbatim()
    {
        const string neg = "blurry, ~watermark, low quality";
        foreach (WorkflowTagging? tg in NonTag)
            Assert.Equal(neg, PromptFinalizer.Finalize(neg, tg).Rendered);
    }

    /// <summary>Null maps to empty; empty and whitespace-only prompts survive unchanged; a non-tag prompt has no
    /// marks. (Edges the ticket does not spell out.)</summary>
    [Fact]
    public void Non_tag_model_maps_null_to_empty_and_preserves_empty_and_whitespace()
    {
        foreach (WorkflowTagging? tg in NonTag)
        {
            Assert.Equal("", PromptFinalizer.Finalize(null, tg).Rendered);
            Assert.Equal("", PromptFinalizer.Finalize("", tg).Rendered);
            Assert.Equal("   ", PromptFinalizer.Finalize("   ", tg).Rendered);
            Assert.Empty(PromptFinalizer.Finalize(null, tg).Marks);
        }
    }

    /// <summary>A block that speaks neither tags nor artists is a NON-tag model — it takes the identical verbatim path as
    /// a null block. Pinned on the reproduction prompt so the two answers must agree AND must be the untouched input.</summary>
    [Fact]
    public void A_block_with_neither_tags_nor_artists_matches_the_null_non_tag_path()
    {
        const string p = "wide shot, ~5 meters away, dusk";
        WorkflowTagging neither = new WorkflowTagging(Tags: false, Artists: false, KeepArtistMarker: false, UnderscoresToSpaces: false);

        Assert.Equal(PromptFinalizer.Finalize(p, null).Rendered, PromptFinalizer.Finalize(p, neither).Rendered);
        Assert.Equal(p, PromptFinalizer.Finalize(p, neither).Rendered);   // ...and that shared answer is verbatim
    }

    /// <summary>Turning on EITHER half of the block makes it a tag model — comma management applies, so a '~' guide is
    /// dropped. This is the far side of the gate and must stay working.</summary>
    [Theory]
    [InlineData(true, false)]    // tags-only
    [InlineData(false, true)]    // artists-only
    [InlineData(true, true)]     // both
    public void Any_block_that_speaks_tags_or_artists_is_a_tag_model_and_drops_a_guide(bool tags, bool artists)
    {
        WorkflowTagging tg = new WorkflowTagging(tags, artists, KeepArtistMarker: false, UnderscoresToSpaces: false);
        Assert.Equal("wide shot, dusk", PromptFinalizer.Finalize("wide shot, ~5 meters away, dusk", tg).Rendered);
    }

    /// <summary>The whole ticket in one assertion: the SAME '~' prompt is dropped for a tag model and kept for a non-tag
    /// model. First line already holds; second is the fix's target.</summary>
    [Fact]
    public void The_same_tilde_prompt_is_dropped_for_a_tag_model_but_kept_for_a_non_tag_model()
    {
        const string p = "wide shot, ~5 meters away, dusk";
        Assert.Equal("wide shot, dusk", PromptFinalizer.Finalize(p, Anima).Rendered);   // tag model: guide removed
        Assert.Equal(p, PromptFinalizer.Finalize(p, null).Rendered);                    // non-tag model: verbatim
    }

    /// <summary>A tag model still strips the leading '#', keeps '@' when the model documents it, and folds underscores to
    /// spaces (score_ excepted) — and records the marks. Unchanged by the gating fix.</summary>
    [Fact]
    public void Tag_model_strips_markers_folds_underscores_and_records_marks()
    {
        FinalizedPrompt f = PromptFinalizer.Finalize("#bad_anatomy, @some_artist, score_1", Anima);

        Assert.Equal("bad anatomy, @some artist, score_1", f.Rendered);   // '#' gone, '@' kept, '_' folded, score_ kept
        Assert.Equal(TokenKinds.Tag, f.Marks["bad_anatomy"]);
        Assert.Equal(TokenKinds.Artist, f.Marks["some_artist"]);
    }

    /// <summary>A tag model drops a '~' guide from the render and does not mark it, while an inert '!' stays in the
    /// picture as a plain tag — the subject-swap shape. Unchanged by the gating fix.</summary>
    [Fact]
    public void Tag_model_drops_a_guide_and_keeps_an_inert_tag()
    {
        FinalizedPrompt f = PromptFinalizer.Finalize("!1girl, ~1boy, #castle", Anima);

        Assert.Equal("1girl, castle", f.Rendered);
        Assert.False(f.Marks.ContainsKey("1boy"));
        Assert.Equal(TokenKinds.Tag, f.Marks["1girl"]);
        Assert.Equal(TokenKinds.Tag, f.Marks["castle"]);
    }

    /// <summary>Tags-only and artists-only blocks are both tag models: they strip markers on the same comma-split path
    /// (here '@' is dropped because neither documents it). Pinned so the gate flips on tags-OR-artists, not tags-AND.</summary>
    [Theory]
    [MemberData(nameof(SingleAxisTagModels))]
    public void A_single_axis_tag_model_still_strips_markers(WorkflowTagging tg)
    {
        Assert.Equal("foo, bar", PromptFinalizer.Finalize("#foo, @bar", tg).Rendered);
    }

    /// <summary>The tags-only and artists-only blocks, for <see cref="A_single_axis_tag_model_still_strips_markers"/>.</summary>
    public static TheoryData<WorkflowTagging> SingleAxisTagModels() => new() { TagsOnly, ArtistsOnly };
}
