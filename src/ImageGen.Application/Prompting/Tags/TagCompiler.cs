namespace ImageGen.Application.Prompting.Tags;

/// <summary>
/// The parsed prompt-expression TREE (before any randomness is resolved) — issue #157's group layer. A prompt is a
/// sequence of literal text, <c>[a|b]</c> CHOICE groups (pick one at random) and <c>{a|b}</c> EXPLODE groups (one output
/// per option), each nestable. <see cref="Generate"/> resolves it: explode multiplies into combos, choice picks one per
/// combo (independently, matching the client's old two-pass behaviour), and each resulting flat prompt is parsed into a
/// <see cref="GeneratedTagGroup"/> ready to render for either model. Parsing is separated from generation from rendering
/// so each layer is independently testable.
/// <para>Disambiguation matches the client: a bracket group is a CHOICE/EXPLODE only when it contains a top-level
/// <c>|</c>; a <c>[tag]</c> (de-emphasis) or <c>(tag:1.2)</c> (weight) has none, so it is left as literal text for the
/// tag parser to read as emphasis.</para>
/// </summary>
public sealed class TagGroup
{
    private readonly PromptExpr _root;

    internal TagGroup(PromptExpr root) => _root = root;

    /// <summary>Parse raw prompt text into the expression tree.</summary>
    public static TagGroup Parse(string? text) => new(PromptExpr.ParseSequence(text ?? string.Empty, 0, out _, TagGroupParse.TopLevel));

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

/// <summary>Which delimiters end the sequence currently being parsed.</summary>
internal enum TagGroupParse
{
    /// <summary>Top level — only end of input ends it.</summary>
    TopLevel,

    /// <summary>Inside a group option — a top-level <c>|</c> or the matching close bracket ends it.</summary>
    Option,
}

/// <summary>One node of the prompt-expression tree.</summary>
public abstract record PromptExpr
{
    /// <summary>Phase 1 — EXPLODE expansion: the set of trees with every <c>{a|b}</c> replaced by one of its options
    /// (cartesian across the sequence), <c>[a|b]</c> choices left intact for phase 2.</summary>
    public abstract IReadOnlyList<PromptExpr> ExpandExplode();

    /// <summary>Phase 2 — CHOICE resolution: the flat text with every <c>[a|b]</c> replaced by one picked option.</summary>
    public abstract string ResolveChoices(Func<int, int> pick);

    /// <summary>Parse a sequence of elements from <paramref name="s"/> at <paramref name="i"/> until end-of-input (top
    /// level) or a top-level <c>|</c>/close-bracket (inside an option). <paramref name="end"/> is the index it stopped
    /// at (the delimiter, or the length).</summary>
    internal static Seq ParseSequence(string s, int i, out int end, TagGroupParse ctx)
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
            if (ctx == TagGroupParse.Option && (c == '|' || c == ']' || c == '}'))
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

            _ = text.Append(c);   // any other char (including a '[tag]'/'(w)' that isn't a group) is literal text
            i++;
        }

        FlushText();
        end = i;
        return new Seq(parts);
    }

    /// <summary>Try to read a CHOICE (<c>[…|…]</c>) or EXPLODE (<c>{…|…}</c>) group at <paramref name="i"/>, returning the
    /// node and setting <paramref name="after"/> to the index past it. Returns null (a literal bracket) unless the bracket
    /// closes AND holds a top-level <c>|</c> (≥2 options) — so a <c>[tag]</c> de-emphasis or a <c>{plain}</c> is left as text.</summary>
    private static PromptExpr? TryParseGroup(string s, int i, out int after)
    {
        after = i;
        char open = s[i], close = open == '[' ? ']' : '}';
        List<PromptExpr> options = [];
        int j = i + 1;
        while (true)
        {
            Seq option = ParseSequence(s, j, out int stop, TagGroupParse.Option);
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

            if (d == close)
            {
                after = stop + 1;
                break;
            }

            return null;   // a foreign close bracket ([...} ) — not a well-formed group
        }

        if (options.Count < 2)
        {
            return null;   // no top-level '|': a '[tag]' de-emphasis or '{plain}', left for the tag parser
        }

        return open == '[' ? new Choice(options) : new Explode(options);
    }
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

/// <summary>A <c>[a|b]</c> group: exactly ONE option, picked at random per output.</summary>
public sealed record Choice(IReadOnlyList<PromptExpr> Options) : PromptExpr
{
    /// <summary>A choice does NOT multiply the combos — it is left intact by phase 1 and resolved in phase 2.</summary>
    public override IReadOnlyList<PromptExpr> ExpandExplode() => [this];

    /// <inheritdoc/>
    public override string ResolveChoices(Func<int, int> pick) =>
        Options[Clamp(pick(Options.Count), Options.Count)].ResolveChoices(pick);

    private static int Clamp(int idx, int count) => idx < 0 ? 0 : idx >= count ? count - 1 : idx;
}

/// <summary>A <c>{a|b}</c> group: one output per option (multiplies the combos).</summary>
public sealed record Explode(IReadOnlyList<PromptExpr> Options) : PromptExpr
{
    /// <inheritdoc/>
    public override IReadOnlyList<PromptExpr> ExpandExplode() => [.. Options.SelectMany(o => o.ExpandExplode())];

    /// <inheritdoc/>
    public override string ResolveChoices(Func<int, int> pick) =>
        throw new InvalidOperationException("Explode groups are removed by ExpandExplode() before choice resolution.");
}
