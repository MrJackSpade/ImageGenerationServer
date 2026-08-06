namespace ImageGen.Comfy;

/// <summary>Own node ids (source LoadImage is the inherited <c>EditNodes.Source</c>). These must NOT reuse the
/// inherited loader-head ids — <c>EditNodes.Clip</c> ("5") / <c>EditNodes.Vae</c> ("6") carry the live CLIP/VAE loaders
/// that <c>clip0</c>/<c>vae0</c> point at; the split-loader path keeps them, so reusing "5"/"6" here would
/// overwrite the loaders and leave the clip/vae edges dangling into this node's own outputs.</summary>
internal static class MageFlowEditNodes
{
    public const string ScaledSource = "11";
    public const string Encode = "7";
    public const string Sampler = "12";
    public const string Decode = "8";
    public const string Save = "9";
}
