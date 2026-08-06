using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;
using ImageGen.Domain;
using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Comfy;

/// <summary>The MoE helper's node ids, named by role. The VALUE is the graph-local node key (preserved exactly, so
/// the emitted graph stays byte-identical); the NAME replaces the bare numeric literals at the use sites.</summary>
internal static class VidNodes
{
    public const string HighExpert = "4";
    public const string HighSampling = "5";
    public const string LowExpert = "41";
    public const string LowSampling = "51";
    public const string HighSampler = "3";
    public const string LowSampler = "31";
}
