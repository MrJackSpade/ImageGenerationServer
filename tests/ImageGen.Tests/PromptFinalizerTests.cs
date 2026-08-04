using ImageGen.Application.Prompting;
using ImageGen.Application.Rendering;
using ImageGen.Application.Workflows;
using ImageGen.Comfy;
using ImageGen.Domain;

namespace ImageGen.Tests;

/// <summary>
/// The negative prompt box shares the positive's booru tag/artist autocomplete, so its text arrives carrying '#'/'@'
/// markers (and underscores). The orchestrator finalizes the negative with the SAME <see cref="PromptFinalizer"/> as
/// the positive before it reaches Comfy — otherwise those markers leak raw into the negative conditioning and degrade
/// output. These lock that normalization in.
/// </summary>
public sealed class PromptFinalizerTests
{
    /// <summary>Anima-style booru tagging: tags + artists, keep '@', underscores→spaces (score_ excepted).</summary>
    private static readonly WorkflowTagging Anima = new(Tags: true, Artists: true, KeepArtistMarker: true, UnderscoresToSpaces: true);

    [Fact]
    public void Negative_from_autocomplete_has_its_markers_normalized_like_the_positive()
    {
        // Autocomplete inserts '#tag'/'@artist' with underscores; finalize strips '#', keeps '@' (Anima wants it),
        // and spaces underscores — so no marker junk reaches CLIP.
        var r = PromptFinalizer.Finalize("#bad_anatomy, @some_artist", Anima).Rendered;
        Assert.Equal("bad anatomy, @some artist", r);
        Assert.DoesNotContain("#", r);
    }

    [Fact]
    public void Negative_preserves_score_underscores_and_handles_null_and_untagged_models()
    {
        // score_ tags keep their underscores (model-significant); a null negative finalizes to empty (→ default alone).
        Assert.Equal("worst quality, score_1", PromptFinalizer.Finalize("worst quality, score_1", Anima).Rendered);
        Assert.Equal("", PromptFinalizer.Finalize(null, Anima).Rendered);
        // A non-tagging model has nothing to normalize — the text passes through untouched.
        Assert.Equal("#literal", PromptFinalizer.Finalize("#literal", null).Rendered);
    }

    [Fact]
    public void Multiple_negative_tags_all_survive_finalize_and_compose_onto_the_default()
    {
        // A two-tag negative '#shirt, #bra': both must reach the conditioning, appended after the model default.
        // The pipeline never drops or dedups the second tag — any "only the first negative applies" effect the user
        // sees in the output is the diffusion model's, not this code's.
        const string dflt = "worst quality, low quality, score_1, score_2, score_3, artist name";
        var finalized = PromptFinalizer.Finalize("#shirt, #bra", Anima).Rendered;   // -> "shirt, bra"
        Assert.Equal("shirt, bra, " + dflt, ComfyGraph.ComposeNegative(dflt, finalized));
    }

    [Fact]
    public void Negative_keys_become_exclusions_for_the_random_samplers()
    {
        // A tag the user negated must never be sampled back in as a random positive. Every comma-segment of the negative
        // yields an exclusion key: '@' declares an artist, '#'-marked and plain-typed alike are tags. Keys are canonical
        // (lowercased, spaces->underscores) so they match the ban sets the tag model and RandomArtist already honour.
        var (tags, artists) = PromptFinalizer.NegativeKeys("#from_side, Bad Anatomy, @some_artist");
        Assert.True(tags.SetEquals(["from_side", "bad_anatomy"]));
        Assert.True(artists.SetEquals(["some_artist"]));
    }

    [Fact]
    public void Negative_keys_are_empty_when_nothing_was_negated()
    {
        var (tags, artists) = PromptFinalizer.NegativeKeys(null);
        Assert.Empty(tags);
        Assert.Empty(artists);
        var (tags2, artists2) = PromptFinalizer.NegativeKeys("  , # , ");
        Assert.Empty(tags2);
        Assert.Empty(artists2);
    }

    /// <summary>
    /// Only a segment's LEADING '#'/'@' is a marker. A blanket Replace("#","") would also eat the '#' inside the HTML
    /// entity the scraped vocab spells apostrophes with, so a randomly-sampled "#holding_another&amp;#039;s_foot" would
    /// render as "holding another&amp;039;s foot" — and, because the marks map is keyed BEFORE that strip, the mangled
    /// segment would no longer match its own mark and the card would draw it as a dead plainchip instead of a tag chip.
    /// </summary>
    [Fact]
    public void An_entity_in_a_tag_keeps_its_hash_and_the_segment_still_matches_its_mark()
    {
        var f = PromptFinalizer.Finalize("#holding_another&#039;s_foot", Anima);

        Assert.Equal("holding another&#039;s foot", f.Rendered);
        // The rendered segment must key back onto the mark the same call recorded — that lookup is the chip.
        var key = Assert.Single(f.Marks).Key;
        Assert.Equal("holding_another&#039;s_foot", key);
        Assert.Equal(key, PromptMarkers.Key(f.Rendered));
    }

    /// <summary>Booru tags natively contain '#' and '@' away from position 0; those are token characters, not markers.</summary>
    [Theory]
    [InlineData("#compass", "compass")]                       // leading '#' IS the marker here
    [InlineData("##compass", "#compass")]                     // marked form of the tag '#compass'
    [InlineData("#genei_ibunroku_#fe", "genei ibunroku #fe")]
    [InlineData("#htol#niq:_hotaru_no_nikki", "htol#niq: hotaru no nikki")]
    public void An_interior_hash_is_part_of_the_tag_and_survives(string raw, string expected) =>
        Assert.Equal(expected, PromptFinalizer.Finalize(raw, Anima).Rendered);

    /// <summary>With '@' NOT kept, only the leading one goes — '@_@' and 'j@ck' are tags, not artist markers.</summary>
    [Fact]
    public void An_interior_at_survives_even_when_the_artist_marker_is_stripped()
    {
        var booru = new WorkflowTagging(Tags: true, Artists: true, KeepArtistMarker: false, UnderscoresToSpaces: false);
        Assert.Equal("@_@, j@ck", PromptFinalizer.Finalize("#@_@, #j@ck", booru).Rendered);
        Assert.Equal("some_artist", PromptFinalizer.Finalize("@some_artist", booru).Rendered);
    }

    /// <summary>An inert tag ('!') is a TAG to everything except the seed build: it renders with its marker gone, and
    /// it marks as a plain tag so it still chips, bookmarks and bans like any other.</summary>
    [Fact]
    public void An_inert_tag_renders_and_marks_exactly_like_a_hash_tag()
    {
        var f = PromptFinalizer.Finalize("!pig, #castle, @greg_rutkowski", Anima);
        Assert.Equal("pig, castle, @greg rutkowski", f.Rendered);
        Assert.Equal(TokenKinds.Tag, f.Marks["pig"]);
        Assert.Equal(TokenKinds.Tag, f.Marks["castle"]);
        Assert.Equal(TokenKinds.Artist, f.Marks["greg_rutkowski"]);
    }

    /// <summary>Same position-0 rule the '#'/'@' markers follow: 25 booru tags genuinely begin with '!' ('!', '!!',
    /// '!?', '!girl', '!-shaped_pupils'), and they are written in marked form, so only the marker itself is eaten.</summary>
    [Theory]
    [InlineData("#!!", "!!")]                                 // marked form of the tag '!!'
    [InlineData("#!-shaped_pupils", "!-shaped pupils")]
    [InlineData("!!!", "!!")]                                 // inert marker on the tag '!!'
    [InlineData("#love_live!_sunshine!!", "love live! sunshine!!")]   // interior '!' is part of the tag
    public void An_interior_bang_is_part_of_the_tag_and_survives(string raw, string expected) =>
        Assert.Equal(expected, PromptFinalizer.Finalize(raw, Anima).Rendered);

    /// <summary>The seed handed to the tag predictor is the finalized prompt minus artists minus inert tags. This is
    /// the whole point of '!': "#pig" drags the sample into barnyard co-occurrences, "!pig" does not.</summary>
    [Fact]
    public void Inert_keys_are_read_off_the_raw_prompt_and_only_the_bang_segments_count()
    {
        var keys = PromptMarkers.InertKeys("#castle, !pig, @greg_rutkowski, !Cow Bell, plain phrase");
        Assert.Equal(["cow_bell", "pig"], keys.Order());   // '!Cow Bell' canonicalizes like any other tag
        Assert.Empty(PromptMarkers.InertKeys("#castle, @greg_rutkowski"));
        Assert.Empty(PromptMarkers.InertKeys(null));
    }

    /// <summary>The payoff: '!pig' reaches the image model but never reaches the predictor. '#castle' still conditions,
    /// the artist is still excluded (pre-existing rule), and 'pig' comes back as a key the caller must ban.</summary>
    [Fact]
    public void An_inert_tag_is_hidden_from_the_predictor_seed_but_stays_in_the_prompt()
    {
        var (seed, inert) = RenderOrchestrator.TagSeed("!pig, #castle, #dragon, @greg_rutkowski", Anima);
        Assert.Equal("castle, dragon", seed);
        Assert.Equal(["pig"], inert.Order());
        // ...and the pig is still rendered for the image model, marker gone.
        Assert.Equal("pig, castle, dragon, @greg rutkowski",
                     PromptFinalizer.Finalize("!pig, #castle, #dragon, @greg_rutkowski", Anima).Rendered);
    }

    /// <summary>Marking every tag inert leaves an EMPTY seed — the predictor then samples unconditionally. That is the
    /// honest consequence of the request, not a bug, but it is worth pinning so it can't change silently.</summary>
    [Fact]
    public void An_all_inert_prompt_seeds_the_predictor_with_nothing()
    {
        var (seed, inert) = RenderOrchestrator.TagSeed("!pig, !cow", Anima);
        Assert.Equal("", seed);
        Assert.Equal(["cow", "pig"], inert.Order());
    }

    /// <summary>Untouched by '!': a prompt without one seeds exactly as it did before.</summary>
    [Fact]
    public void A_prompt_with_no_inert_tags_seeds_exactly_as_before()
    {
        var (seed, inert) = RenderOrchestrator.TagSeed("#castle, @greg_rutkowski, plain phrase", Anima);
        Assert.Equal("castle, plain phrase", seed);
        Assert.Empty(inert);
    }

    /// <summary>
    /// A guide tag ('~') is the mirror of '!': it never reaches the image model, so it renders as nothing at all.
    /// It also does not MARK — marks describe the produced image, and a guide tag is by definition not in it, so
    /// chipping it on the card or offering it as a bookmark would be a lie about the picture.
    /// </summary>
    [Fact]
    public void A_guide_tag_renders_as_nothing_and_does_not_mark()
    {
        var f = PromptFinalizer.Finalize("!1girl, ~1boy, #castle", Anima);

        Assert.Equal("1girl, castle", f.Rendered);
        Assert.False(f.Marks.ContainsKey("1boy"));
        Assert.Equal(TokenKinds.Tag, f.Marks["1girl"]);
        Assert.Equal(TokenKinds.Tag, f.Marks["castle"]);
    }

    /// <summary>
    /// A model with no tagging block gets its prompt back BYTE-FOR-BYTE — '~' means "guide tag" only inside the tagging
    /// gate, so a literal '~'-led segment renders exactly as typed rather than being deleted (#91). (For a TAG model it
    /// IS dropped — see <see cref="A_guide_tag_renders_as_nothing_and_does_not_mark"/>. The full non-tag contract is
    /// pinned in <c>PromptFinalizerGatingTests</c>.)
    /// </summary>
    [Fact]
    public void A_non_tag_model_keeps_a_tilde_segment_verbatim()
    {
        Assert.Equal("a plain prompt, ~1boy", PromptFinalizer.Finalize("a plain prompt, ~1boy", null).Rendered);
    }

    /// <summary>The whole point, in one prompt: subject swapping. The predictor is seeded with 1boy — so it samples
    /// the poses, clothing and framing that co-occur with it — while the picture is of a girl.</summary>
    [Fact]
    public void A_guide_tag_seeds_the_predictor_and_the_other_subject_is_what_renders()
    {
        const string raw = "!1girl, ~1boy, #castle";
        var (seed, suppress) = RenderOrchestrator.TagSeed(raw, Anima);

        Assert.Equal("1boy, castle", seed);                       // 1girl hidden ('!'), 1boy present ('~')
        Assert.Equal("1girl, castle", PromptFinalizer.Finalize(raw, Anima).Rendered);   // ...and 1boy is not rendered
        // Both are suppressed for the call: the hidden one so it can't be sampled back, the guide one so an echo
        // can't be appended as a '#' and rendered — the single thing '~' promises cannot happen.
        Assert.Equal(["1boy", "1girl"], suppress.Order());
    }

    /// <summary>A guide tag keeps the POSITION the user wrote it in, and is rendered into the seed the same way its
    /// neighbours are (underscores folded here). Appending the keys to a finished seed would do neither.</summary>
    [Fact]
    public void A_guide_tag_holds_its_place_and_form_in_the_seed()
    {
        var (seed, _) = RenderOrchestrator.TagSeed("#castle, ~long_hair, #dragon", Anima);

        Assert.Equal("castle, long hair, dragon", seed);
    }

    /// <summary>Guide keys are read off the raw prompt, canonicalized like any other tag, and only '~' counts.</summary>
    [Fact]
    public void Guide_keys_are_read_off_the_raw_prompt_and_only_the_tilde_segments_count()
    {
        var keys = PromptMarkers.GuideKeys("#castle, ~1boy, !pig, ~Cow Bell, @greg_rutkowski, plain phrase");

        Assert.Equal(["1boy", "cow_bell"], keys.Order());
        Assert.Empty(PromptMarkers.GuideKeys("#castle, !pig"));
        Assert.Empty(PromptMarkers.GuideKeys(null));
    }

    /// <summary>A prompt of nothing but guide tags renders EMPTY — the honest consequence of asking for a picture of
    /// nothing, and worth pinning so it can't change silently. The predictor still gets its seed.</summary>
    [Fact]
    public void An_all_guide_prompt_renders_nothing_and_still_seeds()
    {
        var (seed, suppress) = RenderOrchestrator.TagSeed("~1boy, ~castle", Anima);

        Assert.Equal("1boy, castle", seed);
        Assert.Equal(["1boy", "castle"], suppress.Order());
        Assert.Equal("", PromptFinalizer.Finalize("~1boy, ~castle", Anima).Rendered);
    }

    /// <summary>Untouched by '~': a prompt without one seeds and renders exactly as before.</summary>
    [Fact]
    public void A_prompt_with_no_guide_tags_is_unaffected()
    {
        var (seed, suppress) = RenderOrchestrator.TagSeed("#castle, @greg_rutkowski, plain phrase", Anima);

        Assert.Equal("castle, plain phrase", seed);
        Assert.Empty(suppress);
    }

    /// <summary>Position 0 only, like every other marker: booru tags contain '~' natively ('~_~', 'x~'), and a tag
    /// that genuinely begins with one is written in marked form.</summary>
    [Theory]
    [InlineData("#~_~", "~ ~")]              // marked form of the tag '~_~' (underscores fold for this model)
    [InlineData("#hoshino_ruby~", "hoshino ruby~")]
    public void An_interior_tilde_is_part_of_the_tag_and_survives(string raw, string expected) =>
        Assert.Equal(expected, PromptFinalizer.Finalize(raw, Anima).Rendered);

    /// <summary>A negated inert tag is still a negated tag — the marker must not survive into the exclusion key.</summary>
    [Fact]
    public void Negative_keys_strip_the_inert_marker_too()
    {
        var (tags, _) = PromptFinalizer.NegativeKeys("!pig, #castle, ~cow");
        Assert.Equal(["castle", "cow", "pig"], tags.Order());
    }

    /// <summary>
    /// A sampled name is marked by ITS OWN category, not by the sampler that produced it. The tag model returns bare
    /// names and emits artist-type ones whenever the generation mask has artists on; marking those '#' would file an
    /// artist as a tag in the marks map, in the chips, and — permanently — in dbo.BannedToken. The marker is the only
    /// thing downstream reads the kind off.
    /// </summary>
    [Fact]
    public void A_sampled_artist_is_marked_an_artist_and_a_sampled_tag_a_tag()
    {
        var artists = new HashSet<string>(["kazaana", "greg_rutkowski"]);
        var tokens = PromptFinalizer.MarkSampled(
            ["long_hair", "kazaana", "castle", "greg_rutkowski"], NoBans, artists.Contains);

        Assert.Equal(["#long_hair", "@kazaana", "#castle", "@greg_rutkowski"], tokens);

        // ...and the marks the finalizer then derives carry that kind through: artist, not tag.
        var marks = PromptFinalizer.Finalize(string.Join(", ", tokens), Anima).Marks;
        Assert.Equal(TokenKinds.Artist, marks["kazaana"]);
        Assert.Equal(TokenKinds.Tag, marks["long_hair"]);
    }

    /// <summary>A name the catalog doesn't know is not an artist — it stays a tag rather than being guessed at.</summary>
    [Fact]
    public void An_unknown_sampled_name_stays_a_tag()
    {
        Assert.Equal(["#some_novel_token"],
            PromptFinalizer.MarkSampled(["some_novel_token"], NoBans, _ => false));
    }

    /// <summary>Banned and empty names never make it into the prompt, and every name is canonicalized on the way in.</summary>
    [Fact]
    public void Sampled_names_are_canonicalized_and_the_banned_are_dropped()
    {
        var banned = new HashSet<string>(["castle"]);
        Assert.Equal(["#long_hair", "#pig"],
            PromptFinalizer.MarkSampled(["Long Hair", "castle", "  ", "pig"], banned, _ => false));
        // An artist ban binds the tag model's output too — the same set suppresses it whichever kind it is.
        Assert.Empty(PromptFinalizer.MarkSampled(["kazaana"], new HashSet<string>(["kazaana"]), _ => true));
        Assert.Empty(PromptFinalizer.MarkSampled(null, NoBans, _ => false));
    }

    private static readonly IReadOnlySet<string> NoBans = new HashSet<string>();

    [Fact]
    public void Append_drops_a_straggling_separator_the_user_left_on_the_prompt()
    {
        // The autocomplete leaves a trailing "," (and often a space) after the tag it inserts. Appending a random-prompt
        // tag or artist onto that raw tail would otherwise produce "1girl,, next_tag" in the rendered prompt.
        Assert.Equal("1girl, next_tag", PromptFinalizer.Append("1girl,", "next_tag"));
        Assert.Equal("1girl, next_tag", PromptFinalizer.Append("1girl, ", "next_tag"));
        Assert.Equal("1girl, next_tag", PromptFinalizer.Append("1girl , , ", "next_tag"));
        Assert.Equal("1girl, next_tag", PromptFinalizer.Append("1girl", "next_tag"));
        // Nothing typed (or only separators) — the addition stands alone, with no leading comma.
        Assert.Equal("next_tag", PromptFinalizer.Append("", "next_tag"));
        Assert.Equal("next_tag", PromptFinalizer.Append(" , ", "next_tag"));
        Assert.Equal("next_tag", PromptFinalizer.Append(null, "next_tag"));
    }
}
