using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.Flux2Klein4bPixelize;

/// <summary>Flux2Klein4bPixelizeWorkflow's node ids.</summary>
internal static class Nodes
{
    public const string Positive = "60";
    public const string ScaledImage = "62";
    public const string Encode = "63";
    public const string ImageSize = "64";
    public const string Guidance = "65";
    public const string RefLatent = "66";
    public const string Projection = "35";
    public const string Guider = "22";
    public const string EmptyLatentNode = "28";
    public const string Scheduler = "29";
    public const string Noise = "20";
    public const string SamplerSelect = "21";
    public const string SplitSigmas = "27";
    public const string Sampler = "23";
    public const string Decode = "8";
    public const string FinalQuantize = "36";
    public const string Save = "9";
}
