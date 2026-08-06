using ImageGen.Application.Prompting;

namespace ImageGen.Tests;

/// <summary>
/// The history search box's rule: split the query on whitespace, keep only prompts containing EVERY term. These pin
/// the parts that are easy to get subtly wrong — the AND (not OR, not phrase), the case-insensitivity, the underscore
/// fold that makes "long_hair" and "long hair" the same query, and the empty box being no filter at all.
/// </summary>
public sealed class PromptSearchTests
{
    [Fact]
    public void Every_term_must_appear()
    {
        string[] terms = PromptSearch.Terms("snow miku");

        Assert.True(PromptSearch.Matches(terms, "hatsune miku, standing in snow, night"));
        Assert.False(PromptSearch.Matches(terms, "hatsune miku, on a beach"));   // one term short is not a match
        Assert.False(PromptSearch.Matches(terms, "a snowy field"));
    }

    [Fact]
    public void Terms_need_not_be_adjacent_or_in_order()
    {
        // An AND of contains, not a phrase search: word order and distance are irrelevant.
        string[] terms = PromptSearch.Terms("miku snow");
        Assert.True(PromptSearch.Matches(terms, "snow, forest, night, hatsune miku"));
    }

    [Fact]
    public void Matching_ignores_case()
    {
        Assert.True(PromptSearch.Matches(PromptSearch.Terms("MIKU"), "hatsune miku, blue hair"));
        Assert.True(PromptSearch.Matches(PromptSearch.Terms("miku"), "Hatsune Miku, blue hair"));
    }

    [Theory]
    [InlineData("long_hair")]   // canonical token form, as typed in a prompt box
    [InlineData("long hair")]   // …as it reads once finalized
    public void Underscores_and_spaces_are_the_same_query(string query) =>
        Assert.True(PromptSearch.Matches(PromptSearch.Terms(query), "1girl, long hair, smile"));

    [Fact]
    public void A_term_matches_the_raw_marker_form_too()
    {
        // The stored raw prompt is the marker dialect ("#long_hair"); an image is findable by what the user typed,
        // not only by what the model was finally handed.
        string[] terms = PromptSearch.Terms("long_hair");
        Assert.True(PromptSearch.Matches(terms, prompt: "1girl, smile", rawPrompt: "#long_hair, 1girl, smile"));
    }

    [Fact]
    public void A_term_cannot_span_the_join_between_the_two_prompt_forms()
    {
        // Both forms are searched, but as separate texts — the end of one plus the start of the other is not a hit.
        string[] terms = PromptSearch.Terms("smile1girl");
        Assert.False(PromptSearch.Matches(terms, prompt: "1girl, smile", rawPrompt: "1girl, smile"));
    }

    [Fact]
    public void Partial_words_match() =>
        // Substring, not word-boundary: typing half a tag while you think of the rest still narrows the grid.
        Assert.True(PromptSearch.Matches(PromptSearch.Terms("hatsu"), "hatsune miku"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_box_is_not_a_filter(string? query)
    {
        string[] terms = PromptSearch.Terms(query);
        Assert.Empty(terms);
        Assert.True(PromptSearch.Matches(terms, "literally anything"));
    }

    [Fact]
    public void Repeated_whitespace_between_terms_is_harmless() =>
        Assert.Equal(["a", "b"], PromptSearch.Terms("  a \t b  "));
}
