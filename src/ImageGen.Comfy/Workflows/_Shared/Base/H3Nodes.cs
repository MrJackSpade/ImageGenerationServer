using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;
using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Comfy;

/// <summary>The shared T2V/I2V graph's node ids, named by role. The VALUE is the graph-local node key (preserved
/// exactly, so the emitted graph stays byte-identical); the NAME replaces the bare numeric literals at the use
/// sites.</summary>
internal static class H3Nodes
{
    public const string Model = "4";
    public const string Clip = "20";
    public const string VideoVae = "21";
    public const string AudioVae = "22";
    public const string Source = "10";
    public const string ScaledSource = "11";
    public const string SourceSize = "15";
    public const string EndFrame = "12";
    public const string ScaledEndFrame = "13";
    public const string Encode = "14";
    public const string Scheduler = "55";
    public const string SamplerSelect = "56";
    public const string Noise = "57";
    public const string Guider = "58";
    public const string Sampler = "3";
    public const string VideoDecode = "8";
    public const string AudioDecode = "40";
    public const string CreateVideo = "41";
    public const string Save = "9";
}
