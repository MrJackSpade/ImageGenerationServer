//TODO: CHECK FOR FALLBACKS
using ImageGen.Domain.Entities;

namespace ImageGen.Application.Models;

/// <summary>A request to bookmark an image (a self-contained copy of its metadata).</summary>
public sealed record AddImageBookmarkCommand
{
    public required long UserId { get; init; }
    public required string GatewayImageId { get; init; }
    public required string Prompt { get; init; }
    public required string ModelFriendly { get; init; }
    public required string ModelId { get; init; }
    public required string Aspect { get; init; }
    public required DateTime OriginalCreatedAtUtc { get; init; }
    public required IReadOnlyList<Mark> Marks { get; init; }

    public ImageBookmark ToEntity(DateTime savedAtUtc) => new()
    {
        UserId = UserId,
        GatewayImageId = GatewayImageId,
        Prompt = Prompt,
        ModelFriendly = ModelFriendly,
        ModelId = ModelId,
        Aspect = Aspect,
        OriginalCreatedAtUtc = OriginalCreatedAtUtc,
        SavedAtUtc = savedAtUtc,
        Marks = Marks,
    };
}
