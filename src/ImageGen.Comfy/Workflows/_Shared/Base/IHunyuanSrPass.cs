using System.Text.Json;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;
using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Comfy;

/// <summary>The super-resolution pass CONTRACT — a HunyuanVideo 1.5 params record implements this exactly when its config
/// asked for SR (the concrete <c>*SrParams</c> subtype; a non-SR config is a different, SR-less params shape). Every knob
/// is non-null: an SR config supplies them all, so there is nothing to guard at read time (audit #125 C). Consumed by
/// <see cref="HunyuanSr.Refine"/>; the toggle is the PRESENCE of this interface, resolved by <see cref="HunyuanSr.PassOf"/>.</summary>
public interface IHunyuanSrPass
{
    /// <summary>The SR distilled UNet filename (resolved model ref).</summary>
    string SrModel { get; }
    /// <summary>The latent upsampler filename (resolved model ref).</summary>
    string SrUpsampler { get; }
    /// <summary>The SR latent-upscale target width.</summary>
    int SrWidth { get; }
    /// <summary>The SR latent-upscale target height.</summary>
    int SrHeight { get; }
    /// <summary>The SR refine step count.</summary>
    int SrSteps { get; }
    /// <summary>The SR refine denoise fraction.</summary>
    double SrDenoise { get; }
    /// <summary>The SR noise-augmentation amount fed to the SR conditioning node.</summary>
    double SrNoiseAug { get; }
    /// <summary>The SR real-CFG scale.</summary>
    double SrCfg { get; }
    /// <summary>The SR model's flow shift.</summary>
    double SrShift { get; }
}
