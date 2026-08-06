using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.QwenImageOutpaint;

/// <summary>Outpaint params: the shared InstantX knobs with <c>denoise</c> floored at 0. The default stays a full
/// regenerate (a low denoise smears the pre-fill scaffold back — see the workflow's <c>DefaultDenoise</c>), but that is
/// a reason not to PICK a low value, not a reason to reject one: 0 passes the padded latent through unchanged.</summary>
public sealed record QwenImageOutpaintParams : QwenInpaintParams
{
    [JsonPropertyName(WorkflowParamKeys.Denoise)]
    [Range(0.0, 1.0)] public override required double Denoise { get; init; }
}
