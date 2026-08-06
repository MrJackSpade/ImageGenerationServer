namespace ImageGen.Comfy.Edit.FluxKontextEdit;

/// <summary>FluxKontextEditWorkflow's own node ids (the model/clip/vae/source head is the inherited <c>EditNodes</c>).
/// Two FluxKontextImageScale and two VAEEncode are disambiguated by input: the source vs the stitched source+refs.</summary>
internal static class Nodes
{
    public const string Positive = "13";
    public const string SourceScale = "11";
    public const string SourceEncode = "12";
    public const string StitchScale = "18";
    public const string StitchEncode = "19";
    public const string RefLatent = "15";
    public const string Guidance = "14";
    public const string NegativeZero = "16";
    public const string Sampler = "3";
    public const string Decode = "8";
    public const string Save = "9";
}
