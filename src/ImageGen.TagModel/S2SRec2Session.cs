//TODO: CHECK FOR FALLBACKS
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ImageGen.TagModel;

/// <summary>
/// The model itself: one ONNX Runtime session over the exported s2srec2 graph, plus the row→vocab scatter that its
/// output needs.
///
/// <para>The graph takes three inputs and this is the whole contract:</para>
/// <list type="bullet">
/// <item><c>ids</c> (1, n) int64 — the tag set to condition on.</item>
/// <item><c>pad_mask</c> (1, n) bool — always all-false here; batching is what padding is for and this serves one
/// request at a time.</item>
/// <item><c>tmask</c> (1,) int64 — the allowed-types mask, which indexes the model's conditioning embedding directly.
/// The model <b>requires</b> it: the completeness head reads the same pooled encoding, so a model not told which
/// categories are off would judge "is this set finished?" by the standard of sets that still contain them.</item>
/// </list>
///
/// <para>It returns <c>logits</c> over the ~232k EMITTABLE tags (not the full vocab) and a single <c>p_logit</c>
/// completeness score. Python widened the logits by calling <c>model.logits_vocab()</c>; that is a scatter through
/// the checkpoint's <c>out_ids</c>, reproduced here by <see cref="ScatterToVocab"/>, which is why <c>out_ids.bin</c>
/// ships alongside the graph. Nothing else in the Python path touched PyTorch, which is what made this port possible
/// at all — see <c>tools/export-tagmodel-onnx.py</c>.</para>
/// </summary>
public sealed class S2SRec2Session : IDisposable
{
    /// <summary>Fill for vocab slots with no decoder row: they are not emittable, so they must carry no probability.</summary>
    private const float NotEmittable = float.NegativeInfinity;

    private readonly InferenceSession _session;
    private readonly int[] _outIds;
    private readonly int _vocabSize;
    private readonly string _idsName;
    private readonly string _padMaskName;
    private readonly string _typeMaskName;

    /// <summary>Open the graph and load its row→vocab mapping.</summary>
    public S2SRec2Session(string onnxPath, string outIdsPath, int vocabSize)
    {
        _vocabSize = vocabSize;
        _outIds = ReadInt32Array(outIdsPath);

        if (_outIds.Length == 0)
            throw new InvalidDataException($"{outIdsPath} is empty; the model would have no emittable tags.");
        if (_outIds[^1] >= vocabSize)
            throw new InvalidDataException(
                $"{outIdsPath} maps decoder rows onto vocab ids up to {_outIds[^1]}, outside a vocab of {vocabSize}. "
                + "The artifacts are from different builds and every emitted tag would be the wrong one.");

        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        _session = new InferenceSession(onnxPath, options);

        // Bound by name from the graph rather than assumed positionally, so a re-export that reorders inputs fails
        // here with a clear message instead of silently feeding the type mask in as tag ids.
        var inputs = _session.InputMetadata.Keys.ToArray();
        _idsName = Require(inputs, "ids");
        _padMaskName = Require(inputs, "pad_mask");
        _typeMaskName = Require(inputs, "tmask");
    }

    /// <summary>How many tags the decoder may emit — a subset of the vocabulary.</summary>
    public int EmittableCount => _outIds.Length;

    /// <summary>
    /// Run the model over a tag set.
    /// </summary>
    /// <param name="ids">Vocab ids to condition on. May be empty.</param>
    /// <param name="typeMask">The allowed-types mask; see <see cref="TypeMask"/>.</param>
    /// <returns>
    /// <c>Logits</c> indexed by VOCAB id (already widened, <see cref="float.NegativeInfinity"/> where a tag is not
    /// emittable) and <c>CompletenessLogit</c>, whose sigmoid is P(this set is complete).
    /// </returns>
    public (float[] Logits, float CompletenessLogit) Forward(IReadOnlyList<int> ids, int typeMask)
    {
        var n = Math.Max(ids.Count, 1);
        var idBuffer = new long[n];
        for (var i = 0; i < ids.Count; i++)
            idBuffer[i] = ids[i];

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_idsName, new DenseTensor<long>(idBuffer, [1, n])),
            NamedOnnxValue.CreateFromTensor(_padMaskName, new DenseTensor<bool>(new bool[n], [1, n])),
            NamedOnnxValue.CreateFromTensor(_typeMaskName, new DenseTensor<long>(new[] { (long)typeMask }, [1])),
        };

        using var results = _session.Run(inputs);
        var ordered = results.ToArray();
        var rowLogits = ordered[0].AsEnumerable<float>().ToArray();
        var completeness = ordered[1].AsEnumerable<float>().First();

        return (ScatterToVocab(rowLogits), completeness);
    }

    /// <summary>
    /// Widen decoder-row logits to vocab indexing, with <see cref="float.NegativeInfinity"/> everywhere the vocab has
    /// no decoder row. That fill is not a placeholder: those tags are unemittable by design, and -inf is what makes a
    /// softmax give them zero mass and a top-k never select them.
    /// </summary>
    private float[] ScatterToVocab(float[] rowLogits)
    {
        if (rowLogits.Length != _outIds.Length)
            throw new InvalidDataException(
                $"the graph returned {rowLogits.Length} logits but out_ids.bin describes {_outIds.Length} decoder "
                + "rows. The .onnx and out_ids.bin are from different exports.");

        var vocabLogits = new float[_vocabSize];
        Array.Fill(vocabLogits, NotEmittable);
        for (var row = 0; row < _outIds.Length; row++)
            vocabLogits[_outIds[row]] = rowLogits[row];
        return vocabLogits;
    }

    private static string Require(string[] names, string wanted) =>
        names.FirstOrDefault(n => n == wanted)
        ?? throw new InvalidDataException(
            $"the tag model graph has no input named '{wanted}' (it has: {string.Join(", ", names)}). Re-export it "
            + "with tools/export-tagmodel-onnx.py.");

    /// <summary>Little-endian int32, no header: the count is the file length. Written by the export tool.</summary>
    private static int[] ReadInt32Array(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length % sizeof(int) != 0)
            throw new InvalidDataException($"{path} is {bytes.Length} bytes, not a whole number of int32 values.");
        var values = new int[bytes.Length / sizeof(int)];
        Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
        return values;
    }

    /// <inheritdoc />
    public void Dispose() => _session.Dispose();
}
