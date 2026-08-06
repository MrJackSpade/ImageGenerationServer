using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.Krea2Redraw;

/// <summary>This workflow's own nodes; the model/CLIP/VAE/source head reuses <see cref="EditWorkflow{TParams}.Nodes"/>.</summary>
internal static class Nodes
{
    public const string Encode = "12";
    public const string Positive = "13";
    public const string Negative = "14";
    public const string Rebalance = "15";
    public const string Sampler = "3";
    public const string Decode = "8";
    public const string Save = "9";
}
