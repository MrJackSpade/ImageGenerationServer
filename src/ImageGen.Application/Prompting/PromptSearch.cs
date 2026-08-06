namespace ImageGen.Application.Prompting;

/// <summary>
/// The history search rule: the typed query splits on whitespace and an entry matches only when EVERY term appears
/// somewhere in its prompt (case-insensitive substring) — an AND of loose contains, not a phrase match, so
/// "miku snow" finds a prompt that says "snow" long before it says "hatsune miku".
///
/// Underscores fold to spaces on both sides, so one typed term finds a tag in either dialect: "long_hair" matches the
/// finalized "long hair" and the marker-form "#long_hair" alike, and so does "long hair" typed as two terms.
/// Both forms of the prompt are searched — the FINALIZED text (what the card shows) and the RAW marker form (what the
/// user actually typed) — because they differ, and an image is just as findable by either.
///
/// This runs in memory rather than in SQL because <c>HistoryEntry.Prompt</c> is randomized-encrypted at rest: the
/// ciphertext supports no LIKE, so the only place the prompt is readable is after the repository decrypts it.
/// </summary>
public static class PromptSearch
{
    /// <summary>The terms of a search box, folded and ready for <see cref="Matches"/>. Empty when nothing was typed.</summary>
    public static string[] Terms(string? search) =>
        string.IsNullOrWhiteSpace(search)
            ? []
            : [.. Fold(search).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>
    /// True when every term of <paramref name="terms"/> appears in the prompt. No terms → everything matches (an empty
    /// search box is not a filter).
    /// </summary>
    /// <param name="terms">Terms from <see cref="Terms"/>.</param>
    /// <param name="prompt">The finalized prompt.</param>
    /// <param name="rawPrompt">The raw marker-form prompt, when the row has one.</param>
    public static bool Matches(IReadOnlyList<string> terms, string? prompt, string? rawPrompt = null)
    {
        if (terms.Count == 0)
        {
            return true;
        }

        // '\n' between the two forms so a term can't span the join.
        string haystack = Fold(prompt + "\n" + rawPrompt);
        foreach (string term in terms)
        {
            if (!haystack.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Underscores are the canonical token separator; treat them as spaces so either spelling matches.</summary>
    private static string Fold(string? s) => (s ?? string.Empty).Replace('_', ' ');
}