using ImageGen.Comfy;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.DreamOmni2Edit;

/// <summary>The DreamOmni2 editor: drives the pipeline over a source + reference image and an instruction.</summary>
public sealed record RunningHubDreamOmni2Editor : ComfyNode
{
    internal override string ClassType => ComfyNodeTypes.RunningHubDreamOmni2Editor;
    [JsonPropertyName("pipeline")]            public required Output<Slot.Model> Pipeline { get; init; }
    [JsonPropertyName("src_image")]           public required Output<Slot.Image> SrcImage { get; init; }
    [JsonPropertyName("ref_image")]           public required Output<Slot.Image> RefImage { get; init; }
    [JsonPropertyName("prompt")]              public required string Prompt { get; init; }
    [JsonPropertyName("num_inference_steps")] public required int NumInferenceSteps { get; init; }
    [JsonPropertyName("guidance_scale")]      public required double GuidanceScale { get; init; }
    [JsonPropertyName("seed")]                public required long Seed { get; init; }
    public static Output<Slot.Image> Out(string id) => new(id, 0);
}
