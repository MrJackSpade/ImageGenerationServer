using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;

namespace ImageGen.Comfy.Edit.Upscale;

/// <summary>UpscaleWorkflow's own node ids (source LoadImage reuses the inherited <c>EditNodes.Source</c>).</summary>
internal static class Nodes
{
    public const string UpscaleModel = "20";
    public const string Upscale = "21";
    public const string Resample = "22";
    public const string Save = "9";
}
