using ImageGen.Comfy;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.Step1XEdit;

/// <summary>Step1X-Edit generation node — instruction edit over the input image at a target size level.</summary>
public sealed record Step1XEditGenerate : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.Step1XEditGenerate;
    [JsonPropertyName("model")]           public required Output<Slot.Model> Model { get; init; }
    [JsonPropertyName("input_image")]     public required Output<Slot.Image> InputImage { get; init; }
    [JsonPropertyName("prompt")]          public required string Prompt { get; init; }
    [JsonPropertyName("negative_prompt")] public required string NegativePrompt { get; init; }
    [JsonPropertyName("num_steps")]       public required int NumSteps { get; init; }
    [JsonPropertyName("cfg_guidance")]    public required double CfgGuidance { get; init; }
    [JsonPropertyName("seed")]            public required long Seed { get; init; }
    [JsonPropertyName("size_level")]      public required int SizeLevel { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}
