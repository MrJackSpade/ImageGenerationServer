using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;

namespace ImageGen.Comfy.Edit.BooguEdit;

/// <summary>This workflow's own node ids.</summary>
internal static class Nodes
{
    public const string ScaledSource = "11";
    public const string ModelSampling = "33";
    public const string Encode = "13";
    public const string SourceSize = "17";
    public const string Latent = "50";
    public const string SamplerSelect = "16";
    public const string Sigmas = "26";
    public const string Sampler = "3";
    public const string Decode = "8";
    public const string Save = "9";
}
