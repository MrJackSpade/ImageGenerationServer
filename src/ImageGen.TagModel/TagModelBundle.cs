using ImageGen.Domain.CodeAnalysis;
using System.Text.Json;

namespace ImageGen.TagModel;

/// <summary>
/// Everything the tag model needs, loaded once from a directory of published artifacts.
///
/// <para>The directory is produced by <c>tools/export-tagmodel-onnx.py</c> and published to the model's Hugging Face
/// repo; the install script or the container fetches it. Nothing here is generated at runtime — generating the ONNX
/// lazily on first run would make a ~900 MB artifact appear (or fail to) during a user's first autocomplete, and
/// leave a silently stale cache after any checkpoint swap.</para>
/// </summary>
public sealed class TagModelBundle : IDisposable
{
    /// <summary>The published artifact file names loaded from the bundle directory.</summary>
    private static class Files
    {
        /// <summary>The exported ONNX graph's published filename.</summary>
        public const string GraphFileName = "tag_s2srec2.onnx";

        /// <summary>The decoder-row to vocab-id map's published filename.</summary>
        public const string OutIdsFileName = "out_ids.bin";

        /// <summary>The tag vocabulary's published filename.</summary>
        public const string VocabFileName = "vocab_s2srec2.json";

        /// <summary>The model weights sidecar's published filename.</summary>
        public const string WeightsFileName = "tag_s2srec2.onnx.data";

        /// <summary>The junk-ids list's published filename.</summary>
        public const string JunkIdsFileName = "junk_ids.bin";

        /// <summary>The display-calibration file's published filename.</summary>
        public const string CalibrationFileName = "calibration.json";
    }

    /// <summary>The calibration file's JSON property names.</summary>
    private static class CalibrationProps
    {
        /// <summary>Calibration key for the slope term.</summary>
        public const string CalibrationAProperty = "a";

        /// <summary>Calibration key for the intercept term.</summary>
        public const string CalibrationBProperty = "b";
    }

    private TagModelBundle(TagVocab vocab, S2SRec2Session session, DisplayCalibration? calibration, int[] junkIds)
    {
        Vocab = vocab;
        Session = session;
        Calibration = calibration;
        JunkIds = junkIds;
    }

    /// <summary>The tag vocabulary.</summary>
    public TagVocab Vocab { get; }

    /// <summary>The loaded ONNX graph.</summary>
    public S2SRec2Session Session { get; }

    /// <summary>Display calibration, or null if the bundle shipped without it (then raw softmax is shown instead).</summary>
    public DisplayCalibration? Calibration { get; }

    /// <summary>
    /// Vocab ids the junk filter excludes, as published by the export tool.
    ///
    /// <para>Normally empty — a vocab built with the current filter contains no junk by construction. It is honoured
    /// anyway because "usually empty" is not "always empty": an older vocab, or a filter updated after the vocab was
    /// built, would put entries here and the app must not start suggesting <c>bad_id</c>.</para>
    /// </summary>
    public int[] JunkIds { get; }

    /// <summary>Post-hoc display calibration: shown probability = sigmoid(a·logit + b), fitted against the corpus.</summary>
    public sealed record DisplayCalibration(double A, double B);

    /// <summary>
    /// Load a bundle from <paramref name="directory"/>.
    ///
    /// <para>Every missing file is reported by name with what it is for. A tag model that half-loads is worse than one
    /// that refuses to: autocomplete degrading to nonsense is much harder to diagnose than a startup failure.</para>
    /// </summary>
    [AllowMagicStrings("file-purpose descriptions in the missing-artifact exception message")]
    public static TagModelBundle Load(string directory)
    {
        string onnx = Require(directory, Files.GraphFileName, "the model graph");
        string outIds = Require(directory, Files.OutIdsFileName, "the decoder-row to vocab-id map");
        string vocabPath = Require(directory, Files.VocabFileName, "the tag vocabulary");

        // The graph references its weights by relative name, so the ~870 MB sibling must be beside it. ORT reports a
        // confusing protobuf error if it is missing, so check for it here where the message can say what is wrong.
        string weights = Path.Combine(directory, Files.WeightsFileName);
        if (!File.Exists(weights))
        {
            throw new FileNotFoundException(
                $"'tag_s2srec2.onnx.data' (the model weights, ~870 MB) is missing from '{directory}'. The graph "
                + "references it by name, so it must sit beside tag_s2srec2.onnx.", weights);
        }

        TagVocab vocab = TagVocab.Load(vocabPath);
        S2SRec2Session session = new(onnx, outIds, vocab.Count);

        if (session.EmittableCount > vocab.Count)
        {
            throw new InvalidDataException(
                $"the model can emit {session.EmittableCount:N0} tags but the vocabulary holds {vocab.Count:N0}. "
                + "These artifacts are from different builds.");
        }

        string junkPath = Path.Combine(directory, Files.JunkIdsFileName);
        int[] junkIds = File.Exists(junkPath) ? ReadInt32Array(junkPath) : [];

        return new TagModelBundle(vocab, session, LoadCalibration(directory), junkIds);
    }

    /// <summary>
    /// The all-types calibration fit, if present. Absent is tolerable — the caller falls back to the softmax — because
    /// this only affects the percentage shown next to a suggestion, not which tags are suggested or in what order.
    /// </summary>
    private static DisplayCalibration? LoadCalibration(string directory)
    {
        string path = Path.Combine(directory, Files.CalibrationFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty(CalibrationProps.CalibrationAProperty, out JsonElement a) || !root.TryGetProperty(CalibrationProps.CalibrationBProperty, out JsonElement b))
        {
            return null;
        }

        return new DisplayCalibration(a.GetDouble(), b.GetDouble());
    }

    private static string Require(string directory, string fileName, string what)
    {
        string path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"'{fileName}' ({what}) is missing from '{directory}'. Fetch the tag model artifacts, or set "
                + "TagModel:DataDir to where they already are.", path);
        }

        return path;
    }

    private static int[] ReadInt32Array(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int[] values = new int[bytes.Length / sizeof(int)];
        Buffer.BlockCopy(bytes, 0, values, 0, values.Length * sizeof(int));
        return values;
    }

    /// <inheritdoc />
    public void Dispose() => Session.Dispose();
}
