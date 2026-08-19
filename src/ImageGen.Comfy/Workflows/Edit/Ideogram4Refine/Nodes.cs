namespace ImageGen.Comfy.Edit.Ideogram4Refine;

/// <summary>Stable node ids for the Ideogram 4 refine graph.</summary>
internal static class Nodes
{
    public const string SourceScale = "11";
    public const string Encode = "12";
    public const string Positive = "13";
    public const string NegativeZeroOut = "26";
    public const string UncondModel = "40";
    public const string Debanner = "41";
    public const string CfgOverride = "2";
    public const string Guider = "22";
    public const string Sigmas = "17";
    public const string SourceSize = "19";
    public const string SplitSigmas = "27";
    public const string SamplerSelect = "16";
    public const string Noise = "18";
    public const string Sampler = "23";
    public const string Decode = "8";
    public const string Save = "9";
}
