using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;

namespace ImageGen.Comfy.Edit.LineThickenErode;

/// <summary>This workflow's own node ids.</summary>
internal static class Nodes
{
    public const string Thicken = "20";
    public const string Save = "9";
}
