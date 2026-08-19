using System.Text.Json.Serialization;

namespace ImageGen.Domain.Entities;

/// <summary>Portable width/height pair used in persisted render provenance.</summary>
public sealed record PixelDimensions(
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height);

/// <summary>The source, resolved working canvas, and actual stored output size for one render.</summary>
public sealed record RenderDimensions
{
    [JsonPropertyName("policy")] public required string Policy { get; init; }
    [JsonPropertyName("input")] public PixelDimensions? Input { get; init; }
    [JsonPropertyName("working")] public PixelDimensions? Working { get; init; }
    [JsonPropertyName("output")] public PixelDimensions? Output { get; init; }
}
