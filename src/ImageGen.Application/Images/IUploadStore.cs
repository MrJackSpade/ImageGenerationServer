using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Application.Images;

/// <summary>An image the user handed us — an edit source, a reference image, an inpaint mask, an i2v end frame.
/// Held in memory only; see <see cref="IUploadStore"/>.</summary>
/// <param name="Bytes">The raw file exactly as uploaded.</param>
/// <param name="ContentType">The declared content type (defaulted to image/png when the client sent none).</param>
/// <param name="Width">Pixel width, or null when the bytes had no identifiable image header.</param>
/// <param name="Height">Pixel height, or null when the bytes had no identifiable image header.</param>
/// <param name="OwnerUserId">The user who handed it over. An upload id is served back by the image routes, and it is
/// the only ownership record there is for one — nothing about an upload is ever written to the database.</param>
public sealed record UploadedImage(
    byte[] Bytes,
    string ContentType,
    [property: AllowNullable("null = the bytes had no identifiable image header, so no dimensions; distinct from a 0px default")] int? Width,
    [property: AllowNullable("null = the bytes had no identifiable image header, so no dimensions; distinct from a 0px default")] int? Height,
    long OwnerUserId);

/// <summary>
/// Process-local store for uploaded images, keyed by a minted id.
/// <para>
/// Uploads are deliberately NOT persisted. They are inputs to a render, never outputs: nothing in the UI can retrieve
/// one after the fact (they never enter history, the library, or a bookmark), so a durable row would be write-only
/// data.
/// </para>
/// <para>
/// Nothing here expires. An id this store has issued resolves for the life of the process, however long its job waits
/// in the queue: an accepted job must stay renderable, so the store may not decide on its own to drop an input it has
/// already handed out. Anything that would bound residency (a byte budget, an LRU, a TTL) makes queue depth destroy
/// queued work and is not to be reintroduced.
/// </para>
/// <para>
/// Consequence to know about: an edit slot that is queued but not yet submitted to ComfyUI does not survive an app
/// restart, because its source bytes died with the process (an already-submitted render is fine — ComfyUI holds the
/// bytes). The render path reports that as a slot error rather than a crash.
/// </para>
/// </summary>
public interface IUploadStore
{
    /// <summary>Take an uploaded image — with the user it belongs to — into memory under a freshly minted,
    /// globally-unique id; returns that id.</summary>
    string Add(UploadedImage image);

    /// <summary>The upload for an id, or null if this process never issued it.</summary>
    UploadedImage? Get(string id);
}
