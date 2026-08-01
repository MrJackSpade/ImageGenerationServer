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

    public static string ToWire(this TokenKind kind) => kind == TokenKind.Artist ? Artist : Tag;

    /// <summary>Anything that isn't "artist" is a tag — the marks map only ever carries the two.</summary>
    public static TokenKind Parse(string? kind) =>
        string.Equals(kind, Artist, StringComparison.OrdinalIgnoreCase) ? TokenKind.Artist : TokenKind.Tag;
}
