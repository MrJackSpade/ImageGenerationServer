using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;
using ImageGen.Domain;
using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Comfy.Generation.WanA14bT2V;

/// <summary>Wan 2.2 T2V-A14B's own node id: the MoE experts + samplers ("4"/"5"/"41"/"51"/"3"/"31") are written by Vid;
/// Clip/Vae/Positive/Negative/Decode/Save reuse the inherited txt2img roles; only the empty video latent is an own node.</summary>
internal static class WanA14bT2VWorkflowNodes
{
    public const string VideoLatent = "14";
}
