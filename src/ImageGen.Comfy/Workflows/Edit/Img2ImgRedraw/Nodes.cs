using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Domain;
using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Comfy.Edit.Img2ImgRedraw;

/// <summary>Own nodes (the model/clip/vae/source head is the inherited Nodes).</summary>
internal static class Nodes
{
    public const string ClipSkip = "19";
    public const string TokenizerOptions = "17";
    public const string Positive = "13";
    public const string Negative = "14";
    public const string Guidance = "15";
    public const string ModelSampling = "16";
    public const string SourceScale = "11";
    public const string Encode = "12";
    public const string Sampler = "3";
    public const string Decode = "8";
    public const string Save = "9";
}
