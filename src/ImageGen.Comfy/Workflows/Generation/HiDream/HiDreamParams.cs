using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Generation.HiDream;

/// <summary>HiDream's flow-shift knob (ModelSamplingSD3).</summary>
public sealed record HiDreamParams : Txt2ImgParams
{
    [JsonPropertyName(WorkflowParamKeys.Shift)] public required double Shift { get; init; }
}
