namespace ImageGen.Comfy;

/// <summary>
/// The single source of truth for the shared workflow-parameter numeric bounds — written once here and referenced by
/// BOTH the UI-facing <see cref="ParamSpec.Min"/>/<see cref="ParamSpec.Max"/> on the shared schemas AND the
/// <c>[Range]</c> validation attributes on the typed params DTOs (enforced reflectively at the
/// <see cref="ParamsCodec"/> boundary). One constant feeding both is what keeps the bound the SLIDER shows and the
/// bound the server ENFORCES from ever drifting apart: anything the UI lets a user pick is, by construction, within
/// the enforced range, and only an out-of-band API/MCP value trips the guard.
/// <para>These are the model authors' declared ranges (mirrored from the schemas that already carried them), not
/// invented caps. A per-config UI override may TIGHTEN a bound (a narrower slider) but must stay inside these — the
/// hard contract — which the catalog-load consistency check enforces.</para>
/// </summary>
internal static class ParamBounds
{
    /// <summary>Sampler step count. Below 1 renders nothing; the ceiling is the declared schema max (a 5000-step
    /// request bypassing the slider is exactly what this rejects server-side).</summary>
    public const int StepsMin = 1;
    public const int StepsMax = 100;

    /// <summary>Classifier-free guidance scale. 1 disables CFG (distilled models); the ceiling matches the schema.</summary>
    public const double CfgMin = 1;
    public const double CfgMax = 30;

    /// <summary>Denoise / source↔motion strength on the edit path. The floor is 0: a KSampler at denoise 0 passes the
    /// latent through unchanged (a no-op), so 0 is a valid "don't redraw / don't refine" input, never an arbitrary
    /// taste floor to clamp up from. 1.0 is a full redraw.</summary>
    public const double DenoiseMin = 0.0;
    public const double DenoiseMax = 1.0;

    /// <summary>Optional base-model LoRA strength — the generate path allows up to 2.0, the edit path up to 1.5
    /// (the ranges the two shared schemas already declared).</summary>
    public const double GenLoraStrengthMin = 0.0;
    public const double GenLoraStrengthMax = 2.0;
    public const double EditLoraStrengthMin = 0.0;
    public const double EditLoraStrengthMax = 1.5;
}
