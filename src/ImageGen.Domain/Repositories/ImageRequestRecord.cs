//TODO: CHECK FOR FALLBACKS
namespace ImageGen.Domain.Repositories;

/// <summary>
/// The decrypted render request that produced a given image, together with the id of the user who owns it so the
/// caller can gate access to the owner. Returned as null by the repository when no slot produced the image id.
/// </summary>
/// <param name="OwnerUserId">The user who owns the job that produced the image.</param>
/// <param name="RequestJson">The render request (parameters incl. the seed) as JSON, assembled from the slot's typed
/// columns and its reference rows — the only text field it carries is decrypted on the way out.</param>
public sealed record ImageRequestRecord(long OwnerUserId, string RequestJson);
