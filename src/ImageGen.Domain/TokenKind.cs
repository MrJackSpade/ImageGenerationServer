namespace ImageGen.Domain;

/// <summary>
/// The kind of a prompt token. Mirrors the SPA's marks map values ("tag" | "artist")
/// and the artist/tag bookmark split. Stored as TINYINT in the database.
/// </summary>
public enum TokenKind : byte
{
    Tag = 0,
    Artist = 1,
}

/// <summary>
/// The one spelling of <see cref="TokenKind"/> outside the database: the string a marks map, a chip's data-kind and
/// the API wire all use. Every layer converts through here — a private "artist"/"tag" const in a mapper is how the
/// two sides drift apart.
/// </summary>
public static class TokenKinds
{
    public const string Tag = "tag";
    public const string Artist = "artist";
}

/// <summary>Converts <see cref="TokenKind"/> to and from its <see cref="TokenKinds"/> wire spelling.</summary>
public static class TokenKindWire
{
    public static string ToWire(this TokenKind kind) => kind switch
    {
        TokenKind.Tag => TokenKinds.Tag,
        TokenKind.Artist => TokenKinds.Artist,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown token kind."),
    };

    /// <summary>Parse the two explicit wire spellings. Matching is case-insensitive for compatibility with existing
    /// API clients, but null, blank, and every other value are invalid.</summary>
    public static TokenKind Parse(string? kind) => TryParse(kind, out TokenKind parsed)
        ? parsed
        : throw new FormatException($"Unknown token kind '{kind ?? "<null>"}'; expected '{TokenKinds.Tag}' or '{TokenKinds.Artist}'.");

    /// <summary>Try to parse an explicit tag/artist wire value using the same case-insensitive policy as
    /// <see cref="Parse"/>.</summary>
    public static bool TryParse(string? kind, out TokenKind parsed)
    {
        if (string.Equals(kind, TokenKinds.Tag, StringComparison.OrdinalIgnoreCase))
        {
            parsed = TokenKind.Tag;
            return true;
        }

        if (string.Equals(kind, TokenKinds.Artist, StringComparison.OrdinalIgnoreCase))
        {
            parsed = TokenKind.Artist;
            return true;
        }

        parsed = default;
        return false;
    }
}
