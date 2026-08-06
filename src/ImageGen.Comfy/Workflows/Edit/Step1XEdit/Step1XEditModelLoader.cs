using ImageGen.Comfy;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.Step1XEdit;

/// <summary>Step1X-Edit's self-contained loader (DiT fp8 + Flux AE + Qwen2.5-VL, int8-quantized + offloaded). The text
/// encoder is a Hugging Face folder name the node loads from its own directory, so it is a literal, not a bound file.</summary>
public sealed record Step1XEditModelLoader : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.Step1XEditModelLoader;
    [JsonPropertyName("diffusion_model")] public required string DiffusionModel { get; init; }
    [JsonPropertyName("vae")]             public required string Vae { get; init; }
    [JsonPropertyName("text_encoder")]    public required string TextEncoder { get; init; }
    [JsonPropertyName("dtype")]           public required string Dtype { get; init; }
    [JsonPropertyName("quantized")]       public required bool Quantized { get; init; }
    [JsonPropertyName("offload")]         public required bool Offload { get; init; }
    public static Output<Slot.Model> Out(string id) => new(id, 0);
}
