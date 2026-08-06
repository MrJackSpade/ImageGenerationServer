using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;
using ImageGen.Domain;

namespace ImageGen.Comfy.Edit.SeedVr2Upscale;

/// <summary>SeedVR2's own loader/upscale node ids (source LoadImage reuses the inherited <c>EditNodes.Source</c>).</summary>
internal static class Nodes
{
    public const string Dit = "30";
    public const string Vae = "31";
    public const string Upscale = "32";
    public const string Save = "9";
}
