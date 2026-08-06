using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.SdxlAnimateDiff;

/// <summary>This workflow's own node ids.</summary>
internal static class Nodes
{
    public const string ScaleSource = "11";
    public const string MotionLoad = "20";
    public const string ApplyMotion = "21";
    public const string EvolvedSampling = "22";
    public const string Positive = "13";
    public const string Negative = "12";
    public const string Encode = "26";
    public const string RepeatLatent = "27";
    public const string Sampler = "3";
    public const string Decode = "8";
    public const string Save = "9";
}
