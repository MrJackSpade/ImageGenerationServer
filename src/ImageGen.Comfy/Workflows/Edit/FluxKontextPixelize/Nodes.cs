namespace ImageGen.Comfy.Edit.FluxKontextPixelize;

/// <summary>Own nodes (the model/clip/vae/source head is the inherited Nodes; FlattenOnWhite owns 11-14
/// internally).</summary>
internal static class Nodes
{
    public const string Positive = "60";
    public const string Scale = "62";
    public const string Encode = "63";
    public const string RefLatent = "64";
    public const string Guidance = "65";
    public const string NegativeZero = "66";
    public const string Projection = "35";
    public const string Sampler = "3";
    public const string Decode = "8";
    public const string Quantize = "36";
    public const string Save = "9";
}
