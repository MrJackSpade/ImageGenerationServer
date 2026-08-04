//TODO: CHECK FOR FALLBACKS
namespace ImageGen.Domain.Entities;

/// <summary>One LoRA's cached preview media: the raw bytes and their content type (e.g. <c>image/jpeg</c> or
/// <c>video/mp4</c> — some CivitAI previews are short clips, which is why the type travels with the bytes).</summary>
public sealed record LoraPreviewBlob(byte[] Bytes, string ContentType);
