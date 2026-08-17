using System.Text;
using System.Text.Json;

namespace ImageGen.Comfy.Patches;

/// <summary>
/// The patch set this build ships, from its two sources.
///
/// <para><b>comfy-patches/*.patch</b> — a metadata header, a line reading exactly <c>---</c>, then a unified
/// diff. These are hand-written changes to code somebody else owns: ComfyUI core, and third-party node packs.
/// </para>
///
/// <para><b>comfy-nodes/&lt;pack&gt;/</b> plus <c>packs.json</c> — this repository's own node packs, turned into
/// add-everything diffs <i>here, in memory</i>. They stay ordinary <c>.py</c> files to edit, and they reach
/// ComfyUI as patches like everything else. Nothing is generated onto disk, so the shipped patch cannot drift
/// from the tree it came from and there is no sync step to forget.</para>
/// </summary>
public static class ComfyPatchCatalog
{
    /// <summary>Runtime artifacts that live inside a pack and are not part of it: bytecode, and CondCache's cache.</summary>
    private static readonly string[] NotPartOfAPack = ["__pycache__", "cache"];

    /// <summary>Header field names in an authored <c>comfy-patches/*.patch</c> file.</summary>
    private static class HeaderField
    {
        public const string Id = "Id";
        public const string Title = "Title";
        public const string Does = "Does";
        public const string Why = "Why";
        public const string Warn = "Warn";
        public const string Provides = "Provides";
        public const string Target = "Target";
        public const string Source = "Source";
        public const string Rev = "Rev";
    }

    /// <summary>Property names in <c>comfy-nodes/packs.json</c>.</summary>
    private static class PackProperty
    {
        public const string Packs = "packs";
        public const string Dir = "dir";
        public const string Order = "order";
        public const string Id = "id";
        public const string Title = "title";
        public const string Does = "does";
        public const string Why = "why";
        public const string Warn = "warn";
        public const string Provides = "provides";
    }

    /// <summary>File names and globs used when discovering patches and packs.</summary>
    private static class Files
    {
        public const string Manifest = "packs.json";
        public const string PatchGlob = "*.patch";
        public const string AllFilesGlob = "*";
    }

    /// <summary>Markers in an authored patch file.</summary>
    private static class Marker
    {
        public const string HeaderSeparator = "---";
    }

    public sealed class LoadException(string message) : Exception(message);

    /// <summary>
    /// Read both sources and return every patch in apply order.
    /// </summary>
    /// <param name="patchDirectory"><c>comfy-patches/</c>. Skipped when absent.</param>
    /// <param name="nodesDirectory"><c>comfy-nodes/</c>. Skipped when absent.</param>
    public static IReadOnlyList<ComfyPatch> Load(string? patchDirectory, string? nodesDirectory)
    {
        List<ComfyPatch> patches = [];

        if (!string.IsNullOrWhiteSpace(patchDirectory) && Directory.Exists(patchDirectory))
        {
            foreach (string? file in Directory.EnumerateFiles(patchDirectory, Files.PatchGlob).OrderBy(f => f, StringComparer.Ordinal))
            {
                patches.Add(ReadAuthored(file));
            }
        }

        if (!string.IsNullOrWhiteSpace(nodesDirectory) && Directory.Exists(nodesDirectory))
        {
            patches.AddRange(ReadNodePacks(nodesDirectory));
        }

        IGrouping<string, ComfyPatch>? duplicate = patches.GroupBy(p => p.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new LoadException($"Two patches share the id '{duplicate.Key}'. Ids address a patch over the API and must be unique.");
        }

        return [.. patches.OrderBy(p => p.Order).ThenBy(p => p.Id, StringComparer.Ordinal)];
    }

    /// <summary>Read one <c>comfy-patches/*.patch</c>: the header, then the diff after the <c>---</c>.</summary>
    private static ComfyPatch ReadAuthored(string path)
    {
        string name = Path.GetFileName(path);
        string text = File.ReadAllText(path);
        string[] lines = UnifiedDiff.SplitLines(text);

        Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase);
        string? key = null;
        int index = 0;
        bool sawSeparator = false;

        for (; index < lines.Length; index++)
        {
            string line = lines[index];
            if (line == Marker.HeaderSeparator)
            {
                index++;
                sawSeparator = true;
                break;
            }

            // Continuation: a leading space folds the line onto the previous field, so Does: and Why: can be
            // paragraphs rather than one unwrappable line.
            if (line.StartsWith(' ') && key is not null)
            {
                fields[key] = fields[key] + "\n" + line[1..];
                continue;
            }

            int colon = line.IndexOf(':');
            if (colon < 1)
            {
                throw new LoadException($"{name}: line {index + 1} is neither a 'Key: value' header nor the '---' that ends them.");
            }

            key = line[..colon].Trim();
            fields[key] = line[(colon + 1)..].Trim();
        }

        if (!sawSeparator)
        {
            throw new LoadException($"{name}: no '---' separating the header from the diff.");
        }

        string Required(string field)
        {
            return fields.TryGetValue(field, out string? value) && value.Length > 0
            ? value
            : throw new LoadException($"{name}: no {field}: header.");
        }

        string? Optional(string field)
        {
            return fields.TryGetValue(field, out string? value) && value.Length > 0 ? value : null;
        }

        string target = Required(HeaderField.Target).Replace('\\', '/').Trim('/');
        if (target.Length == 0)
        {
            target = PathTokens.CurrentDirectory;
        }

        if (target != PathTokens.CurrentDirectory && (Path.IsPathRooted(target) || target.Split('/').Contains(PathTokens.ParentDirectory)))
        {
            throw new LoadException($"{name}: Target '{target}' leaves the ComfyUI directory.");
        }

        string? source = Optional(HeaderField.Source);
        string? rev = Optional(HeaderField.Rev);
        if (source is not null && rev is null)
        {
            throw new LoadException($"{name}: Source: without Rev:. A patch that installs its target must pin the revision it was written against.");
        }

        string body = index < lines.Length ? string.Join('\n', lines[index..]) : "";
        IReadOnlyList<FileDiff> files;

        if (string.IsNullOrWhiteSpace(body))
        {
            // No diff at all: an install-only patch, which says "this pack belongs here, at this revision" and
            // changes nothing in it. Meaningless without somewhere to get it from.
            if (source is null)
            {
                throw new LoadException($"{name}: no diff and no Source: — this patch would do nothing.");
            }

            files = [];
        }
        else
        {
            try
            {
                files = UnifiedDiff.Parse(body);
            }
            catch (UnifiedDiff.FormatException ex)
            {
                throw new LoadException($"{name}: {ex.Message}");
            }
        }

        return new ComfyPatch(
            Id: Required(HeaderField.Id),
            Title: Required(HeaderField.Title),
            Does: Required(HeaderField.Does),
            Why: Required(HeaderField.Why),
            Target: target,
            SourceUrl: source,
            Rev: rev,
            Warn: Optional(HeaderField.Warn),
            Order: OrderFromName(name),
            Provides: Optional(HeaderField.Provides)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [],
            Files: files);
    }

    /// <summary>The numeric prefix on the filename, which is what orders the authored patches. No prefix sorts last.</summary>
    private static int OrderFromName(string name)
    {
        char[] digits = [.. name.TakeWhile(char.IsAsciiDigit)];
        return digits.Length > 0 ? int.Parse(new string(digits)) : int.MaxValue;
    }

    /// <summary>One entry in <c>comfy-nodes/packs.json</c> — what a pack directory is, for the patch that installs it.</summary>
    private sealed record PackEntry(
        string Dir,
        int Order,
        string Id,
        string Title,
        string Does,
        string Why,
        string? Warn,
        IReadOnlyList<string> Provides);

    private static List<ComfyPatch> ReadNodePacks(string nodesDirectory)
    {
        string manifestPath = Path.Combine(nodesDirectory, Files.Manifest);
        if (!File.Exists(manifestPath))
        {
            throw new LoadException($"{nodesDirectory} has no packs.json, so nothing in it can be described as a patch.");
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (!document.RootElement.TryGetProperty(PackProperty.Packs, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        {
            throw new LoadException("packs.json has no \"packs\" array.");
        }

        List<ComfyPatch> patches = [];
        foreach (JsonElement element in array.EnumerateArray())
        {
            string Required(string field)
            {
                return element.TryGetProperty(field, out JsonElement value) && value.GetString() is { Length: > 0 } text
                    ? text
                    : throw new LoadException($"packs.json: an entry has no \"{field}\".");
            }

            IReadOnlyList<string> OptionalStrings(string field)
            {
                if (!element.TryGetProperty(field, out JsonElement value))
                {
                    return [];
                }

                if (value.ValueKind != JsonValueKind.Array)
                {
                    throw new LoadException($"packs.json: \"{field}\" must be an array of strings.");
                }

                List<string> result = [];
                foreach (JsonElement item in value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                    {
                        throw new LoadException($"packs.json: \"{field}\" contains an empty or non-string value.");
                    }

                    result.Add(item.GetString() ?? throw new LoadException(
                        $"packs.json: \"{field}\" contains a null string."));
                }

                return result;
            }

            PackEntry entry = new(
                Dir: Required(PackProperty.Dir),
                Order: element.TryGetProperty(PackProperty.Order, out JsonElement order) ? order.GetInt32() : int.MaxValue,
                Id: Required(PackProperty.Id),
                Title: Required(PackProperty.Title),
                Does: Required(PackProperty.Does),
                Why: Required(PackProperty.Why),
                Warn: element.TryGetProperty(PackProperty.Warn, out JsonElement warn) ? warn.GetString() : null,
                Provides: OptionalStrings(PackProperty.Provides));

            string packRoot = Path.Combine(nodesDirectory, entry.Dir);
            if (!Directory.Exists(packRoot))
            {
                throw new LoadException($"packs.json names \"{entry.Dir}\", which is not in {nodesDirectory}.");
            }

            patches.Add(SynthesisePack(entry, packRoot));
        }

        return patches;
    }

    private static ComfyPatch SynthesisePack(PackEntry entry, string packRoot)
    {
        List<FileDiff> files = [];

        foreach (string? file in Directory.EnumerateFiles(packRoot, Files.AllFilesGlob, SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(packRoot, file).Replace('\\', '/');
            if (relative.Split('/').Any(segment => NotPartOfAPack.Contains(segment, StringComparer.Ordinal)))
            {
                continue;
            }

            // A pack is not always only source — sketchKeras ships a .pth its node cannot run without — and a
            // file with no lines is carried whole instead of diffed. Detected by content rather than extension:
            // the question is whether it can be split into lines and put back byte-identically, and a NUL byte
            // is the answer that it cannot.
            byte[] bytes = File.ReadAllBytes(file);
            files.Add(bytes.Contains((byte)0)
                ? UnifiedDiff.AddedBinary(relative, bytes)
                : UnifiedDiff.Added(relative, new UTF8Encoding(false).GetString(bytes)));
        }

        if (files.Count == 0)
        {
            throw new LoadException($"{entry.Dir} holds no files to install.");
        }

        return new ComfyPatch(
            Id: entry.Id,
            Title: entry.Title,
            Does: entry.Does,
            Why: entry.Why,
            Target: "custom_nodes/" + entry.Dir,
            SourceUrl: null,      // it IS the source — this patch creates the pack rather than patching one
            Rev: null,
            Warn: entry.Warn,
            Order: entry.Order,
            Provides: entry.Provides,
            Files: files);
    }

    /// <summary>
    /// Where <paramref name="patch"/> stands against the install at <paramref name="comfyRoot"/>, worked out by
    /// trying it both ways. Nothing is stored anywhere: a recorded flag can disagree with the files, and when it
    /// does it is the flag that gets believed.
    /// </summary>
    public static (PatchState State, string? Detail) Inspect(ComfyPatch patch, string comfyRoot)
    {
        string target = patch.ResolveTarget(comfyRoot);

        // An install-only patch has nothing to reverse-apply, so its state is simply whether the pack is there.
        if (patch.IsInstallOnly)
        {
            return Directory.Exists(target) ? (PatchState.Applied, null) : (PatchState.NotApplied, null);
        }

        if (!Directory.Exists(target))
        {
            return patch.CreatesItsTarget
                ? (PatchState.NotApplied, null)
                : (PatchState.TargetMissing, $"{patch.Target} is not installed.");
        }

        PatchProbe reverse = PatchApplier.Probe(target, patch.Files, reverse: true);
        if (reverse.Ok)
        {
            return (PatchState.Applied, null);
        }

        PatchProbe forward = PatchApplier.Probe(target, patch.Files, reverse: false);
        if (forward.Ok)
        {
            return (PatchState.NotApplied, null);
        }

        return (PatchState.Conflicted, forward.Reason);
    }
}
