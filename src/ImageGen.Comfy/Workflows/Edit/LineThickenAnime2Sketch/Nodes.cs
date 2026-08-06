using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;

namespace ImageGen.Comfy.Edit.LineThickenAnime2Sketch;

/// <summary>This workflow's own node ids.</summary>
internal static class Nodes
{
    public const string Lineart = "20";
    public const string Invert = "21";
    public const string Size = "15";
    public const string Scale = "22";
    public const string Thicken = "23";
    public const string Blend = "24";
    public const string Save = "9";
}
