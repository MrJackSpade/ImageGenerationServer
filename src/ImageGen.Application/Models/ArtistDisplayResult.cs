//TODO: CHECK FOR FALLBACKS
namespace ImageGen.Application.Models;

/// <summary>
/// The resolved display image for an artist: the chosen image id (null when the user has no generation for the
/// artist and no override) and whether it came from a manual override rather than the latest-generation fallback.
/// </summary>
/// <param name="ImageId">Gateway image id to display, or null when none is available.</param>
/// <param name="IsOverride">True when the image is a manual pick; false when it is the latest-generation fallback.</param>
public sealed record ArtistDisplayResult(string? ImageId, bool IsOverride);
