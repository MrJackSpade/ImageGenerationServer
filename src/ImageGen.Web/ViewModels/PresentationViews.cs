namespace ImageGen.Web.ViewModels;

/// <summary>
/// A history image as a grid card renders it (gallery / artist / tag-filter pages). A presentation DTO — the Razor
/// views bind to this, not to the domain <c>HistoryEntry</c>, so the entity never leaks into the view layer.
/// </summary>
/// <param name="GatewayImageId">The image id (used in the card link + image url).</param>
/// <param name="Prompt">The prompt text shown under the card.</param>
/// <param name="ModelFriendly">The model's display name shown on the card.</param>
/// <param name="Ts">When it was generated, as a millisecond epoch — client JS formats it in the browser's zone.</param>
/// <param name="Viewed">Whether the user has opened this image. The card is outlined until they have.</param>
public sealed record HistoryItemView(string GatewayImageId, string Prompt, string ModelFriendly, long Ts, bool Viewed);

/// <summary>
/// A bookmarked image as the bookmarks grid renders it. Presentation DTO over <c>ImageBookmark</c>. Carries the same
/// fields as the detail card's record (id, prompt, model, aspect, ts, marks) so the bookmarks page can emit a JSON
/// record for the category long-press without a second round trip.
/// </summary>
/// <param name="GatewayImageId">The image id.</param>
/// <param name="Prompt">The prompt text.</param>
/// <param name="ModelFriendly">The model's display name.</param>
/// <param name="ModelId">The configuration id.</param>
/// <param name="Aspect">The aspect label.</param>
/// <param name="Ts">When the image was originally generated, as a millisecond epoch.</param>
/// <param name="Marks">Canonical token → "tag"|"artist" for the marked tokens (empty when none).</param>
public sealed record ImageBookmarkView(
    string GatewayImageId,
    string Prompt,
    string ModelFriendly,
    string ModelId,
    string Aspect,
    long Ts,
    IReadOnlyDictionary<string, string> Marks);

/// <summary>
/// The image the detail page / lightbox card renders. Presentation DTO over <c>HistoryEntry</c>: it carries only the
/// fields the card needs and the marks already in the SPA's token→("tag"|"artist") shape, so neither the view model
/// nor the Razor views touch the domain entity (or <c>Mark</c>/<c>TokenKind</c>).
/// </summary>
/// <param name="GatewayImageId">The image id.</param>
/// <param name="Prompt">The prompt text (split into chips by the view model).</param>
/// <param name="ModelFriendly">The model's display name.</param>
/// <param name="ModelId">The configuration id (the "open this workflow" link).</param>
/// <param name="Aspect">The aspect label.</param>
/// <param name="CreatedAtUtc">When it was generated.</param>
/// <param name="Marks">Canonical token → "tag"|"artist" for the marked tokens (empty when none).</param>
/// <param name="GeneratedTokens">Canonical keys of the marks a random sampler APPENDED (auto-generated) — the subset
/// the card dashes. Empty when none, and absent (renders no dash) for rows written before provenance was recorded.</param>
public sealed record ImageDetailView(
    string GatewayImageId,
    string Prompt,
    string ModelFriendly,
    string ModelId,
    string Aspect,
    DateTime CreatedAtUtc,
    IReadOnlyDictionary<string, string> Marks,
    IReadOnlyList<LoraView>? Loras = null,
    IReadOnlySet<string>? GeneratedTokens = null);

/// <summary>One LoRA an image was generated with (name + weight), for the viewer's LoRA list and its Reload.</summary>
/// <param name="Name">The subfolder-qualified <c>lora_name</c>.</param>
/// <param name="Weight">The strength it was applied at.</param>
public sealed record LoraView(string Name, double Weight);

/// <summary>The JSON contract used to client-render an image detail card. It contains the same presentation facts
/// the standalone Razor card consumes; no HTML or domain entity crosses the endpoint.</summary>
public sealed record ImageDetailRecord(
    long Ts,
    string Id,
    string Prompt,
    string? MarkerPrompt,
    string? NegativePrompt,
    string? OriginalPrompt,
    string Model,
    string ModelId,
    string Aspect,
    IReadOnlyDictionary<string, string>? Marks,
    IReadOnlyList<LoraView>? Loras,
    IReadOnlyList<PromptChip> Chips,
    bool Bookmarked);
