//TODO: CHECK FOR FALLBACKS
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace ImageGen.Comfy;

/// <summary>
/// Tier-2 LoRA compatibility: does a LoRA's tensors actually fit a checkpoint's layers? Computed from FILE HEADERS
/// only — no VRAM, no ComfyUI round-trip, no dequantisation — so it works for both <c>.safetensors</c> and (the common
/// low-VRAM case) <c>.gguf</c> checkpoints.
///
/// <para>The check is by DIMENSION, not by key name. A LoRA's <c>lora_down</c>/<c>lora_up</c> (or <c>lora_A</c>/<c>lora_B</c>)
/// pair encodes the wrapped layer's input and output feature sizes; a matching base model must contain layers of those
/// sizes. So we intersect the set of feature dimensions the LoRA targets with the set of layer dimensions present in the
/// checkpoint. This is architecture-agnostic and quantisation-agnostic — GGUF stores logical tensor dimensions (<c>ne[]</c>)
/// even when the data is quantised — which is exactly why it needs no kohya→comfy name map (that map is what would drift on
/// a new architecture, and what a GGUF layout would defeat).</para>
///
/// <para>It is deliberately conservative: a LoRA that genuinely matches always has every feature dimension it targets
/// present in the base, so a true match is NEVER dimmed (no false negatives). A mismatch fails because its characteristic
/// dimension (e.g. the cross-attention context size) is absent. It can occasionally pass a mismatch whose dimensions all
/// happen to coexist in the base — which is why the picker DIMS rather than hides, and search still finds everything.</para>
/// </summary>
public static class LoraCompatibility
{
    /// <summary>Parsed, cached feature sets for one model file, keyed by (path, length, last-write). Header parsing is
    /// cheap but pointless to repeat every time the picker opens; the cache makes a re-open free.</summary>
    private sealed record FileDims(HashSet<long> AllDims, HashSet<long> LoraFeatureDims, bool ClipCapable);

    private static readonly ConcurrentDictionary<string, FileDims> Cache = new();

    /// <summary>The result for one LoRA against a checkpoint.</summary>
    public readonly record struct Result(bool Compatible, bool ClipCapable);

    /// <summary>Evaluate a LoRA (by absolute path) against a checkpoint whose layer dimensions were read once. A LoRA
    /// whose header can't be read is reported compatible + CLIP-capable (unknown → show, never hide).</summary>
    public static Result Evaluate(string loraPath, IReadOnlySet<long>? checkpointDims)
    {
        var lora = ReadCached(loraPath);
        if (lora is null)
            return new Result(true, true);   // couldn't parse — show it, model-only assumption off

        // No checkpoint dimensions to compare against (unknown/older renderer): compatibility not evaluated.
        if (checkpointDims is null || checkpointDims.Count == 0 || lora.LoraFeatureDims.Count == 0)
            return new Result(true, lora.ClipCapable);

        var compatible = lora.LoraFeatureDims.All(checkpointDims.Contains);
        return new Result(compatible, lora.ClipCapable);
    }

    /// <summary>The set of layer dimensions present in a checkpoint (absolute path), or null when it can't be read.</summary>
    public static IReadOnlySet<long>? CheckpointDims(string checkpointPath)
    {
        var dims = ReadCached(checkpointPath);
        return dims is null || dims.AllDims.Count == 0 ? null : dims.AllDims;
    }

    private static FileDims? ReadCached(string path)
    {
        FileInfo info;
        try { info = new FileInfo(path); if (!info.Exists) return null; }
        catch { return null; }

        var key = $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        if (Cache.TryGetValue(key, out var cached))
            return cached;

        var shapes = ReadShapes(path);
        if (shapes is null)
            return null;

        var dims = Derive(shapes);
        Cache[key] = dims;
        return dims;
    }

    private static FileDims Derive(IReadOnlyDictionary<string, long[]> shapes)
    {
        var all = new HashSet<long>();
        var feature = new HashSet<long>();
        var clip = false;
        foreach (var (name, dim) in shapes)
        {
            if (dim.Length >= 2)
                foreach (var d in dim) all.Add(d);

            var lower = name.ToLowerInvariant();
            // kohya text-encoder LoRA keys: lora_te_, lora_te1_, lora_te2_; diffusers: text_encoder/text_model.
            if (lower.Contains("lora_te") || lower.Contains("text_encoder") || lower.Contains("text_model"))
                clip = true;

            // A LoRA down/A matrix is [rank, in_features] → the input feature size is its LAST dim; an up/B matrix is
            // [out_features, rank] → the output feature size is its FIRST dim. Those are the sizes the base must have.
            if (dim.Length >= 2)
            {
                if (lower.Contains("lora_down") || lower.Contains(".lora_a") || lower.EndsWith("lora_a.weight"))
                    feature.Add(dim[^1]);
                else if (lower.Contains("lora_up") || lower.Contains(".lora_b") || lower.EndsWith("lora_b.weight"))
                    feature.Add(dim[0]);
            }
        }
        return new FileDims(all, feature, clip);
    }

    /// <summary>Tensor name → dimensions, dispatched by extension: the safetensors JSON header, or the GGUF binary
    /// header. Null when neither format applies or the header is unreadable.</summary>
    private static IReadOnlyDictionary<string, long[]>? ReadShapes(string path)
    {
        try
        {
            if (path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
                return ReadGguf(path);
            if (path.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase))
                return ReadSafetensors(path);
            return null;   // .ckpt / .pt and friends aren't header-inspectable this cheaply — treated as "unknown"
        }
        catch { return null; }   // a truncated/garbled header is "unknown" (show it), not a fault to surface here
    }

    /// <summary>The safetensors header: 8-byte little-endian length, then that many bytes of JSON mapping each tensor to
    /// its <c>shape</c>. Only the header is read — never the weights.</summary>
    private static IReadOnlyDictionary<string, long[]> ReadSafetensors(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> lenBuf = stackalloc byte[8];
        fs.ReadExactly(lenBuf);
        var headerLen = BinaryPrimitives.ReadUInt64LittleEndian(lenBuf);
        if (headerLen == 0 || headerLen > 200_000_000)   // a sane guard; real headers are KBs–low MBs
            return new Dictionary<string, long[]>();

        var json = new byte[headerLen];
        fs.ReadExactly(json);
        using var doc = JsonDocument.Parse(json);

        var result = new Dictionary<string, long[]>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Name == "__metadata__" || prop.Value.ValueKind != JsonValueKind.Object) continue;
            if (!prop.Value.TryGetProperty("shape", out var shapeEl) || shapeEl.ValueKind != JsonValueKind.Array) continue;
            var dims = new long[shapeEl.GetArrayLength()];
            var i = 0;
            foreach (var d in shapeEl.EnumerateArray())
                dims[i++] = d.GetInt64();
            result[prop.Name] = dims;
        }
        return result;
    }

    /// <summary>The GGUF header: magic, version, tensor/KV counts, the metadata KVs (skipped), then the tensor infos —
    /// each a name, dimension list, type and offset. Only the header region is read; the quantised data is never touched,
    /// and the dimensions are the logical (unquantised) tensor shapes.</summary>
    private static IReadOnlyDictionary<string, long[]> ReadGguf(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);   // BinaryReader is little-endian; GGUF is little-endian

        if (br.ReadUInt32() != 0x46554747)   // "GGUF"
            return new Dictionary<string, long[]>();
        var version = br.ReadUInt32();

        // v1 used uint32 counts/lengths; v2+ use uint64. Modern quants are v3.
        var tensorCount = ReadCount(br, version);
        var kvCount = ReadCount(br, version);

        for (ulong i = 0; i < kvCount; i++)
            SkipKv(br, version);

        var result = new Dictionary<string, long[]>();
        for (ulong i = 0; i < tensorCount; i++)
        {
            var name = ReadGgufString(br, version);
            var nDims = br.ReadUInt32();
            var dims = new long[nDims];
            for (var d = 0; d < nDims; d++)
                dims[d] = (long)ReadCount(br, version);
            _ = br.ReadUInt32();   // ggml type
            _ = br.ReadUInt64();   // data offset
            result[name] = dims;
        }
        return result;
    }

    private static ulong ReadCount(BinaryReader br, uint version) => version == 1 ? br.ReadUInt32() : br.ReadUInt64();

    private static string ReadGgufString(BinaryReader br, uint version)
    {
        var len = ReadCount(br, version);
        if (len > 10_000_000) throw new InvalidDataException("GGUF string length out of range.");
        return Encoding.UTF8.GetString(br.ReadBytes((int)len));
    }

    /// <summary>GGUF metadata value type tags (from the GGUF spec), for skipping KV values.</summary>
    private const uint TUint8 = 0, TInt8 = 1, TUint16 = 2, TInt16 = 3, TUint32 = 4, TInt32 = 5,
        TFloat32 = 6, TBool = 7, TString = 8, TArray = 9, TUint64 = 10, TInt64 = 11, TFloat64 = 12;

    private static void SkipKv(BinaryReader br, uint version)
    {
        _ = ReadGgufString(br, version);   // key
        SkipValue(br, version, br.ReadUInt32());
    }

    private static void SkipValue(BinaryReader br, uint version, uint type)
    {
        switch (type)
        {
            case TUint8 or TInt8 or TBool: br.ReadByte(); break;
            case TUint16 or TInt16: br.ReadUInt16(); break;
            case TUint32 or TInt32 or TFloat32: br.ReadUInt32(); break;
            case TUint64 or TInt64 or TFloat64: br.ReadUInt64(); break;
            case TString: ReadGgufString(br, version); break;
            case TArray:
                var elemType = br.ReadUInt32();
                var count = ReadCount(br, version);
                for (ulong i = 0; i < count; i++)
                    SkipValue(br, version, elemType);
                break;
            default: throw new InvalidDataException($"Unknown GGUF value type {type}.");
        }
    }
}
