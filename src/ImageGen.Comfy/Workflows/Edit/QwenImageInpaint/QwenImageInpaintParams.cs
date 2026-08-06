using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.QwenImageInpaint;

/// <summary>Inpaint params: the shared InstantX knobs with <c>denoise</c> floored at 0 (a KSampler at denoise 0 passes
/// the masked latent through unchanged — "don't change the region"; no arbitrary positive minimum).</summary>
public sealed record QwenImageInpaintParams : QwenInpaintParams
{
    [JsonPropertyName(WorkflowParamKeys.Denoise)]
    [Range(0.0, 1.0)] public override required double Denoise { get; init; }
}
