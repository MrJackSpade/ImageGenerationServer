namespace ImageGen.Comfy;

/// <summary>The closed set of aspect names the workflows understand — the sub-key of a configuration's aspect→[w,h]
/// dims map and the value <see cref="ComfyGraph.NormalizeAspect"/> resolves a request to. Written once here so the
/// normalizer, the dims lookup, and any comparison share one spelling.</summary>
internal static class Aspects
{
    public const string Square = "square";
    public const string Landscape = "landscape";
    public const string Portrait = "portrait";
}
