namespace ImageGen.Comfy;

/// <summary>Stable wire tokens describing how a workflow chooses output dimensions.</summary>
public static class OutputSizePolicies
{
    public const string ExactSource = "exact-source";
    public const string NormalizedNative = "normalized-native";
    public const string ExpandedCanvas = "expanded-canvas";
    public const string ExplicitRequested = "explicit-requested";
}
