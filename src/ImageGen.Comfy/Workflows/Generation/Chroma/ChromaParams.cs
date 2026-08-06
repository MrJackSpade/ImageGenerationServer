using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Generation.Chroma;

/// <summary>Chroma's flow-shift knob (ModelSamplingAuraFlow).</summary>
public sealed record ChromaParams : Txt2ImgParams
{
    [JsonPropertyName(WorkflowParamKeys.Shift)] public required double Shift { get; init; }
}
