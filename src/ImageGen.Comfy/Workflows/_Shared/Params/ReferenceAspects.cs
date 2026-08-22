using ImageGen.Application.Rendering;

namespace ImageGen.Comfy;

/// <summary>Wire names for reference-workflow output aspect choices.</summary>
internal static class ReferenceAspectNames
{
    public const string Reference = "reference";
    public const string Square = "square";
    public const string Landscape = "landscape";
    public const string Portrait = "portrait";
}

/// <summary>Aspect choices offered by reference-driven workflows. The primary upload remains the authoritative
/// reference; fixed shapes are implemented as a centered crop before model-specific resolution normalization.</summary>
internal static class ReferenceAspects
{
    public static readonly string[] Choices =
    [
        ReferenceAspectNames.Reference,
        ReferenceAspectNames.Square,
        ReferenceAspectNames.Landscape,
        ReferenceAspectNames.Portrait,
    ];

    public static (int Width, int Height)? Ratio(string? value) => value switch
    {
        ReferenceAspectNames.Reference => null,
        ReferenceAspectNames.Square => (1, 1),
        ReferenceAspectNames.Landscape => (16, 9),
        ReferenceAspectNames.Portrait => (9, 16),
        _ => throw new RenderValidationException($"Unknown reference aspect '{value}'."),
    };
}
