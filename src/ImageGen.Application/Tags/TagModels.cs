using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Application.Tags;

/// <summary>One autocomplete row: the tag/artist name, how many images carry it (for ranking), and its raw category
/// id from the data file (Gelbooru: 0=general, 1=artist, 3=copyright, 4=character, 5=meta, 6=deprecated).</summary>
/// <param name="Name">The tag or artist name (underscored, no marker).</param>
/// <param name="Count">Number of images carrying the token, for ranking.</param>
/// <param name="Type">Raw Gelbooru category id.</param>
public readonly record struct TagEntry(string Name, int Count, int Type);

/// <summary>One model-ranked tag suggestion: the tag plus its conditional probability and lift given the current
/// prompt context.</summary>
/// <param name="Name">The suggested tag name.</param>
/// <param name="P">Conditional probability P(tag | context tags).</param>
/// <param name="Lift">Lift over the tag's base rate, or null when unavailable.</param>
public readonly record struct TagSuggestion(
    string Name,
    double P,
    [property: AllowNullable("null = lift unavailable (no base rate for the tag); 0.0 would be a real lift value")] double? Lift);
