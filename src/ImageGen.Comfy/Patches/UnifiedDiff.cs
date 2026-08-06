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

    /// <summary>The "no newline at end of file" sentinel a unified diff emits.</summary>
    private static class Sentinel
    {
        public const string NoNewline = "\\ No newline at end of file";
    }

    /// <summary>The fixed markers of the unified/git-diff format this reads and writes.</summary>
    private static class DiffMarker
    {
        public const string OldFileHeader = "--- ";
        public const string NewFileHeader = "+++ ";
        public const string HunkMarker = "@@";
        public const string HunkHeaderStart = "@@ -";
        public const string NewRangePrefix = " +";
        public const string HunkHeaderEnd = " @@\n";
        public const string DiffGitPrefix = "diff --git a/";
        public const string NewSidePrefix = " b/";
        public const string OldPathPrefix = "a/";
        public const string NewPathPrefix = "b/";
        public const string DevNull = "/dev/null";
        public const string ParentDirectory = "..";
        public const string GitBinaryPatch = "GIT binary patch";
        public const string BinaryFilesPrefix = "Binary files ";
        public const string BinaryFilePrefix = "Binary file, ";
        public const string ByteSuffix = " bytes\n";
        public const string Rename = "rename ";
        public const string NewFileMode = "new file mode 100644\n";
        public const string DeletedFileMode = "deleted file mode 100644\n";
        public const string Crlf = "\r\n";
        public const string Lf = "\n";
    }

    /// <summary>Split into lines the way a diff does — on \n, with a trailing \r kept off the content.</summary>
    public static string[] SplitLines(string text) =>
        text.Length == 0 ? [] : text.Replace(DiffMarker.Crlf, DiffMarker.Lf).Split('\n');

    public static IReadOnlyList<FileDiff> Parse(string diff)
    {
        string[] lines = SplitLines(diff);
        List<FileDiff> files = [];
        int i = 0;

        while (i < lines.Length)
        {
            // Anything before the first "--- " of a file header is preamble: the "diff --git" line, the
            // "index" line, mode lines. None of it is needed to apply the change, and skipping it is what
            // lets this read both `git diff` and `git format-patch` output.
            if (!lines[i].StartsWith(DiffMarker.OldFileHeader, StringComparison.Ordinal))
            {
                if (lines[i].StartsWith(DiffMarker.BinaryFilesPrefix, StringComparison.Ordinal) ||
                    lines[i].StartsWith(DiffMarker.GitBinaryPatch, StringComparison.Ordinal))
                {
                    throw new FormatException($"line {i + 1}: binary patches are not supported.");
                }

                if (lines[i].StartsWith(DiffMarker.Rename, StringComparison.Ordinal))
                {
                    throw new FormatException($"line {i + 1}: renames are not supported — express it as a delete and an add.");
                }

                i++;
                continue;
            }

            string? oldPath = HeaderPath(lines[i], DiffMarker.OldFileHeader, i);
            i++;
            if (i >= lines.Length || !lines[i].StartsWith(DiffMarker.NewFileHeader, StringComparison.Ordinal))
            {
                throw new FormatException($"line {i + 1}: a '--- ' line must be followed by '+++ '.");
            }

            string? newPath = HeaderPath(lines[i], DiffMarker.NewFileHeader, i);
            i++;

            FileChange change = (oldPath, newPath) switch
            {
                (null, null) => throw new FormatException($"line {i}: neither side of this file header names a file."),
                (null, not null) => FileChange.Add,
                (not null, null) => FileChange.Delete,
                _ => FileChange.Modify,
            };
            string path = newPath ?? oldPath ?? throw new InvalidOperationException("A diff entry has neither an old nor a new path.");

            List<Hunk> hunks = [];
            bool oldNoNewline = false;
            bool newNoNewline = false;

            while (i < lines.Length && lines[i].StartsWith(DiffMarker.HunkMarker, StringComparison.Ordinal))
            {
                (int oldStart, int oldCount, int newStart, int newCount) = ParseHunkHeader(lines[i], i);
                i++;

                List<string> oldLines = [];
                List<string> newLines = [];

                while ((oldLines.Count < oldCount || newLines.Count < newCount) && i < lines.Length)
                {
                    string line = lines[i];

                    // An empty line inside a hunk is a context line whose trailing space git dropped.
                    // Treating it as end-of-hunk truncates the patch, which is how a "clean" apply loses code.
                    if (line.Length == 0)
                    {
                        oldLines.Add(string.Empty);
                        newLines.Add(string.Empty);
                        i++;
                        continue;
                    }

                    if (line == Sentinel.NoNewline)
                    {
                        // Applies to whichever side's last line we just read.
                        if (newLines.Count > 0 && (oldLines.Count == 0 || newLines.Count >= oldLines.Count))
                        {
                            newNoNewline = true;
                        }

                        if (oldLines.Count > 0 && (newLines.Count == 0 || oldLines.Count >= newLines.Count))
                        {
                            oldNoNewline = true;
                        }

                        i++;
                        continue;
                    }

                    string content = line[1..];
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
                {
                    throw new FormatException(
                        $"hunk at line {i}: header promised {oldCount} old / {newCount} new lines but the body has {oldLines.Count} / {newLines.Count}.");
                }

                hunks.Add(new Hunk(oldStart, oldLines, newStart, newLines));
            }

            if (hunks.Count == 0)
            {
                throw new FormatException($"'{path}' has a file header but no hunks.");
            }

            files.Add(new FileDiff(path, change, hunks)
            {
                OldEndsWithoutNewline = oldNoNewline,
                NewEndsWithoutNewline = newNoNewline,
            });
        }

        if (files.Count == 0)
        {
            throw new FormatException("this patch contains no file diffs.");
        }

        return files;
    }

    /// <summary>The path out of a '--- a/x' or '+++ b/x' line, or null for /dev/null.</summary>
    private static string? HeaderPath(string line, string prefix, int index)
    {
        string value = line[prefix.Length..].Trim();

        // git appends a tab and a timestamp on some outputs; the path is everything before it.
        int tab = value.IndexOf('\t');
        if (tab >= 0)
        {
            value = value[..tab];
        }

        if (value is DiffMarker.DevNull)
        {
            return null;
        }

        if (value.StartsWith(DiffMarker.OldPathPrefix, StringComparison.Ordinal) || value.StartsWith(DiffMarker.NewPathPrefix, StringComparison.Ordinal))
        {
            value = value[2..];
        }

        if (value.Length == 0)
        {
            throw new FormatException($"line {index + 1}: empty path in '{prefix.Trim()}'.");
        }

        // A path escaping the target directory would let a patch write anywhere on the disk. This engine
        // applies patches the app ships, but "we only load our own" is not a property that survives, and the
        // check costs nothing.
        string normalised = value.Replace('\\', '/');
        if (Path.IsPathRooted(normalised) || normalised.Split('/').Contains(DiffMarker.ParentDirectory))
        {
            throw new FormatException($"line {index + 1}: '{value}' leaves the target directory.");
        }

        return normalised;
    }

    private static (int OldStart, int OldCount, int NewStart, int NewCount) ParseHunkHeader(string line, int index)
    {
        // @@ -oldStart,oldCount +newStart,newCount @@ optional heading
        int end = line.IndexOf(DiffMarker.HunkMarker, 2, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new FormatException($"line {index + 1}: unterminated hunk header.");
        }

        string[] parts = line[2..end].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || parts[0][0] != '-' || parts[1][0] != '+')
        {
            throw new FormatException($"line {index + 1}: '{line}' is not a hunk header.");
        }

        (int oldStart, int oldCount) = ParseRange(parts[0][1..], index);
        (int newStart, int newCount) = ParseRange(parts[1][1..], index);
        return (oldStart, oldCount, newStart, newCount);
    }

    private static (int Start, int Count) ParseRange(string range, int index)
    {
        int comma = range.IndexOf(',');
        // No comma means a one-line range — "@@ -3 +3,2 @@" is legal and means count 1.
        string startText = comma < 0 ? range : range[..comma];
        string countText = comma < 0 ? "1" : range[(comma + 1)..];

        if (!int.TryParse(startText, out int start) || !int.TryParse(countText, out int count) || start < 0 || count < 0)
        {
            throw new FormatException($"line {index + 1}: '{range}' is not a line range.");
        }

        return (start, count);
    }

    /// <summary>
    /// The diff that creates <paramref name="path"/> holding <paramref name="content"/>. This is how a node
    /// pack becomes a patch: every file as one add-hunk, exactly what git emits against /dev/null.
    /// </summary>
    public static FileDiff Added(string path, string content)
    {
        bool endsWithoutNewline = content.Length > 0 && !content.EndsWith('\n');
        List<string> lines = SplitLines(content).ToList();

        // A file ending in a newline splits to a trailing empty element that is not a line of the file.
        if (!endsWithoutNewline && lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

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
        StringBuilder text = new();
        foreach (FileDiff file in files)
        {
            if (file.IsBinary)
            {
                // Named and measured, not dumped: nobody reads a megabyte of base64, and pretending it is a diff
                // would make the output useless for the text around it.
                _ = text.Append(DiffMarker.DiffGitPrefix).Append(file.Path).Append(DiffMarker.NewSidePrefix).Append(file.Path).Append('\n');
                _ = text.Append(DiffMarker.BinaryFilePrefix).Append(file.Bytes?.Length ?? throw new InvalidOperationException($"Binary file '{file.Path}' carries no bytes.")).Append(DiffMarker.ByteSuffix);
                continue;
            }

            string oldSide = file.Change == FileChange.Add ? DiffMarker.DevNull : DiffMarker.OldPathPrefix + file.Path;
            string newSide = file.Change == FileChange.Delete ? DiffMarker.DevNull : DiffMarker.NewPathPrefix + file.Path;
            _ = text.Append(DiffMarker.DiffGitPrefix).Append(file.Path).Append(DiffMarker.NewSidePrefix).Append(file.Path).Append('\n');
            if (file.Change == FileChange.Add)
            {
                _ = text.Append(DiffMarker.NewFileMode);
            }

            if (file.Change == FileChange.Delete)
            {
                _ = text.Append(DiffMarker.DeletedFileMode);
            }

            _ = text.Append(DiffMarker.OldFileHeader).Append(oldSide).Append('\n');
            _ = text.Append(DiffMarker.NewFileHeader).Append(newSide).Append('\n');

            foreach (Hunk hunk in file.Hunks)
            {
                _ = text.Append(DiffMarker.HunkHeaderStart).Append(hunk.OldLines.Count == 0 ? 0 : hunk.OldStart).Append(',').Append(hunk.OldLines.Count)
                    .Append(DiffMarker.NewRangePrefix).Append(hunk.NewLines.Count == 0 ? 0 : hunk.NewStart).Append(',').Append(hunk.NewLines.Count)
                    .Append(DiffMarker.HunkHeaderEnd);

                // Walk both sides together so shared lines emit once as context, in their original order.
                int o = 0, n = 0;
                while (o < hunk.OldLines.Count || n < hunk.NewLines.Count)
                {
                    if (o < hunk.OldLines.Count && n < hunk.NewLines.Count && hunk.OldLines[o] == hunk.NewLines[n])
                    {
                        _ = text.Append(' ').Append(hunk.OldLines[o]).Append('\n');
                        if (o == hunk.OldLines.Count - 1 && file.OldEndsWithoutNewline)
                        {
                            _ = text.Append(Sentinel.NoNewline).Append('\n');
                        }

                        o++;
                        n++;
                    }
                    else if (o < hunk.OldLines.Count && (n >= hunk.NewLines.Count || hunk.OldLines[o] != hunk.NewLines[n]))
                    {
                        _ = text.Append('-').Append(hunk.OldLines[o]).Append('\n');
                        if (o == hunk.OldLines.Count - 1 && file.OldEndsWithoutNewline)
                        {
                            _ = text.Append(Sentinel.NoNewline).Append('\n');
                        }

                        o++;
                    }
                    else
                    {
                        _ = text.Append('+').Append(hunk.NewLines[n]).Append('\n');
                        if (n == hunk.NewLines.Count - 1 && file.NewEndsWithoutNewline)
                        {
                            _ = text.Append(Sentinel.NoNewline).Append('\n');
                        }

                        n++;
                    }
                }
            }
        }

        return text.ToString();
    }
}
