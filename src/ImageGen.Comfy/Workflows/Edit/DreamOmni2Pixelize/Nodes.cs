using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;
using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Comfy.Edit.DreamOmni2Pixelize;

/// <summary>DreamOmni2PixelizeWorkflow's node ids (the source LoadImage reuses <c>EditNodes.Source</c>).</summary>
internal static class Nodes
{
    public const string Reference = "11";
    public const string Pipeline = "1";
    public const string Editor = "2";
    public const string FinalQuantize = "36";
    public const string Save = "9";
}
