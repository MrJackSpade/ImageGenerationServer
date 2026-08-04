//TODO: CHECK FOR FALLBACKS
using ImageGen.Application.Prompting;
using ImageGen.Application.Rendering;
using ImageGen.Application.Workflows;
using ImageGen.Domain;

namespace ImageGen.Tests;

/// <summary>
/// The marker form ('#tag, @artist' on canonical underscored tokens) is what the user types, what the random samplers
/// append to, and what is STORED verbatim as HistoryEntry.RawPrompt — so copy / Reload / Edit can hand it straight back.
/// These pin the two halves that keeps honest: the canonical key, and the guarantee that finalizing the stored raw
/// prompt reproduces the stored rendered prompt and marks exactly.
/// </summary>
public sealed class PromptMarkersTests
{
    /// <summary>The two tagging flavours actually configured in workflows.json.</summary>
    private static readonly WorkflowTagging Booru = new(Tags: true, Artists: true, KeepArtistMarker: false, UnderscoresToSpaces: false);

    /// <summary>The keep-the-'@', fold-the-underscores flavour (Anima and friends).</summary>
    private static readonly WorkflowTagging Anima = new(Tags: true, Artists: true, KeepArtistMarker: true, UnderscoresToSpaces: true);

    [Theory]
    [InlineData("Long Hair", "long_hair")]      // display form -> canonical
    [InlineData("#long_hair", "long_hair")]     // marker dropped
    [InlineData("@Greg Rutkowski", "greg_rutkowski")]
    [InlineData("  spaced   out  ", "spaced_out")]   // whitespace RUNS collapse to a single '_'
    public void Key_is_the_canonical_token(string segment, string expected) =>
        Assert.Equal(expected, PromptMarkers.Key(segment));

    /// <summary>
    /// The worker injects a randomly-sampled tag by appending '#token' to the RAW prompt and letting the finalizer
    /// render it, instead of appending pre-rendered text and hand-writing the marks entry. Same rendered output, same
    /// mark — and the injected tag now also lives in the stored raw prompt, so reloading the image re-rolls nothing:
    /// you get the picture you clicked on.
    /// </summary>
    [Theory]
    [InlineData(false, "1girl, long_hair")]
    [InlineData(true, "1girl, long hair")]
    public void A_randomly_injected_tag_renders_exactly_as_the_hand_rolled_injection_did(bool anima, string expected)
    {
        var tagging = anima ? Anima : Booru;
        var raw = PromptFinalizer.Append("#1girl", PromptMarkers.TagMarker + "long_hair");

        var final = PromptFinalizer.Finalize(raw, tagging);

        Assert.Equal(expected, final.Rendered);
        Assert.Equal(TokenKinds.Tag, final.Marks["long_hair"]);
    }

    /// <summary>
    /// Same for the random artist: appending '@token' to the raw prompt must reproduce what AppendArtist used to build
    /// by hand — '@' kept only when the model documents it, underscores folded only when the model wants spaces.
    /// </summary>
    [Theory]
    [InlineData(false, "1girl, greg_rutkowski")]   // marker stripped, underscores kept
    [InlineData(true, "1girl, @greg rutkowski")]   // marker kept, underscores folded
    public void A_randomly_injected_artist_renders_exactly_as_AppendArtist_did(bool anima, string expected)
    {
        var tagging = anima ? Anima : Booru;
        var raw = PromptFinalizer.Append("#1girl", PromptMarkers.ArtistMarker + "greg_rutkowski");

        var final = PromptFinalizer.Finalize(raw, tagging);

        Assert.Equal(expected, final.Rendered);
        Assert.Equal(TokenKinds.Artist, final.Marks["greg_rutkowski"]);
    }

    /// <summary>
    /// The invariant the whole design rests on: finalizing the stored raw prompt reproduces the stored rendered prompt
    /// and marks exactly. Both are written from the same string by the worker, so a Reload of the raw prompt renders
    /// the same image — and every read surface can hand the raw prompt back without inverting anything.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Finalizing_the_stored_raw_prompt_reproduces_the_stored_prompt_and_marks(bool anima)
    {
        var tagging = anima ? Anima : Booru;
        // Exactly how the worker builds it: what the user typed, then the random injections in the same dialect.
        var raw = PromptFinalizer.Append("#1girl, a plain phrase", "#long_hair, @greg_rutkowski");

        var stored = PromptFinalizer.Finalize(raw, tagging);
        var reloaded = PromptFinalizer.Finalize(raw, tagging);   // the Reload button resubmits the raw prompt as-is

        Assert.Equal(stored.Rendered, reloaded.Rendered);
        Assert.Equal(stored.Marks, reloaded.Marks);
        Assert.Equal(["1girl", "long_hair", "greg_rutkowski"], stored.Marks.Keys);
    }
}
