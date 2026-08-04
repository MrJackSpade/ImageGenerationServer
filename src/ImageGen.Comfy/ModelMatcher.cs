//TODO: CHECK FOR FALLBACKS
using System.Text.RegularExpressions;

namespace ImageGen.Comfy;

/// <summary>One catalogue slot's recognition rules, as read from <c>configurations/models/&lt;id&gt;.json</c>.</summary>
/// <param name="Id">The slot id a configuration links to.</param>
/// <param name="Kind">Which loader's file list this slot draws from. Matching never crosses kinds.</param>
/// <param name="Patterns">
/// Published-name patterns, or empty. Empty is the common case and is not a gap: 143 of the 173 shipped slots
/// have none, and are bound by hand.
/// </param>
public sealed record MatchableSlot(string Id, RequirementKind Kind, IReadOnlyList<string> Patterns);

/// <summary>What the matcher concluded for one slot.</summary>
/// <param name="SlotId">The slot.</param>
/// <param name="Candidates">Every file of the right kind that matched, in the order ComfyUI reported them.</param>
/// <param name="AutoBind">
/// The file to bind without asking, or null. Non-null only when exactly one file matched AND no other slot of the
/// same kind claimed it — anything less certain is left for a person, because a wrong binding that looks settled
/// is worse than an empty one that looks empty.
/// </param>
public sealed record SlotMatch(string SlotId, IReadOnlyList<string> Candidates, string? AutoBind);

/// <summary>
/// Matches catalogue slots to the model files ComfyUI reports, so a fresh install offers something instead of an
/// empty picker.
///
/// <para>This only ever produces a <b>suggestion</b>. A binding lives in the database and the user may change or
/// clear any of it; a pattern can pre-fill a slot but never restricts what may be bound to it.</para>
/// </summary>
public static class ModelMatcher
{
    /// <summary>
    /// Compiled without backtracking, which makes catastrophic backtracking impossible by construction rather
    /// than by imposing a time limit on the match. The cost is that lookarounds and backreferences are rejected —
    /// the shipped patterns use neither, and a user-written one that does will fail to compile and be reported
    /// against its file rather than hanging a request.
    /// </summary>
    private const RegexOptions Options =
        RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>Compiles a slot's patterns. Throws <see cref="ArgumentException"/> naming the bad pattern.</summary>
    public static IReadOnlyList<Regex> Compile(MatchableSlot slot)
    {
        var compiled = new List<Regex>(slot.Patterns.Count);
        foreach (var pattern in slot.Patterns)
        {
            try
            {
                compiled.Add(new Regex(pattern, Options));
            }
            // Two different exceptions, one meaning. ArgumentException is a malformed pattern; NotSupportedException
            // is a well-formed one the non-backtracking engine refuses (a lookaround or a backreference). Both are
            // "this pattern is unusable", and both must name the slot — a bare regex error from inside a catalogue
            // load says nothing about which of 173 files to go and look at.
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                throw new ArgumentException(
                    $"Model slot '{slot.Id}' has an unusable match pattern /{pattern}/: {ex.Message} "
                    + "Patterns compile without backtracking, so lookarounds and backreferences are not supported.",
                    nameof(slot), ex);
            }
        }
        return compiled;
    }

    /// <summary>
    /// Works out, for every slot, which present files it recognises and whether one of them can be bound without
    /// asking.
    /// </summary>
    /// <param name="slots">The catalogue's slots.</param>
    /// <param name="filesByKind">What ComfyUI reports it can load, per loader kind.</param>
    /// <returns>One result per slot that matched at least one file. Slots that matched nothing are omitted.</returns>
    public static IReadOnlyList<SlotMatch> Match(
        IEnumerable<MatchableSlot> slots,
        IReadOnlyDictionary<RequirementKind, IReadOnlyList<string>> filesByKind)
    {
        // Pass one: what does each slot recognise?
        var hits = new List<(MatchableSlot Slot, List<string> Files)>();
        foreach (var slot in slots)
        {
            if (slot.Patterns.Count == 0) continue;
            if (!filesByKind.TryGetValue(slot.Kind, out var files) || files.Count == 0) continue;

            var regexes = Compile(slot);
            var matched = files.Where(f => regexes.Any(rx => rx.IsMatch(Stem(f)))).ToList();
            if (matched.Count > 0) hits.Add((slot, matched));
        }

        // Pass two: how many slots of the same kind claim each file? A file two slots both recognise means the
        // patterns are too loose, and picking one of them silently would hide a catalogue bug on the user's disk.
        var claims = new Dictionary<(RequirementKind, string), int>();
        foreach (var (slot, files) in hits)
            foreach (var f in files)
            {
                var key = (slot.Kind, f);
                claims[key] = claims.TryGetValue(key, out var n) ? n + 1 : 1;
            }

        return hits.Select(h =>
        {
            // Bind only on a clean one-to-one: this slot recognised exactly one file, and nothing else of the
            // same kind recognised it either.
            var only = h.Files.Count == 1 ? h.Files[0] : null;
            var auto = only is not null && claims[(h.Slot.Kind, only)] == 1 ? only : null;
            return new SlotMatch(h.Slot.Id, h.Files, auto);
        }).ToList();
    }

    /// <summary>
    /// The filename without its extension. Patterns describe a model, not a container, so <c>.safetensors</c> and
    /// <c>.gguf</c> would otherwise have to be written into every one of them.
    /// </summary>
    private static string Stem(string fileName)
    {
        // Only the final extension, and only a short one: "Wan2.2-TI2V-5B-Q4_K_M.gguf" must lose ".gguf" and keep
        // the version dots, which Path.GetFileNameWithoutExtension would also do — but a name with no extension at
        // all (a custom-node directory, which the catalogue does carry) must survive untouched.
        var dot = fileName.LastIndexOf('.');
        if (dot <= 0 || fileName.Length - dot > 12) return fileName;
        return fileName[..dot];
    }
}
