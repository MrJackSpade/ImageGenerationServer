using ImageGen.Comfy;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

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
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch { /* best-effort temp cleanup */ }
    }

    [Fact]
    public void A_matching_lora_is_compatible_and_a_mismatched_one_is_not_safetensors()
    {
        // Checkpoint carries layer dims {320, 1280, 2048}.
        string ckpt = Safetensors("ckpt.safetensors", new()
        {
            ["model.a.weight"] = [320, 2048],
            ["model.b.weight"] = [1280, 1280],
        });
        IReadOnlySet<long>? dims = LoraCompatibility.CheckpointDims(ckpt);
        Assert.NotNull(dims);

        // Matching LoRA: down [rank, 2048] and up [320, rank] → feature dims {2048, 320}, both present → compatible.
        string ok = Safetensors("ok.safetensors", new()
        {
            ["lora_unet_x.lora_down.weight"] = [16, 2048],
            ["lora_unet_x.lora_up.weight"] = [320, 16],
        });
        Assert.True(LoraCompatibility.Evaluate(ok, dims).Compatible);

        // Mismatched LoRA: feature dims {768, 320} — 768 is absent from the base → NOT compatible (all must be present).
        string bad = Safetensors("bad.safetensors", new()
        {
            ["lora_unet_x.lora_down.weight"] = [16, 768],
            ["lora_unet_x.lora_up.weight"] = [320, 16],
        });
        Assert.False(LoraCompatibility.Evaluate(bad, dims).Compatible);
    }

    [Fact]
    public void Clip_capability_is_detected_from_text_encoder_keys()
    {
        string modelOnly = Safetensors("mo.safetensors", new()
        {
            ["lora_unet_x.lora_down.weight"] = [16, 2048],
            ["lora_unet_x.lora_up.weight"] = [320, 16],
        });
        Assert.False(LoraCompatibility.Evaluate(modelOnly, null).ClipCapable);

        string withClip = Safetensors("clip.safetensors", new()
        {
            ["lora_te_text_model_x.lora_down.weight"] = [16, 768],
            ["lora_te_text_model_x.lora_up.weight"] = [768, 16],
        });
        Assert.True(LoraCompatibility.Evaluate(withClip, null).ClipCapable);
    }

    [Fact]
    public void A_gguf_checkpoint_header_yields_the_same_dimension_match()
    {
        string ckpt = Gguf("ckpt.gguf", new()
        {
            ["blk.0.attn.weight"] = [320, 2048],
            ["blk.1.ffn.weight"] = [1280, 1280],
        });
        IReadOnlySet<long>? dims = LoraCompatibility.CheckpointDims(ckpt);
        Assert.NotNull(dims);

        string ok = Safetensors("ok2.safetensors", new()
        {
            ["lora_unet_x.lora_down.weight"] = [16, 2048],
            ["lora_unet_x.lora_up.weight"] = [1280, 16],
        });
        Assert.True(LoraCompatibility.Evaluate(ok, dims).Compatible);
    }

    [Fact]
    public void Resaving_a_model_path_replaces_the_cached_version()
    {
        string path = Safetensors("resaved.safetensors", new()
        {
            ["model.weight"] = [320, 2048],
        });
        File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        IReadOnlySet<long>? first = LoraCompatibility.CheckpointDims(path);
        Assert.NotNull(first);
        Assert.Contains(2048, first);

        _ = Safetensors("resaved.safetensors", new()
        {
            ["model.weight"] = [320, 768],
        });
        File.SetLastWriteTimeUtc(path, new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        IReadOnlySet<long>? second = LoraCompatibility.CheckpointDims(path);
        Assert.NotNull(second);
        Assert.Contains(768, second);
        Assert.DoesNotContain(2048, second);
    }

    /// <summary>Write a minimal safetensors file: the 8-byte length + JSON header only (no tensor data — the parser
    /// reads the header alone).</summary>
    private string Safetensors(string file, Dictionary<string, long[]> tensors)
    {
        Dictionary<string, object> header = [];
        long offset = 0;
        foreach ((string? name, long[]? shape) in tensors)
        {
            long numel = 1;
            foreach (long d in shape)
            {
                numel *= d;
            }

            long bytes = numel * 4;
            header[name] = new { dtype = "F32", shape, data_offsets = new[] { offset, offset + bytes } };
            offset += bytes;
        }

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(header);

        string path = Path.Combine(_dir, file);
        using FileStream fs = File.Create(path);
        Span<byte> len = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(len, (ulong)json.Length);
        fs.Write(len);
        fs.Write(json);
        return path;
    }

    /// <summary>Write a minimal GGUF file: magic, version, counts, no metadata KVs, then the tensor infos.</summary>
    private string Gguf(string file, Dictionary<string, long[]> tensors)
    {
        string path = Path.Combine(_dir, file);
        using FileStream fs = File.Create(path);
        using BinaryWriter bw = new(fs);
        bw.Write(0x46554747u);          // "GGUF"
        bw.Write(3u);                   // version
        bw.Write((ulong)tensors.Count); // tensor count
        bw.Write(0ul);                  // metadata KV count (none, for simplicity)
        foreach ((string? name, long[]? shape) in tensors)
        {
            byte[] nb = Encoding.UTF8.GetBytes(name);
            bw.Write((ulong)nb.Length);
            bw.Write(nb);
            bw.Write((uint)shape.Length);
            foreach (long d in shape)
            {
                bw.Write((ulong)d);
            }

            bw.Write(0u);   // ggml type
            bw.Write(0ul);  // data offset
        }

        return path;
    }
}
