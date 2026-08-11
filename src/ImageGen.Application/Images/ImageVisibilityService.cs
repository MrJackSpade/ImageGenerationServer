using ImageGen.Domain;
using ImageGen.Domain.Repositories;

namespace ImageGen.Application.Images;

/// <summary>
/// One caller's permission to read one image id, minted only by <see cref="ImageVisibilityService"/>. It is what the
/// image-serving path takes instead of a bare id, so a route cannot reach the bytes without having asked.
/// </summary>
public sealed class ImageReadGrant
{
    internal ImageReadGrant(string imageId) => ImageId = imageId;

    /// <summary>The image id the grant was issued for.</summary>
    public string ImageId { get; }
}

/// <summary>
/// The single answer to "may this user read this image id". Every id-addressed image read goes through it, rather
/// than each endpoint deciding for itself — the per-endpoint re-decisions are how the unchecked routes arose.
///
/// <para>An id is readable when the caller owns the in-memory upload it names, or has a history row for it, or owns
/// the job whose slot produced it. Nothing else grants it: an id no source can place with anyone — a legacy id
/// predating the job tables, or one whose rows are gone — is refused, including to whoever made it. A
/// default-readable branch here would be the hole itself.</para>
///
/// <para>Infrastructure failures propagate. A database that cannot be reached is not an answer to an authorization
/// question, and returning "readable" (or "not readable") for one would either leak or lie about the image's
/// existence, with nothing in the response saying the check never ran.</para>
/// </summary>
public sealed class ImageVisibilityService(IUploadStore uploads, IImageVisibilityRepository visibility)
{
    private readonly IUploadStore _uploads = uploads;
    private readonly IImageVisibilityRepository _visibility = visibility;

    /// <summary>The caller's grant for <paramref name="imageId"/>, or null when the id is not theirs to read — which
    /// includes a null/blank id, since it is a caller-controlled route value rather than an internal invariant.</summary>
    public async Task<ImageReadGrant?> CanReadImageAsync(long userId, string? imageId, CancellationToken ct)
    {
        _ = Ensure.GreaterThanZero(userId);

        // The id is a route value the caller controls, so a blank one is garbage input, not a broken invariant: refuse
        // it as unreadable (the endpoints' normal 401 path) rather than faulting the request.
        if (string.IsNullOrWhiteSpace(imageId))
        {
            return null;
        }

        // The upload store is process-local and in hand, so it answers without a query — and it is the ONLY record of
        // an upload's owner, since an upload is never persisted.
        if (_uploads.Get(imageId) is { } upload)
        {
            return upload.OwnerUserId == userId ? new ImageReadGrant(imageId) : null;
        }

        return await _visibility.IsReadableAsync(userId, imageId, ct) ? new ImageReadGrant(imageId) : null;
    }

    /// <summary>The subset of <paramref name="imageIds"/> the caller may read, for the bulk id-addressed reads. One
    /// query for the stored ids, not one per id: this runs behind every gallery page.</summary>
    public async Task<IReadOnlySet<string>> ReadableAsync(
        long userId, IReadOnlyCollection<string> imageIds, CancellationToken ct)
    {
        _ = Ensure.GreaterThanZero(userId);

        HashSet<string> readable = new(StringComparer.Ordinal);
        List<string> stored = [];
        foreach (string id in imageIds)
        {
            if (_uploads.Get(id) is { } upload)
            {
                if (upload.OwnerUserId == userId)
                {
                    _ = readable.Add(id);
                }

                continue;
            }

            stored.Add(id);
        }

        readable.UnionWith(await _visibility.ReadableAsync(userId, stored, ct));
        return readable;
    }
}
