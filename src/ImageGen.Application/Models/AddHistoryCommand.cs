using ImageGen.Domain.Entities;

namespace ImageGen.Application.Models;

/// <summary>A request to record one generated image in a user's history.</summary>
public sealed record AddHistoryCommand
{
    public required long UserId { get; init; }
    public required string GatewayImageId { get; init; }
    public required string Prompt { get; init; }
    public required string ModelFriendly { get; init; }
    public required string ModelId { get; init; }
    public required string Aspect { get; init; }
    public required DateTime CreatedAtUtc { get; init; }
    public required IReadOnlyList<Mark> Marks { get; init; }

    public HistoryEntry ToEntity() => new()
    {
        UserId = UserId,
        GatewayImageId = GatewayImageId,
        Prompt = Prompt,
        ModelFriendly = ModelFriendly,
        ModelId = ModelId,
        Aspect = Aspect,
        CreatedAtUtc = CreatedAtUtc,
        Marks = Marks,
    };
}
