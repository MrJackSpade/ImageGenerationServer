using System.Text.RegularExpressions;

namespace ImageGen.Comfy;

/// <summary>
/// Collapses a HuggingFace-sharded model — a single model split across N <c>…-&lt;n&gt;-of-&lt;m&gt;.safetensors</c>
/// files with a sibling <c>model.safetensors.index.json</c> mapping every tensor to the shard that holds it — down
/// to the ONE entry a loader can actually consume.
///
/// <para>ComfyUI's file listings (<c>CLIPLoader</c>/<c>DualCLIPLoader</c> and friends) recurse into the model
/// folders and emit every shard as its own name, so a sharded text encoder such as <c>Qwen2.5-VL-7B-Instruct</c>
/// surfaces as five separate options in the model picker. None of them is loadable on its own — each shard is a
/// distinct, non-overlapping ~1/m slice of the weights — so binding a slot to one is always wrong: the loader reads
/// the folder/index and pulls every shard together (issue #184).</para>
///
/// <para>Detection is by the shard-name grammar alone (the index sibling is a <c>.json</c>, which the model
/// listings never report), which is a fixed HuggingFace convention rather than a guess. The whole group is replaced
/// by a single representative: the containing folder when the shards live in one (<c>Qwen2.5-VL-7B-Instruct</c> —
/// exactly what the VLM folder-loaders take), else the index filename the shards belong to
/// (<c>&lt;stem&gt;.safetensors.index.json</c>). The individual shards are dropped entirely — never "just the first
/// shard", which only trades N confusing options for one that is silently a broken 1/m of a model.</para>
/// </summary>
public static partial class HuggingFaceShards
{
    /// <summary>
    /// One HuggingFace shard filename: an optional directory, a stem, then the <c>-&lt;n&gt;-of-&lt;m&gt;.safetensors</c>
    /// suffix. <c>dir</c> captures through the last separator (so the folder is the deepest one holding the shards);
    /// <c>stem</c> is the shard-set's base name, which the index sibling is named after.
    /// </summary>
    [GeneratedRegex(@"^(?<dir>.*[\\/])?(?<stem>.+)-\d+-of-\d+\.safetensors$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Shard();

    /// <summary>The fixed tokens of the HuggingFace shard grammar this class reads.</summary>
    private static class Names
    {
        /// <summary>The <see cref="Shard"/> capture holding the directory through the last separator (empty at a root).</summary>
        public const string DirGroup = "dir";

        /// <summary>The <see cref="Shard"/> capture holding the shard-set's base name, which its index sibling is named after.</summary>
        public const string StemGroup = "stem";

        /// <summary>The suffix a shard set's index sibling carries, appended to the stem to name a rootless set.</summary>
        public const string IndexSuffix = ".safetensors.index.json";
    }

    /// <summary>
    /// The list with every sharded set collapsed to its single loadable representative, order preserved: the
    /// representative takes the position of the set's first shard and the remaining shards are removed. Non-shard
    /// names pass through untouched. A set's representative is emitted once even when its shards do not run
    /// contiguously in the input.
    /// </summary>
    public static IReadOnlyList<string> Collapse(IEnumerable<string> files)
    {
        List<string> result = [];
        HashSet<string> emitted = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in files)
        {
            Match m = Shard().Match(file);
            if (!m.Success)
            {
                result.Add(file);
                continue;
            }

            // The folder holding the shards (its trailing separator trimmed) is what a loader consumes; a shard set
            // sitting at a listing root has no folder, so it is named by the index file it belongs to instead.
            string dir = m.Groups[Names.DirGroup].Value;
            string representative = dir.Length > 0
                ? dir[..^1]
                : m.Groups[Names.StemGroup].Value + Names.IndexSuffix;

            if (emitted.Add(representative))
            {
                result.Add(representative);
            }
        }

        return result;
    }
}
