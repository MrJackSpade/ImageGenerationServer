using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;
using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Comfy.Edit.PixelQuantize;

/// <summary>This workflow's own node ids (source LoadImage is the inherited EditNodes.Source; flatten-on-white nodes live in PixelHarnessGraph).</summary>
internal static class Nodes
{
    public const string Matte = "15";
    public const string Quantize = "20";
    public const string Save = "9";
}
