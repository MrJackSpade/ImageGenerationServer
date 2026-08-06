using ImageGen.Application.Prompting;
using ImageGen.Application.Rendering;
using ImageGen.Application.Workflows;
using ImageGen.Domain;

namespace ImageGen.Tests;

/// <summary>
/// Issue #157: one definitive parse of a tag, exhaustively covered. For ANY way a user can write a tag — marker
/// (none/#/@/!/~) × weight/emphasis (none/(t)/(t:w)/[t]/nested/escaped) × marker POSITION relative to the wrapper
/// (outside <c>#(t:1.2)</c> vs inside <c>(#t:1.2)</c>) — these pin BOTH outputs at once through the single parse API
/// <see cref="PromptParse.Analyze"/>: the image-model prompt (weights kept, markers removed, guides removed) AND the
/// tag-model routing (seed, inert/guide exclusion, marks). The negative-prompt suppression (#/~/! from #134) is pinned
/// through <see cref="PromptFinalizer.NegativeKeys"/>.
///
/// <para>Definitive format: the canonical spelling of a weighted, marked tag is the marker OUTSIDE the wrapper
/// (<c>#(tag:1.2)</c>). The marker-inside spelling (<c>(#tag:1.2)</c>), nested weights, <c>[...]</c> de-emphasis and
/// escaped brackets are all accepted and parse IDENTICALLY — the key, the image-model prompt and the tag-model tags
/// never disagree for the same input, whatever the nesting.</para>
/// </summary>
public sealed class TagSyntaxTests
{
    private static readonly WorkflowTagging Booru = new(Tags: true, Artists: true, KeepArtistMarker: false, UnderscoresToSpaces: false);
    private static readonly WorkflowTagging Anima = new(Tags: true, Artists: true, KeepArtistMarker: true, UnderscoresToSpaces: true);


    /// <summary>Every spelling of a weighted '#' tag routes the same: base tag seeds/marks, weight-kept-marker-stripped
    /// form renders. This is the divergence #157 fixes — position-0 detection missed the marker-inside spelling.</summary>
    [Theory]
    [InlineData("#(furry_male:1.2)")]      // marker OUTSIDE the wrapper — canonical
    [InlineData("(#furry_male:1.2)")]      // marker INSIDE the wrapper — the bug
    [InlineData("#(furry_male)")]          // emphasis, no explicit weight
    [InlineData("(#furry_male)")]
    [InlineData("#[furry_male]")]          // de-emphasis
    [InlineData("([#furry_male]:1.2)")]    // marker inside nested wrappers
    public void Every_weighted_hash_spelling_routes_to_the_base_tag(string raw)
    {
        PromptAnalysis a = PromptParse.Analyze(raw, Booru);

        Assert.Equal(TokenKinds.Tag, a.Marks["furry_male"]);        // marked under the base tag, marker wherever it sat
        Assert.DoesNotContain('#', a.ImageModelPrompt);             // never leaks to the image model
        Assert.Contains("furry_male", a.ImageModelPrompt);          // the tag itself still renders (with its weight)
        Assert.Contains("furry_male", a.TagModelSeed);              // and seeds the predictor
    }

    /// <summary>The weight/emphasis the user typed survives into the image prompt unchanged; only the marker is removed.</summary>
    [Theory]
    [InlineData("#(long_hair:1.2)", "(long_hair:1.2)")]
    [InlineData("(#long_hair:1.2)", "(long_hair:1.2)")]
    [InlineData("#[long_hair]", "[long_hair]")]
    [InlineData("((#long_hair:1.1):1.2)", "((long_hair:1.1):1.2)")]
    public void The_image_prompt_keeps_the_weight_and_drops_only_the_marker(string raw, string rendered) =>
        Assert.Equal(rendered, PromptParse.Analyze(raw, Booru).ImageModelPrompt);


    /// <summary>'!' inert: renders (marker stripped), marks as a tag, and is hidden from the predictor's seed — however
    /// it is weighted, and wherever the marker sits.</summary>
    [Theory]
    [InlineData("!pig")]
    [InlineData("!(pig:1.3)")]
    [InlineData("(!pig:1.3)")]
    public void An_inert_tag_renders_marks_and_is_hidden_from_the_seed(string raw)
    {
        PromptAnalysis a = PromptParse.Analyze(raw, Booru);

        Assert.Contains("pig", a.ImageModelPrompt);
        Assert.DoesNotContain('!', a.ImageModelPrompt);
        Assert.Equal(TokenKinds.Tag, a.Marks["pig"]);
        Assert.Contains("pig", a.InertKeys);
        Assert.DoesNotContain("pig", a.TagModelSeed);   // subtracted from the seed
    }

    /// <summary>'~' guide: never reaches the image model, is NOT marked, but seeds the predictor — however weighted,
    /// wherever the marker sits.</summary>
    [Theory]
    [InlineData("~feet")]
    [InlineData("~(feet:1.1)")]
    [InlineData("(~feet:1.1)")]
    public void A_guide_tag_seeds_the_predictor_but_never_renders_or_marks(string raw)
    {
        PromptAnalysis a = PromptParse.Analyze(raw, Booru);

        Assert.Equal("", a.ImageModelPrompt);           // dropped from the image model entirely
        Assert.False(a.Marks.ContainsKey("feet"));      // not in the picture, so never marked
        Assert.Contains("feet", a.GuideKeys);
        Assert.Contains("feet", a.TagModelSeed);         // it DOES seed the predictor (as a '#' tag)
    }

    /// <summary>'@' artist inside a weight wrapper marks as an artist and honours the model's keep-'@' rule.</summary>
    [Theory]
    [InlineData("(@greg_rutkowski:1.1)")]
    [InlineData("@(greg_rutkowski:1.1)")]
    public void A_weighted_artist_marks_as_an_artist(string raw)
    {
        Assert.Equal(TokenKinds.Artist, PromptParse.Analyze(raw, Booru).Marks["greg_rutkowski"]);
        Assert.Equal(TokenKinds.Artist, PromptParse.Analyze(raw, Anima).Marks["greg_rutkowski"]);
    }

    /// <summary>Anima keeps the '@' and folds underscores even when the marker sits inside a weight wrapper.</summary>
    [Fact]
    public void Anima_keeps_the_artist_marker_inside_a_wrapper() =>
        Assert.Equal("(@greg rutkowski:1.1)", PromptParse.Analyze("(@greg_rutkowski:1.1)", Anima).ImageModelPrompt);


    /// <summary>A booru tag that natively carries brackets or a marker char (not at position 0, not a whole-segment
    /// wrapper) is left exactly alone — no phantom marker, no phantom weight peel.</summary>
    [Theory]
    [InlineData("hatsune_miku_(vocaloid)", "hatsune_miku_(vocaloid)")]   // trailing native parens, no marker
    [InlineData("(a)_(b)", "(a)_(b)")]                                   // first '(' closes mid-string, not a wrapper
    [InlineData("re:zero", "re:zero")]                                   // ':' outside a wrapper is not a weight
    public void Native_brackets_are_left_alone(string raw, string rendered)
    {
        PromptAnalysis a = PromptParse.Analyze(raw, Booru);
        Assert.Equal(rendered, a.ImageModelPrompt);
        Assert.Empty(a.Marks);   // unmarked plain tags aren't marked
    }

    /// <summary>An escaped '\('/'\)' is a literal bracket, not a weight wrapper: the '#' is still recognised and stripped
    /// (it doesn't leak), and the escaped tag renders with its escape intact.</summary>
    [Fact]
    public void An_escaped_bracket_is_a_literal_not_a_wrapper()
    {
        PromptAnalysis a = PromptParse.Analyze(@"#\(o\)", Booru);
        Assert.DoesNotContain('#', a.ImageModelPrompt);   // the marker is stripped, not leaked
        Assert.Contains(@"\(o\)", a.ImageModelPrompt);     // the escaped brackets survive into the image prompt
        Assert.Equal(1, a.Marks.Count);                    // it is a single marked tag
    }


    /// <summary>In the negative, '#'/plain and '~' are tag-model exclusions (weight-aware); '!' is image-side only and
    /// is NOT a tagger exclusion. '@' is an artist exclusion.</summary>
    [Theory]
    [InlineData("#feet", true)]
    [InlineData("feet", true)]            // plain, no marker
    [InlineData("~feet", true)]          // guide: tagger-only suppression
    [InlineData("#(feet:1.2)", true)]    // weight-aware
    [InlineData("(~feet:1.1)", true)]    // guide inside a wrapper
    [InlineData("!feet", false)]         // inert: image-side only, never a tagger exclusion
    [InlineData("(!feet:1.2)", false)]
    public void The_negative_excludes_a_tag_from_the_predictor_only_when_the_marker_says_so(string raw, bool excluded)
    {
        (System.Collections.Generic.HashSet<string> tags, _) = PromptFinalizer.NegativeKeys(raw);
        Assert.Equal(excluded, tags.Contains("feet"));
    }

    /// <summary>'@' in the negative is an artist exclusion (weight-aware), never a tag one.</summary>
    [Fact]
    public void The_negative_routes_an_artist_to_the_artist_set()
    {
        (System.Collections.Generic.HashSet<string> tags, System.Collections.Generic.HashSet<string> artists) =
            PromptFinalizer.NegativeKeys("(@greg_rutkowski:1.1)");
        Assert.Contains("greg_rutkowski", artists);
        Assert.DoesNotContain("greg_rutkowski", tags);
    }


    /// <summary>A realistic mix — plain tag, weighted-marked tag (inside spelling), inert, guide, artist — routes each
    /// segment to exactly the right place through the one parse.</summary>
    [Fact]
    public void A_mixed_prompt_routes_every_segment_correctly()
    {
        PromptAnalysis a = PromptParse.Analyze("#1girl, (#long_hair:1.2), !pig, ~1boy, @greg_rutkowski", Booru);

        // Image model: the guide and the markers are gone; the weight is kept; the artist renders (marker stripped on Booru).
        Assert.Equal("1girl, (long_hair:1.2), pig, greg_rutkowski", a.ImageModelPrompt);
        // Marks: tags and the artist, under their base keys; NOT the guide (never in the picture).
        Assert.Equal(TokenKinds.Tag, a.Marks["1girl"]);
        Assert.Equal(TokenKinds.Tag, a.Marks["long_hair"]);
        Assert.Equal(TokenKinds.Tag, a.Marks["pig"]);
        Assert.Equal(TokenKinds.Artist, a.Marks["greg_rutkowski"]);
        Assert.False(a.Marks.ContainsKey("1boy"));
        // Tag model: inert hidden from the seed, guide banned; the artist is hidden from the seed too.
        Assert.Contains("pig", a.InertKeys);
        Assert.Contains("1boy", a.GuideKeys);
        Assert.DoesNotContain("pig", a.TagModelSeed);
        Assert.DoesNotContain("greg_rutkowski", a.TagModelSeed);
        Assert.Contains("1boy", a.TagModelSeed);      // the guide seeds it (rewritten to a '#' tag)
        Assert.Contains("long_hair", a.TagModelSeed);
    }
}
