//TODO: CHECK FOR FALLBACKS
using System.Text.RegularExpressions;

namespace ImageGen.Tests;

/// <summary>
/// Every prompt-bearing column in this database is encrypted under a PER-USER key, and the point of that is to keep the
/// text out of consoles, logs and terminals — <c>UserLogRepository</c> encrypts prompt-bearing events for exactly this
/// reason. But any code holding the connection string can decrypt, and a migration/diagnostic tool legitimately HAS to:
/// it must read the plaintext to rewrite it. Decrypting is necessary. PRINTING never is.
///
/// This is not hypothetical. The RawPrompt backfill shipped with a "show the first 5 rows" preview and dumped real user
/// prompts straight into a terminal on its first dry run. So the rule is enforced here rather than left to discipline:
/// no <c>Console</c> write and no <c>ILogger</c> call anywhere in src/ or tools/ may emit a prompt-bearing value.
/// Report counts and ids; never content, not even truncated, not even in a dry run.
///
/// <para><c>ILogger</c> is here because it is what a FILE SINK captures. A console line scrolls away with the window;
/// a log file is durable, greppable, and outlives the process that wrote it by years. The rule this repo already chose
/// to enforce mechanically was, until now, enforced only on the sink that matters least.</para>
/// </summary>
public sealed partial class NoPlaintextLogTests
{
    /// <summary>Identifier fragments that mean "this expression may hold user text". Deliberately broad — a false
    /// positive costs a rename, a false negative costs a privacy breach.</summary>
    private static readonly string[] PromptBearing =
        ["prompt", "raw", "instruction", "marks", "token", "seed", "negative", "plaintext", "decrypt"];

    /// <summary>
    /// Fragments that CONTAIN a dangerous one but name an opaque identifier rather than any user text. Removed from a
    /// line before the search, so the heuristic above stays broad without forcing a rename that would make the code
    /// worse: a ComfyUI <c>promptId</c> is a GUID the backend minted — no more revealing than a job id, and the single
    /// most useful thing to have in a log line about a render that failed.
    /// </summary>
    private static readonly string[] NotPromptBearing = ["promptid"];

    [Fact]
    public void No_console_write_can_emit_a_prompt_bearing_value() =>
        AssertNoPromptBearingSink(ConsoleWrite(), "A Console write");

    /// <summary>The same rule for ILogger — the sink a log file actually records.</summary>
    [Fact]
    public void No_log_call_can_emit_a_prompt_bearing_value() =>
        AssertNoPromptBearingSink(LogCall(), "An ILogger call");

    private static void AssertNoPromptBearingSink(Regex sink, string what)
    {
        var root = RepoRoot();
        var offenders = new List<string>();

        foreach (var dir in new[] { "src", "tools" })
        {
            var path = Path.Combine(root, dir);
            if (!Directory.Exists(path))
                continue;

            foreach (var file in Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                    continue;

                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    // Matched against CODE ONLY. A comment can mention a logger call and the reason it must not carry
                    // a prompt — this file's own subject matter — and a comment emits nothing, so matching one is a
                    // false positive that would make the rule impossible to document beside the code it governs.
                    if (!sink.IsMatch(CodeOnly(lines[i])))
                        continue;

                    var code = CodeParts(lines[i]);
                    if (PromptBearing.Any(p => code.Contains(p, StringComparison.OrdinalIgnoreCase)))
                        offenders.Add($"{Path.GetRelativePath(root, file)}:{i + 1}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            what + " appears to emit a prompt-bearing (decrypted user) value. Report counts and ids, never " +
            "content:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The parts of a line that are CODE rather than prose: the interpolation holes, plus what is left once string
    /// literals are removed. So a literal that merely mentions a prompt ("rows with no raw prompt: {n}") is fine, while
    /// one that interpolates a value ("{prompt}") or passes it directly (Console.WriteLine(prompt)) is caught.
    /// </summary>
    private static string CodeParts(string line)
    {
        var holes = string.Join(" ", Hole().Matches(line).Select(m => m.Groups[1].Value));
        var code = holes + " " + CodeOnly(line);
        // Strip the known-safe fragments BEFORE searching for the dangerous ones, or "promptId" reads as "prompt".
        foreach (var safe in NotPromptBearing)
            code = code.Replace(safe, "", StringComparison.OrdinalIgnoreCase);
        return code;
    }

    /// <summary>The executable part of a line: string literals removed first (so a "//" inside a URL isn't mistaken
    /// for a comment), then everything from the first remaining "//" dropped.</summary>
    private static string CodeOnly(string line)
    {
        var withoutLiterals = Literal().Replace(line, "");
        var comment = withoutLiterals.IndexOf("//", StringComparison.Ordinal);
        return comment < 0 ? withoutLiterals : withoutLiterals[..comment];
    }

    /// <summary>Walk up from the test binary to the directory holding the solution.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ImageGen.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [GeneratedRegex(@"Console\s*\.\s*(Out\s*\.\s*|Error\s*\.\s*)?Write(Line)?\s*\(")]
    private static partial Regex ConsoleWrite();

    /// <summary>Any ILogger call, however the logger is named — the extension methods and the raw Log(level, …).</summary>
    [GeneratedRegex(@"\.\s*Log(Trace|Debug|Information|Warning|Error|Critical)?\s*\(")]
    private static partial Regex LogCall();

    [GeneratedRegex(@"\{([^{}]*)\}")]
    private static partial Regex Hole();

    [GeneratedRegex("\"[^\"]*\"")]
    private static partial Regex Literal();
}
