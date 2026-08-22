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

/// <summary>Output-canvas aspect choices offered by reference-only workflows. Conditioning uploads are never cropped;
/// a fixed shape sizes an empty target latent, while Reference follows image1 or the first attached image.</summary>
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
