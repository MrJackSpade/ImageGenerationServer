using ImageGen.Comfy;

namespace ImageGen.Tests;

/// <summary>
/// Collapsing a HuggingFace-sharded model's per-shard listing down to the one entry a loader can consume (issue #184).
/// The point is that no individual <c>…-&lt;n&gt;-of-&lt;m&gt;.safetensors</c> shard ever survives into the picker —
/// binding a slot to a 1/m slice of the weights is always wrong.
/// </summary>
public sealed class HuggingFaceShardsTests
{
    [Fact]
    public void A_folder_of_shards_collapses_to_the_folder()
    {
        IReadOnlyList<string> result = HuggingFaceShards.Collapse(
        [
            "Qwen2.5-VL-7B-Instruct/model-00001-of-00005.safetensors",
            "Qwen2.5-VL-7B-Instruct/model-00002-of-00005.safetensors",
            "Qwen2.5-VL-7B-Instruct/model-00003-of-00005.safetensors",
            "Qwen2.5-VL-7B-Instruct/model-00004-of-00005.safetensors",
            "Qwen2.5-VL-7B-Instruct/model-00005-of-00005.safetensors",
        ]);

        Assert.Equal(["Qwen2.5-VL-7B-Instruct"], result);
    }

    [Fact]
    public void Backslash_separators_collapse_the_same_way()
    {
        IReadOnlyList<string> result = HuggingFaceShards.Collapse(
        [
            @"DreamOmni2-vlm-model\model-00001-of-00004.safetensors",
            @"DreamOmni2-vlm-model\model-00002-of-00004.safetensors",
            @"DreamOmni2-vlm-model\model-00003-of-00004.safetensors",
            @"DreamOmni2-vlm-model\model-00004-of-00004.safetensors",
        ]);

        Assert.Equal(["DreamOmni2-vlm-model"], result);
    }

    [Fact]
    public void Rootless_shards_collapse_to_their_index_file()
    {
        IReadOnlyList<string> result = HuggingFaceShards.Collapse(
        [
            "model-00001-of-00003.safetensors",
            "model-00002-of-00003.safetensors",
            "model-00003-of-00003.safetensors",
        ]);

        Assert.Equal(["model.safetensors.index.json"], result);
    }

    [Fact]
    public void Non_shard_files_pass_through_untouched_and_in_order()
    {
        IReadOnlyList<string> result = HuggingFaceShards.Collapse(
        [
            "clip_l.safetensors",
            "t5xxl_fp16.safetensors",
            "umt5_xxl_fp8_e4m3fn_scaled.safetensors",
        ]);

        Assert.Equal(
            ["clip_l.safetensors", "t5xxl_fp16.safetensors", "umt5_xxl_fp8_e4m3fn_scaled.safetensors"],
            result);
    }

    [Fact]
    public void A_shard_set_collapses_in_place_leaving_surrounding_files_where_they_were()
    {
        IReadOnlyList<string> result = HuggingFaceShards.Collapse(
        [
            "clip_l.safetensors",
            "llava-llama-3-8b-text-encoder-tokenizer/model-00001-of-00004.safetensors",
            "llava-llama-3-8b-text-encoder-tokenizer/model-00002-of-00004.safetensors",
            "llava-llama-3-8b-text-encoder-tokenizer/model-00003-of-00004.safetensors",
            "llava-llama-3-8b-text-encoder-tokenizer/model-00004-of-00004.safetensors",
            "t5xxl_fp16.safetensors",
        ]);

        Assert.Equal(
            [
                "clip_l.safetensors",
                "llava-llama-3-8b-text-encoder-tokenizer",
                "t5xxl_fp16.safetensors",
            ],
            result);
    }

    [Fact]
    public void Non_contiguous_shards_still_emit_one_representative()
    {
        IReadOnlyList<string> result = HuggingFaceShards.Collapse(
        [
            "vlm/model-00001-of-00002.safetensors",
            "clip_l.safetensors",
            "vlm/model-00002-of-00002.safetensors",
        ]);

        Assert.Equal(["vlm", "clip_l.safetensors"], result);
    }

    [Fact]
    public void A_lone_shard_is_still_hidden_behind_its_folder()
    {
        // Even one shard means the model is sharded and this file is a fraction of it — never a bindable option.
        IReadOnlyList<string> result = HuggingFaceShards.Collapse(
            ["Qwen2.5-VL-7B-Instruct/model-00003-of-00005.safetensors"]);

        Assert.Equal(["Qwen2.5-VL-7B-Instruct"], result);
    }
}
