namespace ImageGen.Domain.Entities;

/// <summary>
/// One entry from a generation's marks map: a canonical prompt token (lowercase, underscored)
/// and whether it was a tag or an artist. Used to render bookmarkable chips and to filter
/// history/bookmarks by a starred artist/tag.
/// <para><paramref name="Generated"/> records the token's PROVENANCE: true when a random sampler
/// (random-prompt tag or random-artist) APPENDED it, false when the user typed it. The viewer dashes
/// the border of generated chips. Pre-provenance rows carry false — "not known to be generated", which
/// renders no dash (never a guess).</para>
/// </summary>
public sealed record Mark(string Token, TokenKind Kind, bool Generated = false);
