using System.Text;

namespace ImageGen.Comfy.Patches;

/// <summary>Whether a patch would apply, and if not, what refused.</summary>
/// <param name="Ok">Every file and hunk found its place.</param>
/// <param name="Reason">Which file and which hunk refused, in words the settings page can show. Null when <paramref name="Ok"/>.</param>
public sealed record PatchProbe(bool Ok, string? Reason)
{
    public static readonly PatchProbe Fine = new(true, null);
    public static PatchProbe No(string reason) => new(false, reason);
}

/// <summary>Raised when a patch cannot be applied. Nothing has been written when this is thrown.</summary>
public sealed class PatchConflictException(string message) : Exception(message);

/// <summary>
/// Applies and un-applies a unified diff against a directory.
///
/// <para>Two rules make this safe to hand a settings page. <b>All or nothing:</b> every file is resolved in
/// memory and only written once the whole patch has resolved, so a patch that fails on its last hunk leaves
/// the tree exactly as it was — there is no half-patched ComfyUI to diagnose. <b>No fuzz:</b> a hunk's
/// context must match exactly. It may match at a different line than the diff recorded (upstream shifts code
/// around constantly, and refusing over that would make every patch break on the next release), but a hunk
/// whose context has genuinely changed is a conflict and says so. Guessing is how a patch corrupts a file
/// while reporting success.</para>
///
/// <para>Un-applying is applying the same diff backwards, which is what makes "is this applied?" answerable
/// without storing a flag anywhere: a patch is applied exactly when it reverse-applies cleanly.</para>
/// </summary>
public static class PatchApplier
{
    private const string ParentDirectory = "..";
    private const string PyCache = "__pycache__";
    private const string ListSeparator = ", ";
    private const string Crlf = "\r\n";
    private const string Lf = "\n";

    /// <summary>Could this patch be applied to <paramref name="root"/> right now, without writing anything?</summary>
    public static PatchProbe Probe(string root, IReadOnlyList<FileDiff> files, bool reverse)
    {
        try
        {
            Resolve(root, files, reverse, overwrite: false);
            return PatchProbe.Fine;
        }
        catch (PatchConflictException ex)
        {
            return PatchProbe.No(ex.Message);
        }
    }

    /// <summary>
    /// The files this patch would create that are already there holding something else — what an operator has to
    /// agree to lose before <c>overwrite</c> is a reasonable thing to ask for.
    /// </summary>
    public static IReadOnlyList<string> Occupied(string root, IReadOnlyList<FileDiff> files)
    {
        List<string> occupied = new List<string>();
        foreach (FileDiff? file in files.Where(f => f.Change == FileChange.Add))
        {
            string full = Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) continue;

            bool differs = file.IsBinary
                ? !File.ReadAllBytes(full).AsSpan().SequenceEqual(file.Bytes)
                : File.ReadAllText(full) != Content(file, reverse: false, ReadCrlf(full));
            if (differs) occupied.Add(file.Path);
        }
        return occupied;
    }

    /// <summary>
    /// Apply (or, with <paramref name="reverse"/>, un-apply) the diff. Throws <see cref="PatchConflictException"/>
    /// having written nothing if any part of it does not fit.
    /// </summary>
    /// <param name="overwrite">
    /// Replace files this patch creates that already hold something else — how a node pack whose installed copy
    /// has fallen behind the shipped one is brought back into line. Off by default, and never inferred: what it
    /// discards might be somebody's edit, so it is a thing the operator says yes to, naming the files.
    /// </param>
    public static void Apply(string root, IReadOnlyList<FileDiff> files, bool reverse, bool overwrite = false)
    {
        Outcome outcome = Resolve(root, files, reverse, overwrite);

        foreach ((string? path, string? content) in outcome.Writes)
        {
            string full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full) ?? throw new InvalidOperationException($"'{full}' has no parent directory."));
            File.WriteAllText(full, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        foreach ((string? path, byte[]? bytes) in outcome.BinaryWrites)
        {
            string full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full) ?? throw new InvalidOperationException($"'{full}' has no parent directory."));
            File.WriteAllBytes(full, bytes);
        }

        foreach (string path in outcome.Deletes)
        {
            string full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full)) File.Delete(full);
            PruneEmptyDirectories(root, Path.GetDirectoryName(full) ?? throw new InvalidOperationException($"'{full}' has no parent directory."));
        }
    }

    /// <summary>
    /// Removing the files a patch added leaves their directories behind, holding nothing but the bytecode Python
    /// wrote while it ran. Those are not something anyone put there, so they go with the files — upward as far as
    /// <paramref name="root"/> but never <paramref name="root"/> itself, which belongs to whoever created it.
    /// </summary>
    private static void PruneEmptyDirectories(string root, string directory)
    {
        string stop = Path.GetFullPath(root);
        string current = Path.GetFullPath(directory);

        while (current.Length > stop.Length && current.StartsWith(stop, StringComparison.OrdinalIgnoreCase) && RemoveIfSpent(current))
            current = Path.GetFullPath(Path.Combine(current, ParentDirectory));
    }

    /// <summary>
    /// Delete <paramref name="directory"/> if nothing is left in it but Python bytecode, and say whether it went.
    /// A pack directory is the patch's own once its files are gone; <c>__pycache__</c> is a runtime artifact of
    /// the code that just left, not content, and keeping it would leave a directory that looks like an install.
    /// </summary>
    public static bool RemoveIfSpent(string directory)
    {
        if (!Directory.Exists(directory)) return false;
        if (Directory.EnumerateFiles(directory).Any()) return false;

        List<string> subdirectories = Directory.EnumerateDirectories(directory).ToList();
        if (subdirectories.Any(d => !string.Equals(Path.GetFileName(d), PyCache, StringComparison.Ordinal))) return false;

        foreach (string cache in subdirectories) Directory.Delete(cache, recursive: true);
        Directory.Delete(directory);
        return true;
    }

    private sealed record Outcome(Dictionary<string, string> Writes, Dictionary<string, byte[]> BinaryWrites, List<string> Deletes);

    /// <summary>Work the whole patch out in memory. Every failure mode lands here, before anything is written.</summary>
    private static Outcome Resolve(string root, IReadOnlyList<FileDiff> files, bool reverse, bool overwrite)
    {
        Dictionary<string, string> writes = new Dictionary<string, string>(StringComparer.Ordinal);
        Dictionary<string, byte[]> binaryWrites = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        List<string> deletes = new List<string>();

        // Collected rather than thrown on sight: a node pack is dozens of files, and being told about the first
        // one that differs, once per attempt, is a bad way to find out that six of them do.
        List<string> occupied = new List<string>();

        foreach (FileDiff file in files)
        {
            FileChange change = reverse
                ? file.Change switch
                {
                    FileChange.Add => FileChange.Delete,
                    FileChange.Delete => FileChange.Add,
                    _ => FileChange.Modify,
                }
                : file.Change;

            string full = Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar));
            bool exists = File.Exists(full);

            switch (change)
            {
                case FileChange.Add:
                    {
                        // A carried file has no lines to reconcile: it is these exact bytes or it is not.
                        if (file.IsBinary)
                        {
                            byte[] bytes = file.Bytes ?? throw new InvalidOperationException($"Binary patch file '{file.Path}' carries no bytes.");
                            if (!exists) { binaryWrites[file.Path] = bytes; break; }
                            if (File.ReadAllBytes(full).AsSpan().SequenceEqual(bytes)) break;
                            if (overwrite) binaryWrites[file.Path] = bytes;
                            else occupied.Add(file.Path);
                            break;
                        }

                        if (!exists)
                        {
                            writes[file.Path] = Content(file, reverse, useCrlf: false);
                            break;
                        }

                        // There already. If it is byte-for-byte what this patch installs, there is nothing to do and
                        // nothing wrong — that is the ordinary state of an applied pack. Otherwise it is somebody
                        // else's file until they say so.
                        if (File.ReadAllText(full) == Content(file, reverse, ReadCrlf(full))) break;

                        if (overwrite) writes[file.Path] = Content(file, reverse, ReadCrlf(full));
                        else occupied.Add(file.Path);
                        break;
                    }

                case FileChange.Delete:
                    {
                        if (!exists) throw new PatchConflictException($"{file.Path} is not there to remove.");

                        if (file.IsBinary)
                        {
                            if (!File.ReadAllBytes(full).AsSpan().SequenceEqual(file.Bytes))
                                throw new PatchConflictException($"{file.Path} is not the file this patch put there, so removing it would discard someone else's.");
                            deletes.Add(file.Path);
                            break;
                        }

                        (List<string>? actual, bool _) = Read(full);
                        IReadOnlyList<string> expected = Side(file.Hunks[0], reverse, wanted: true);
                        if (!actual.SequenceEqual(expected))
                            throw new PatchConflictException($"{file.Path} is not what this patch put there, so removing it would discard someone's changes.");
                        deletes.Add(file.Path);
                        break;
                    }

                default:
                    {
                        if (file.IsBinary)
                            throw new PatchConflictException($"{file.Path} is carried whole; there is no way to modify part of it.");
                        if (!exists) throw new PatchConflictException($"{file.Path} is missing.");
                        (List<string>? lines, bool crlf) = Read(full);
                        List<string> patched = ApplyHunks(file, lines, reverse);
                        writes[file.Path] = Join(patched, EndsWithoutNewline(file, reverse, pre: false), crlf);
                        break;
                    }
            }
        }

        if (occupied.Count > 0)
            throw new PatchConflictException(
                occupied.Count == 1
                    ? $"{occupied[0]} is already there and differs from what this patch installs."
                    : $"{occupied.Count} files are already there and differ from what this patch installs: {string.Join(ListSeparator, occupied)}.");

        return new Outcome(writes, binaryWrites, deletes);
    }

    /// <summary>The whole content of a created file, in the line endings the destination already uses.</summary>
    private static string Content(FileDiff file, bool reverse, bool useCrlf) =>
        Join(Side(file.Hunks[0], reverse, wanted: false), EndsWithoutNewline(file, reverse, pre: false), useCrlf);

    /// <summary>Whether an existing file is a CRLF file, so rewriting it does not flip every line ending.</summary>
    private static bool ReadCrlf(string path) => File.ReadAllText(path).Contains(Crlf, StringComparison.Ordinal);

    /// <summary>The side of a hunk we expect to find (<paramref name="wanted"/>) or intend to leave behind.</summary>
    private static IReadOnlyList<string> Side(Hunk hunk, bool reverse, bool wanted) =>
        wanted != reverse ? hunk.OldLines : hunk.NewLines;

    private static bool EndsWithoutNewline(FileDiff file, bool reverse, bool pre) =>
        pre != reverse ? file.OldEndsWithoutNewline : file.NewEndsWithoutNewline;

    private static List<string> ApplyHunks(FileDiff file, List<string> lines, bool reverse)
    {
        List<string> result = new List<string>(lines.Count);
        int cursor = 0;   // how far through `lines` we have copied

        foreach (Hunk hunk in file.Hunks)
        {
            IReadOnlyList<string> wanted = Side(hunk, reverse, wanted: true);
            IReadOnlyList<string> replacement = Side(hunk, reverse, wanted: false);
            int recorded = (reverse ? hunk.NewStart : hunk.OldStart) - 1;

            int at;
            if (wanted.Count == 0)
            {
                // A pure insertion has no context to search for, so the recorded position is all there is.
                at = Math.Max(recorded, cursor);
                if (at > lines.Count)
                    throw new PatchConflictException($"{file.Path}: this patch inserts at line {at + 1}, past the end of a {lines.Count}-line file.");
            }
            else
            {
                at = FindBlock(lines, wanted, Math.Max(recorded, cursor), cursor);
                if (at < 0)
                    throw new PatchConflictException(
                        $"{file.Path}: the {wanted.Count} line(s) this patch expects around line {recorded + 1} are not there — that code has changed.");
            }

            result.AddRange(lines.GetRange(cursor, at - cursor));
            result.AddRange(replacement);
            cursor = at + wanted.Count;
        }

        result.AddRange(lines.GetRange(cursor, lines.Count - cursor));
        return result;
    }

    /// <summary>
    /// Where <paramref name="block"/> sits in <paramref name="lines"/>, searching outward from
    /// <paramref name="preferred"/> so the nearest match to where the diff was written wins. Never looks
    /// before <paramref name="floor"/> — hunks apply in order and must not reach back over one another.
    /// </summary>
    private static int FindBlock(List<string> lines, IReadOnlyList<string> block, int preferred, int floor)
    {
        int last = lines.Count - block.Count;
        if (last < floor) return -1;

        int start = Math.Clamp(preferred, floor, last);
        if (Matches(lines, block, start)) return start;

        for (int distance = 1; ; distance++)
        {
            int forward = start + distance;
            int backward = start - distance;
            if (forward > last && backward < floor) return -1;
            if (forward <= last && Matches(lines, block, forward)) return forward;
            if (backward >= floor && Matches(lines, block, backward)) return backward;
        }
    }

    private static bool Matches(List<string> lines, IReadOnlyList<string> block, int at)
    {
        for (int i = 0; i < block.Count; i++)
            if (!string.Equals(lines[at + i], block[i], StringComparison.Ordinal))
                return false;
        return true;
    }

    /// <summary>
    /// The file as lines, plus whether it is a CRLF file. Endings are normalised for matching and restored on
    /// write: a Windows checkout of ComfyUI has CRLF throughout and a patch generated on Linux does not, and
    /// refusing over that would make every patch conflict on half the installs for no real reason.
    /// </summary>
    private static (List<string> Lines, bool Crlf) Read(string path)
    {
        string text = File.ReadAllText(path);
        bool crlf = text.Contains(Crlf, StringComparison.Ordinal);
        List<string> lines = UnifiedDiff.SplitLines(text).ToList();

        // A trailing newline splits to an empty final element that is not a line of the file.
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return (lines, crlf);
    }

    private static string Join(IReadOnlyList<string> lines, bool endsWithoutNewline, bool useCrlf)
    {
        string newline = useCrlf ? Crlf : Lf;
        StringBuilder text = new StringBuilder();
        for (int i = 0; i < lines.Count; i++)
        {
            text.Append(lines[i]);
            if (i < lines.Count - 1 || !endsWithoutNewline) text.Append(newline);
        }
        return text.ToString();
    }
}
