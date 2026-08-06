using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;
using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Comfy.Edit.PixelQuantizeVideo;

/// <summary>Node ids named by role. Values preserved exactly.</summary>
internal static class Nodes
{
    public const string Source = "10";
    public const string Frames = "11";
    public const string Matte = "15";
    public const string Quantize = "20";
    public const string Save = "9";
}
