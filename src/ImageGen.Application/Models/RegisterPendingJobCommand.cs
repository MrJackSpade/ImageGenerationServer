//TODO: CHECK FOR FALLBACKS
using ImageGen.Domain.Entities;

namespace ImageGen.Application.Models;

/// <summary>A request to register a just-submitted gateway job so the reconciler can record its result.</summary>
public sealed record RegisterPendingJobCommand
{
    public required long UserId { get; init; }
    public required string JobId { get; init; }
    public required string Prompt { get; init; }
    public required string ModelFriendly { get; init; }
    public required string ModelId { get; init; }
    public required string Aspect { get; init; }
    public required DateTime CreatedAtUtc { get; init; }

    public PendingJob ToEntity() => new()
    {
        UserId = UserId,
        JobId = JobId,
        Prompt = Prompt,
        ModelFriendly = ModelFriendly,
        ModelId = ModelId,
        Aspect = Aspect,
        CreatedAtUtc = CreatedAtUtc,
    };
}
