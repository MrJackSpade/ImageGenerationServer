namespace ImageGen.Comfy.Edit.QwenPixelize;

/// <summary>QwenPixelizeWorkflow's own role-named node ids, atop the inherited edit head and FlattenOnWhite nodes.</summary>
internal static class Nodes
{
    public const string KontextScale = "20";
    public const string Encode = "22";
    public const string SnapScale = "25";
    public const string SourceEncode = "21";
    public const string RefLatent = "24";
    public const string ImageSize = "40";
    public const string EmptyLatentNode = "41";
    public const string ZeroNegative = "26";
    public const string ModelSampling = "2";
    public const string CfgNorm = "7";
    public const string Projection = "35";
    public const string Sampler = "3";
    public const string Decode = "8";
    public const string FinalQuantize = "36";
    public const string Save = "9";
}
