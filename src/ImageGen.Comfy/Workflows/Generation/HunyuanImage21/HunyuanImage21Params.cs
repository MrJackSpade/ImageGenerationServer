using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Generation.HunyuanImage21;

/// <summary>HunyuanImage 2.1's flow-shift knob (ModelSamplingSD3).</summary>
public sealed record HunyuanImage21Params : Txt2ImgParams
{
    [JsonPropertyName(WorkflowParamKeys.Shift)]
    [Range(1.0, 12.0)] public required double Shift { get; init; }
}
