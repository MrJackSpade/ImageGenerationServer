using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Comfy.Edit.Pixelize;

/// <summary>This workflow's own role-named node ids, atop the inherited edit head and FlattenOnWhite nodes.</summary>
internal static class Nodes
{
    public const string WorkingScale = "30";
    public const string InitEncode = "31";
    public const string Positive = "32";
    public const string Guidance = "33";
    public const string Projection = "35";
    public const string Negative = "37";
    public const string Sampler = "3";
    public const string Decode = "8";
    public const string FinalQuantize = "36";
    public const string Save = "9";
}
