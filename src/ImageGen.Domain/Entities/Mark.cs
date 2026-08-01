namespace ImageGen.Domain.Entities;

/// <summary>
/// One entry from a generation's marks map: a canonical prompt token (lowercase, underscored)
/// and whether it was a tag or an artist. Used to render bookmarkable chips and to filter
/// history/bookmarks by a starred artist/tag.
/// </summary>
public sealed record Mark(string Token, TokenKind Kind);
