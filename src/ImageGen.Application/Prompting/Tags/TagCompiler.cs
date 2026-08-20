namespace ImageGen.Application.Prompting.Tags;

/// <summary>
/// The parsed prompt-expression TREE (before any randomness is resolved) — issue #157's group layer. A prompt is a
/// sequence of literal text, Comfy-compatible <c>{a|b}</c> CHOICE groups (pick one at random), legacy <c>[a|b]</c>
/// choices, and <c>{{a|b}}</c> EXPLODE groups (one output per option), each nestable. <see cref="Generate"/> resolves it:
/// explode multiplies into combos, choice picks one per
/// combo (independently, matching the client's old two-pass behaviour), and each resulting flat prompt is parsed into a
/// <see cref="GeneratedTagGroup"/> ready to render for either model. Parsing is separated from generation from rendering
/// so each layer is independently testable.
/// <para>A delimited group is syntax only when it contains a top-level <c>|</c>; <c>[tag]</c> de-emphasis,
/// <c>{plain}</c>, and <c>{{plain}}</c> therefore remain literal. Backslash escapes delimiters before parsing.</para>
/// </summary>
public sealed class TagGroup
{
    private readonly PromptExpr _root;

    internal TagGroup(PromptExpr root) => _root = root;

    /// <summary>Parse raw prompt text into the expression tree.</summary>
    public static TagGroup Parse(string? text) => new(PromptExpr.ParseSequence(text ?? string.Empty, 0, out _, null));

    /// <summary>Resolve the tree into one <see cref="GeneratedTagGroup"/> per explode-combo, choices picked via
    /// <paramref name="pick"/> (given an option count, returns the chosen index — inject it for deterministic tests;
    /// defaults to a real RNG). Each combo resolves its choices independently.</summary>
    public IReadOnlyList<GeneratedTagGroup> Generate(Func<int, int>? pick = null)
    {
        Func<int, int> choose = pick ?? (n => n <= 1 ? 0 : System.Random.Shared.Next(n));
        List<GeneratedTagGroup> outputs = [];
        foreach (PromptExpr combo in _root.ExpandExplode())
        {
            outputs.Add(GeneratedTagGroup.FromResolvedText(combo.ResolveChoices(choose)));
        }

        return outputs;
    }

    /// <summary>How many explode-combos this prompt yields (the number the "this will create N generations" warning
    /// needs) — choice groups don't multiply, so they don't count.</summary>
    public int ComboCount => _root.ExpandExplode().Count;
}

/// <summary>Wire tokens for the prompt-expression grammar.</summary>
internal static class PromptSyntaxTokens
{
    public const string ExplodeOpen = "{{";
    public const string ExplodeClose = "}}";
    public const string SquareClose = "]";
    public const string CurlyClose = "}";
}

/// <summary>One node of the prompt-expression tree.</summary>
public abstract record PromptExpr
{
    /// <summary>Phase 1 — EXPLODE expansion: the set of trees with every <c>{{a|b}}</c> replaced by one of its options
    /// (cartesian across the sequence), choice groups left intact for phase 2.</summary>
    public abstract IReadOnlyList<PromptExpr> ExpandExplode();

    /// <summary>Phase 2 — CHOICE resolution: the flat text with every choice replaced by one picked option.</summary>
    public abstract string ResolveChoices(Func<int, int> pick);

    /// <summary>Parse a sequence of elements from <paramref name="s"/> at <paramref name="i"/> until end-of-input (top
    /// level) or a top-level <c>|</c>/close-bracket (inside an option). <paramref name="end"/> is the index it stopped
    /// at (the delimiter, or the length).</summary>
    internal static Seq ParseSequence(string s, int i, out int end, string? closeToken)
    {
        List<PromptExpr> parts = [];
        System.Text.StringBuilder text = new();
        void FlushText()
        {
            if (text.Length > 0)
            {
                parts.Add(new Text(text.ToString()));
                _ = text.Clear();
            }
        }

        while (i < s.Length)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length && IsEscapable(s[i + 1]))
            {
                _ = text.Append(s[i + 1]);
                i += 2;
                continue;
            }

            if (closeToken is not null && (c == '|' || StartsWith(s, i, closeToken)))
            {
                break;   // this option ends here; the caller consumes the delimiter
            }

            if (c is '[' or '{' && TryParseGroup(s, i, out int after) is { } group)
            {
                FlushText();
                parts.Add(group);
                i = after;
                continue;
            }

            // A malformed/no-pipe double-brace opener is literal as a PAIR. Do not reconsider its second brace as a
            // normal Comfy choice opener, which would turn "{{plain}}" into a partially parsed expression.
            if (StartsWith(s, i, PromptSyntaxTokens.ExplodeOpen))
            {
                _ = text.Append(PromptSyntaxTokens.ExplodeOpen);
                i += PromptSyntaxTokens.ExplodeOpen.Length;
                continue;
            }

            _ = text.Append(c);   // any other char (including a '[tag]'/'(w)' that isn't a group) is literal text
            i++;
        }

        FlushText();
        end = i;
        return new Seq(parts);
    }

    /// <summary>Try to read a Comfy CHOICE (<c>{…|…}</c>), legacy choice (<c>[…|…]</c>), or EXPLODE
    /// (<c>{{…|…}}</c>) group at <paramref name="i"/>, returning the node and setting <paramref name="after"/> to the
    /// index past it. Returns null unless the group closes and holds a top-level <c>|</c> (at least two options).</summary>
    private static PromptExpr? TryParseGroup(string s, int i, out int after)
    {
        after = i;
        bool explode = StartsWith(s, i, PromptSyntaxTokens.ExplodeOpen);
        string closeToken = explode ? PromptSyntaxTokens.ExplodeClose
            : s[i] == '[' ? PromptSyntaxTokens.SquareClose : PromptSyntaxTokens.CurlyClose;
        List<PromptExpr> options = [];
        int j = i + (explode ? PromptSyntaxTokens.ExplodeOpen.Length : 1);
        while (true)
        {
            Seq option = ParseSequence(s, j, out int stop, closeToken);
            options.Add(option);
            if (stop >= s.Length)
            {
                return null;   // unterminated group — the open bracket was just a literal char
            }

            char d = s[stop];
            if (d == '|')
            {
                j = stop + 1;
                continue;
            }

            if (StartsWith(s, stop, closeToken))
            {
                after = stop + closeToken.Length;
                break;
            }

            return null;
        }

        if (options.Count < 2)
        {
            return null;   // no top-level '|': a '[tag]' de-emphasis or '{plain}', left for the tag parser
        }

        return explode ? new Explode(options) : new Choice(options);
    }

    private static bool StartsWith(string value, int index, string token) =>
        index + token.Length <= value.Length
        && value.AsSpan(index, token.Length).SequenceEqual(token.AsSpan());

    private static bool IsEscapable(char c) => c is '\\' or '{' or '}' or '[' or ']' or '|';
}

/// <summary>A literal run of text (no unresolved groups).</summary>
public sealed record Text(string Value) : PromptExpr
{
    /// <inheritdoc/>
    public override IReadOnlyList<PromptExpr> ExpandExplode() => [this];

    /// <inheritdoc/>
    public override string ResolveChoices(Func<int, int> pick) => Value;
}

/// <summary>A concatenation of elements.</summary>
public sealed record Seq(IReadOnlyList<PromptExpr> Parts) : PromptExpr
{
    /// <inheritdoc/>
    public override IReadOnlyList<PromptExpr> ExpandExplode()
    {
        // Cartesian across the parts' explode-expansions, keeping each combination as a Seq (choices still inside).
        List<List<PromptExpr>> combos = [[]];
        foreach (PromptExpr part in Parts)
        {
            List<List<PromptExpr>> next = [];
            // prefix outer, option inner — so an earlier group varies SLOWEST (matching the client's left-to-right explode).
            foreach (List<PromptExpr> prefix in combos)
            {
                foreach (PromptExpr expanded in part.ExpandExplode())
                {
                    next.Add([.. prefix, expanded]);
                }
            }

            combos = next;
        }

        return [.. combos.Select(parts => (PromptExpr)new Seq(parts))];
    }

    /// <inheritdoc/>
    public override string ResolveChoices(Func<int, int> pick) => string.Concat(Parts.Select(p => p.ResolveChoices(pick)));
}

/// <summary>A Comfy <c>{a|b}</c> group (or legacy <c>[a|b]</c>): exactly one option, picked at random per output.</summary>
public sealed record Choice(IReadOnlyList<PromptExpr> Options) : PromptExpr
{
    /// <summary>A choice does NOT multiply the combos — it is left intact by phase 1 and resolved in phase 2.</summary>
    public override IReadOnlyList<PromptExpr> ExpandExplode() => [this];

    /// <inheritdoc/>
    public override string ResolveChoices(Func<int, int> pick) =>
        Options[RequireIndex(pick(Options.Count), Options.Count)].ResolveChoices(pick);

    private static int RequireIndex(int index, int count)
    {
        if ((uint)index >= (uint)count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index), index, $"The choice picker must return an index from 0 through {count - 1}.");
        }

        return index;
    }
}

/// <summary>A <c>{{a|b}}</c> group: one output per option (multiplies the combos).</summary>
public sealed record Explode(IReadOnlyList<PromptExpr> Options) : PromptExpr
{
    /// <inheritdoc/>
    public override IReadOnlyList<PromptExpr> ExpandExplode() => [.. Options.SelectMany(o => o.ExpandExplode())];

    /// <inheritdoc/>
    public override string ResolveChoices(Func<int, int> pick) =>
        throw new InvalidOperationException("Explode groups are removed by ExpandExplode() before choice resolution.");
}
