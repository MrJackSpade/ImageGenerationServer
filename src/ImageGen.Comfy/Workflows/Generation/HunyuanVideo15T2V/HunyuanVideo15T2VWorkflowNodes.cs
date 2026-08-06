using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;
using ImageGen.Domain;
using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Comfy.Generation.HunyuanVideo15T2V;

/// <summary>HunyuanVideo 1.5 t2v's own node ids beyond the inherited txt2img roles
/// (Model/Clip/Vae/Positive/Negative/Sampler/Decode/Save reused).</summary>
internal static class HunyuanVideo15T2VWorkflowNodes
{
    public const string ModelSampling = "30";
    public const string VideoLatent = "14";
    public const string Scheduler = "55";
    public const string SamplerSelect = "56";
    public const string Noise = "57";
    public const string Guider = "58";
}
