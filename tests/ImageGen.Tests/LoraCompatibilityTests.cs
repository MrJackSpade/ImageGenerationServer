//TODO: CHECK FOR FALLBACKS
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using ImageGen.Comfy;

namespace ImageGen.Tests;

/// <summary>
/// The header-only, dimension-based LoRA compatibility check (<see cref="LoraCompatibility"/>): parse a
/// <c>.safetensors</c> and a <c>.gguf</c> header, and decide whether a LoRA's feature dimensions are all present in a
/// checkpoint. A true match is never dimmed; a LoRA targeting a dimension the base lacks is.
/// </summary>
public sealed class LoraCompatibilityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "loracompat-" + Guid.NewGuid().ToString("N"));

    public LoraCompatibilityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    [Fact]
    public void A_matching_lora_is_compatible_and_a_mismatched_one_is_not_safetensors()
    {
        // Checkpoint carries layer dims {320, 1280, 2048}.
        var ckpt = Safetensors("ckpt.safetensors", new()
        {
            ["model.a.weight"] = [320, 2048],
            ["model.b.weight"] = [1280, 1280],
        });
        var dims = LoraCompatibility.CheckpointDims(ckpt);
        Assert.NotNull(dims);

        // Matching LoRA: down [rank, 2048] and up [320, rank] → feature dims {2048, 320}, both present → compatible.
        var ok = Safetensors("ok.safetensors", new()
        {
            ["lora_unet_x.lora_down.weight"] = [16, 2048],
            ["lora_unet_x.lora_up.weight"] = [320, 16],
        });
        Assert.True(LoraCompatibility.Evaluate(ok, dims).Compatible);

        // Mismatched LoRA: feature dims {768, 320} — 768 is absent from the base → NOT compatible (all must be present).
        var bad = Safetensors("bad.safetensors", new()
        {
            ["lora_unet_x.lora_down.weight"] = [16, 768],
            ["lora_unet_x.lora_up.weight"] = [320, 16],
        });
        Assert.False(LoraCompatibility.Evaluate(bad, dims).Compatible);
    }

    [Fact]
    public void Clip_capability_is_detected_from_text_encoder_keys()
    {
        var modelOnly = Safetensors("mo.safetensors", new()
        {
            ["lora_unet_x.lora_down.weight"] = [16, 2048],
            ["lora_unet_x.lora_up.weight"] = [320, 16],
        });
        Assert.False(LoraCompatibility.Evaluate(modelOnly, null).ClipCapable);

        var withClip = Safetensors("clip.safetensors", new()
        {
            ["lora_te_text_model_x.lora_down.weight"] = [16, 768],
            ["lora_te_text_model_x.lora_up.weight"] = [768, 16],
        });
        Assert.True(LoraCompatibility.Evaluate(withClip, null).ClipCapable);
    }

    [Fact]
    public void A_gguf_checkpoint_header_yields_the_same_dimension_match()
    {
        var ckpt = Gguf("ckpt.gguf", new()
        {
            ["blk.0.attn.weight"] = [320, 2048],
            ["blk.1.ffn.weight"] = [1280, 1280],
        });
        var dims = LoraCompatibility.CheckpointDims(ckpt);
        Assert.NotNull(dims);

        var ok = Safetensors("ok2.safetensors", new()
        {
            ["lora_unet_x.lora_down.weight"] = [16, 2048],
            ["lora_unet_x.lora_up.weight"] = [1280, 16],
        });
        Assert.True(LoraCompatibility.Evaluate(ok, dims).Compatible);
    }

    /// <summary>Write a minimal safetensors file: the 8-byte length + JSON header only (no tensor data — the parser
    /// reads the header alone).</summary>
    private string Safetensors(string file, Dictionary<string, long[]> tensors)
    {
        var header = new Dictionary<string, object>();
        long offset = 0;
        foreach (var (name, shape) in tensors)
        {
            long numel = 1;
            foreach (var d in shape) numel *= d;
            var bytes = numel * 4;
            header[name] = new { dtype = "F32", shape, data_offsets = new[] { offset, offset + bytes } };
            offset += bytes;
        }
        var json = JsonSerializer.SerializeToUtf8Bytes(header);

        var path = Path.Combine(_dir, file);
        using var fs = File.Create(path);
        Span<byte> len = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(len, (ulong)json.Length);
        fs.Write(len);
        fs.Write(json);
        return path;
    }

    /// <summary>Write a minimal GGUF file: magic, version, counts, no metadata KVs, then the tensor infos.</summary>
    private string Gguf(string file, Dictionary<string, long[]> tensors)
    {
        var path = Path.Combine(_dir, file);
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write(0x46554747u);          // "GGUF"
        bw.Write(3u);                   // version
        bw.Write((ulong)tensors.Count); // tensor count
        bw.Write(0ul);                  // metadata KV count (none, for simplicity)
        foreach (var (name, shape) in tensors)
        {
            var nb = Encoding.UTF8.GetBytes(name);
            bw.Write((ulong)nb.Length);
            bw.Write(nb);
            bw.Write((uint)shape.Length);
            foreach (var d in shape) bw.Write((ulong)d);
            bw.Write(0u);   // ggml type
            bw.Write(0ul);  // data offset
        }
        return path;
    }
}
