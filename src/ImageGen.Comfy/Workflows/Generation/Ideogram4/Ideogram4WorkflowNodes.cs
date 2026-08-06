using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Generation.Ideogram4;

/// <summary>Ideogram 4's own node ids beyond the inherited txt2img roles.</summary>
internal static class Ideogram4WorkflowNodes
{
    public const string UncondModel = "40";
    public const string NegativeZeroOut = "26";
    public const string CfgOverride = "2";
    public const string Guider = "22";
    public const string Sigmas = "17";
    public const string SamplerSelect = "16";
    public const string Noise = "18";
    public const string Sampler = "23";
}
