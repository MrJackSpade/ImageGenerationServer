using System.Text;

namespace ImageGen.Comfy.Patches;

/// <summary>What a <see cref="FileDiff"/> does to the file it names.</summary>
public enum FileChange
{
    /// <summary>The file does not exist beforehand and is created in full.</summary>
    Add,

    /// <summary>The file exists and its lines are rewritten.</summary>
    Modify,

    /// <summary>The file exists beforehand and is removed.</summary>
    Delete,
}

/// <summary>
/// One hunk: a run of context, removed and added lines, anchored at a line number in each side.
/// </summary>
/// <param name="OldStart">1-based first line of <paramref name="OldLines"/> in the pre-image. 0 when the file is new.</param>
/// <param name="NewStart">1-based first line of <paramref name="NewLines"/> in the post-image. 0 when the file is deleted.</param>
/// <param name="OldLines">The lines this hunk expects to find, in order — context and removals.</param>
/// <param name="NewLines">The lines it leaves behind — context and additions.</param>
public sealed record Hunk(int OldStart, IReadOnlyList<string> OldLines, int NewStart, IReadOnlyList<string> NewLines);

/// <summary>Every change to one file. <paramref name="Path"/> is relative to the patch's target directory.</summary>
public sealed record FileDiff(string Path, FileChange Change, IReadOnlyList<Hunk> Hunks)
{
    /// <summary>
    /// The whole file, as bytes, when it is not text. Null for every ordinary diff.
    ///
    /// <para>A node pack is not always only source: sketchKeras ships a 71 MB <c>.pth</c>, and a pack that
    /// carries an asset it cannot run without is still one thing to install or remove. Such a file has no lines
    /// to match, so there is nothing to diff — it is present with these exact bytes or it is not, which makes
    /// applying, verifying and reversing it simpler than text rather than harder.</para>
    ///
    /// <para>Only SYNTHESISED pack patches produce these. The authored <c>.patch</c> files stay text-only: a
    /// diff a person is expected to read and re-export is not the place for a megabyte of base64, and
    /// <see cref="Parse"/> still refuses git's binary patch format.</para>
    /// </summary>
    public byte[]? Bytes { get; init; }

    /// <summary>True when this file is carried whole rather than as hunks.</summary>
    public bool IsBinary => Bytes is not null;

    /// <summary>
    /// True when the pre-image had no trailing newline. Tracked because a diff that ends
    /// "\ No newline at end of file" and one that does not produce different bytes, and the applier
    /// compares bytes.
    /// </summary>
    public bool OldEndsWithoutNewline { get; init; }

    /// <summary>As <see cref="OldEndsWithoutNewline"/>, for the post-image.</summary>
    public bool NewEndsWithoutNewline { get; init; }
}

/// <summary>
/// A unified diff, parsed.
///
/// <para>This is deliberately a small reader of the subset git emits for source trees: file adds, deletes
/// and line edits. It does NOT understand renames, mode changes or binary patches, and says so loudly
/// rather than half-applying one — every patch this repo ships is text, and a patch that needed more than
/// this would be silently mis-applied by a parser that guessed.</para>
/// </summary>
public static class UnifiedDiff
{
    /// <summary>Thrown when a diff cannot be read. The message names the line, because that is the only
    /// thing that makes a malformed patch findable.</summary>
    public sealed class FormatException(string message) : Exception(message);

    private const string NoNewline = "\\ No newline at end of file";

    /// <summary>Split into lines the way a diff does — on \n, with a trailing \r kept off the content.</summary>
    public static string[] SplitLines(string text) =>
        text.Length == 0 ? [] : text.Replace("\r\n", "\n").Split('\n');

    public static IReadOnlyList<FileDiff> Parse(string diff)
    {
        var lines = SplitLines(diff);
        var files = new List<FileDiff>();
        var i = 0;

        while (i < lines.Length)
        {
            // Anything before the first "--- " of a file header is preamble: the "diff --git" line, the
            // "index" line, mode lines. None of it is needed to apply the change, and skipping it is what
            // lets this read both `git diff` and `git format-patch` output.
            if (!lines[i].StartsWith("--- ", StringComparison.Ordinal))
            {
                if (lines[i].StartsWith("Binary files ", StringComparison.Ordinal) ||
                    lines[i].StartsWith("GIT binary patch", StringComparison.Ordinal))
                    throw new FormatException($"line {i + 1}: binary patches are not supported.");
                if (lines[i].StartsWith("rename ", StringComparison.Ordinal))
                    throw new FormatException($"line {i + 1}: renames are not supported — express it as a delete and an add.");
                i++;
                continue;
            }

            var oldPath = HeaderPath(lines[i], "--- ", i);
            i++;
            if (i >= lines.Length || !lines[i].StartsWith("+++ ", StringComparison.Ordinal))
                throw new FormatException($"line {i + 1}: a '--- ' line must be followed by '+++ '.");
            var newPath = HeaderPath(lines[i], "+++ ", i);
            i++;

            var change = (oldPath, newPath) switch
            {
                (null, null) => throw new FormatException($"line {i}: neither side of this file header names a file."),
                (null, not null) => FileChange.Add,
                (not null, null) => FileChange.Delete,
                _ => FileChange.Modify,
            };
            var path = newPath ?? oldPath ?? throw new InvalidOperationException("A diff entry has neither an old nor a new path.");

            var hunks = new List<Hunk>();
            var oldNoNewline = false;
            var newNoNewline = false;

            while (i < lines.Length && lines[i].StartsWith("@@", StringComparison.Ordinal))
            {
                var (oldStart, oldCount, newStart, newCount) = ParseHunkHeader(lines[i], i);
                i++;

                var oldLines = new List<string>();
                var newLines = new List<string>();

                while ((oldLines.Count < oldCount || newLines.Count < newCount) && i < lines.Length)
                {
                    var line = lines[i];

                    // An empty line inside a hunk is a context line whose trailing space git dropped.
                    // Treating it as end-of-hunk truncates the patch, which is how a "clean" apply loses code.
                    if (line.Length == 0)
                    {
                        oldLines.Add("");
                        newLines.Add("");
                        i++;
                        continue;
                    }

                    if (line == NoNewline)
                    {
                        // Applies to whichever side's last line we just read.
                        if (newLines.Count > 0 && (oldLines.Count == 0 || newLines.Count >= oldLines.Count)) newNoNewline = true;
                        if (oldLines.Count > 0 && (newLines.Count == 0 || oldLines.Count >= newLines.Count)) oldNoNewline = true;
                        i++;
                        continue;
                    }

                    var content = line[1..];
                    switch (line[0])
                    {
                        case ' ': oldLines.Add(content); newLines.Add(content); break;
                        case '-': oldLines.Add(content); break;
                        case '+': newLines.Add(content); break;
                        default:
                            throw new FormatException($"line {i + 1}: '{line[0]}' is not a diff marker (expected ' ', '-', '+' or '@@').");
                    }
                    i++;
                }

                if (oldLines.Count != oldCount || newLines.Count != newCount)
                    throw new FormatException(
                        $"hunk at line {i}: header promised {oldCount} old / {newCount} new lines but the body has {oldLines.Count} / {newLines.Count}.");

                hunks.Add(new Hunk(oldStart, oldLines, newStart, newLines));
            }

            if (hunks.Count == 0)
                throw new FormatException($"'{path}' has a file header but no hunks.");

            files.Add(new FileDiff(path, change, hunks)
            {
                OldEndsWithoutNewline = oldNoNewline,
                NewEndsWithoutNewline = newNoNewline,
            });
        }

        if (files.Count == 0) throw new FormatException("this patch contains no file diffs.");
        return files;
    }

    /// <summary>The path out of a '--- a/x' or '+++ b/x' line, or null for /dev/null.</summary>
    private static string? HeaderPath(string line, string prefix, int index)
    {
        var value = line[prefix.Length..].Trim();

        // git appends a tab and a timestamp on some outputs; the path is everything before it.
        var tab = value.IndexOf('\t');
        if (tab >= 0) value = value[..tab];

        if (value is "/dev/null") return null;
        if (value.StartsWith("a/", StringComparison.Ordinal) || value.StartsWith("b/", StringComparison.Ordinal))
            value = value[2..];
        if (value.Length == 0) throw new FormatException($"line {index + 1}: empty path in '{prefix.Trim()}'.");

        // A path escaping the target directory would let a patch write anywhere on the disk. This engine
        // applies patches the app ships, but "we only load our own" is not a property that survives, and the
        // check costs nothing.
        var normalised = value.Replace('\\', '/');
        if (Path.IsPathRooted(normalised) || normalised.Split('/').Contains(".."))
            throw new FormatException($"line {index + 1}: '{value}' leaves the target directory.");

        return normalised;
    }

    private static (int OldStart, int OldCount, int NewStart, int NewCount) ParseHunkHeader(string line, int index)
    {
        // @@ -oldStart,oldCount +newStart,newCount @@ optional heading
        var end = line.IndexOf("@@", 2, StringComparison.Ordinal);
        if (end < 0) throw new FormatException($"line {index + 1}: unterminated hunk header.");

        var parts = line[2..end].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || parts[0][0] != '-' || parts[1][0] != '+')
            throw new FormatException($"line {index + 1}: '{line}' is not a hunk header.");

        var (oldStart, oldCount) = ParseRange(parts[0][1..], index);
        var (newStart, newCount) = ParseRange(parts[1][1..], index);
        return (oldStart, oldCount, newStart, newCount);
    }

    private static (int Start, int Count) ParseRange(string range, int index)
    {
        var comma = range.IndexOf(',');
        // No comma means a one-line range — "@@ -3 +3,2 @@" is legal and means count 1.
        var startText = comma < 0 ? range : range[..comma];
        var countText = comma < 0 ? "1" : range[(comma + 1)..];

        if (!int.TryParse(startText, out var start) || !int.TryParse(countText, out var count) || start < 0 || count < 0)
            throw new FormatException($"line {index + 1}: '{range}' is not a line range.");
        return (start, count);
    }

    /// <summary>
    /// The diff that creates <paramref name="path"/> holding <paramref name="content"/>. This is how a node
    /// pack becomes a patch: every file as one add-hunk, exactly what git emits against /dev/null.
    /// </summary>
    public static FileDiff Added(string path, string content)
    {
        var endsWithoutNewline = content.Length > 0 && !content.EndsWith('\n');
        var lines = SplitLines(content).ToList();

        // A file ending in a newline splits to a trailing empty element that is not a line of the file.
        if (!endsWithoutNewline && lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        return new FileDiff(path.Replace('\\', '/'), FileChange.Add, [new Hunk(0, [], 1, lines)])
        {
            NewEndsWithoutNewline = endsWithoutNewline,
        };
    }

    /// <summary>The diff that creates <paramref name="path"/> holding exactly <paramref name="bytes"/>.</summary>
    public static FileDiff AddedBinary(string path, byte[] bytes) =>
        new(path.Replace('\\', '/'), FileChange.Add, []) { Bytes = bytes };

    /// <summary>Render back to unified-diff text — what the UI shows when someone asks what a patch contains.</summary>
    public static string Write(IEnumerable<FileDiff> files)
    {
        var text = new StringBuilder();
        foreach (var file in files)
        {
            if (file.IsBinary)
            {
                // Named and measured, not dumped: nobody reads a megabyte of base64, and pretending it is a diff
                // would make the output useless for the text around it.
                text.Append("diff --git a/").Append(file.Path).Append(" b/").Append(file.Path).Append('\n');
                text.Append("Binary file, ").Append(file.Bytes?.Length ?? throw new InvalidOperationException($"Binary file '{file.Path}' carries no bytes.")).Append(" bytes\n");
                continue;
            }

            var oldSide = file.Change == FileChange.Add ? "/dev/null" : "a/" + file.Path;
            var newSide = file.Change == FileChange.Delete ? "/dev/null" : "b/" + file.Path;
            text.Append("diff --git a/").Append(file.Path).Append(" b/").Append(file.Path).Append('\n');
            if (file.Change == FileChange.Add) text.Append("new file mode 100644\n");
            if (file.Change == FileChange.Delete) text.Append("deleted file mode 100644\n");
            text.Append("--- ").Append(oldSide).Append('\n');
            text.Append("+++ ").Append(newSide).Append('\n');

            foreach (var hunk in file.Hunks)
            {
                text.Append("@@ -").Append(hunk.OldLines.Count == 0 ? 0 : hunk.OldStart).Append(',').Append(hunk.OldLines.Count)
                    .Append(" +").Append(hunk.NewLines.Count == 0 ? 0 : hunk.NewStart).Append(',').Append(hunk.NewLines.Count)
                    .Append(" @@\n");

                // Walk both sides together so shared lines emit once as context, in their original order.
                int o = 0, n = 0;
                while (o < hunk.OldLines.Count || n < hunk.NewLines.Count)
                {
                    if (o < hunk.OldLines.Count && n < hunk.NewLines.Count && hunk.OldLines[o] == hunk.NewLines[n])
                    {
                        text.Append(' ').Append(hunk.OldLines[o]).Append('\n');
                        if (o == hunk.OldLines.Count - 1 && file.OldEndsWithoutNewline) text.Append(NoNewline).Append('\n');
                        o++; n++;
                    }
                    else if (o < hunk.OldLines.Count && (n >= hunk.NewLines.Count || hunk.OldLines[o] != hunk.NewLines[n]))
                    {
                        text.Append('-').Append(hunk.OldLines[o]).Append('\n');
                        if (o == hunk.OldLines.Count - 1 && file.OldEndsWithoutNewline) text.Append(NoNewline).Append('\n');
                        o++;
                    }
                    else
                    {
                        text.Append('+').Append(hunk.NewLines[n]).Append('\n');
                        if (n == hunk.NewLines.Count - 1 && file.NewEndsWithoutNewline) text.Append(NoNewline).Append('\n');
                        n++;
                    }
                }
            }
        }
        return text.ToString();
    }
}
