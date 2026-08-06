namespace ImageGen.Application.Prompting.Tags;

/// <summary>What the leading marker on a comma-segment declares it to be.</summary>
public enum TagKind
{
    /// <summary>No marker — a plain word/phrase or an unmarked tag.</summary>
    Plain,

    /// <summary>'#' — a booru tag (rendered, marked, seeds the predictor).</summary>
    Tag,

    /// <summary>'@' — an artist (rendered per the model's keep-'@' rule, marked, excluded from the seed).</summary>
    Artist,

    /// <summary>'!' — an inert tag: rendered and marked like a '#' tag, but hidden from the predictor's seed.</summary>
    Inert,

    /// <summary>'~' — a guide tag: seeds the predictor but never reaches the image model and is never marked.</summary>
    Guide,
}

/// <summary>
/// The A1111/Comfy emphasis wrapping a tag carries — kept EXACTLY as typed so a tag renders with the identical weight
/// (the picture is unchanged; only what the tag matches against is weight-invisible, issue #133). <see cref="Open"/>
/// and <see cref="Close"/> are the literal bracket text on each side of the base tag; <see cref="Weight"/> is the
/// derived numeric strength (A1111 convention) for anything that wants the number rather than the spelling.
/// </summary>
/// <param name="Open">The exact opening wrapper text ("" / "(" / "((" / "[" …).</param>
/// <param name="Close">The exact closing wrapper text ("" / ")" / ":1.2)" / ":1.1):1.2)" / "]" …).</param>
/// <param name="Weight">The numeric strength: none=1.0, <c>(t)</c>=1.1, <c>(t:w)</c>=w, <c>[t]</c>=1/1.1, nested=product.</param>
public sealed record Emphasis(string Open, string Close, double Weight)
{
    /// <summary>No emphasis: a bare tag at strength 1.0.</summary>
    public static readonly Emphasis None = new(string.Empty, string.Empty, 1.0);

    /// <summary>Wrap <paramref name="body"/> back in this exact emphasis: <c>Open + body + Close</c>.</summary>
    public string Wrap(string body) => Open + body + Close;
}

/// <summary>
/// One tag, fully unwrapped: everything the app supports about a single comma-segment, as strongly-typed data — its
/// <see cref="Kind"/> (from the marker), its <see cref="Emphasis"/> (strength), its position (<see cref="Ordinal"/>),
/// the canonical <see cref="Key"/> it matches/seeds/bans under, and the base tag text as typed (<see cref="BaseText"/>,
/// markers and weight removed, casing/underscores/escapes kept for faithful rendering). A parser produces these; a
/// <see cref="TagPromptService"/> reassembles them into what each model consumes.
/// </summary>
/// <param name="Ordinal">0-based position among the prompt's non-empty comma-segments.</param>
/// <param name="Kind">The marker's meaning.</param>
/// <param name="Key">Canonical match key: lowercased, whitespace-&gt;underscores, weight/marker/escapes removed.</param>
/// <param name="BaseText">The base tag as typed (no marker, no weight wrapper) — the render source, escapes intact.</param>
/// <param name="Emphasis">The weight/emphasis wrapper, preserved exactly.</param>
public sealed record ParsedTag(int Ordinal, TagKind Kind, string Key, string BaseText, Emphasis Emphasis);
