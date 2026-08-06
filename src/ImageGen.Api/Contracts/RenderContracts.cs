using ImageGen.Application.Rendering;
using ImageGen.Domain;
using ImageGen.Domain.CodeAnalysis;
using System.Text.Json;

namespace ImageGen.Api.Contracts;

/// <summary>One item of a batch enqueue (Edit=true marks an edit item) — the SINGLE submission shape for both kinds,
/// since every page now POSTs its work as an /enqueue batch (there is no separate /generate or /edit endpoint).
/// <c>Workflow</c> is non-nullable and required for both item kinds; <c>ImageId</c> stays nullable because a generate
/// item legitimately omits it — its presence is the discriminated-union concern validated by the <c>Edit == true</c>
/// branch, not a missing-member check. Generate fields (prompt/aspect/random-*/tagTypes/loras) and edit fields
/// (instruction/imageId/mask/refs/lastFrame) coexist; <c>ToRenderItem</c> reads the set that matches <c>Edit</c>.</summary>
public sealed record EnqueueItem(
    string Workflow, bool Edit = false, string? Prompt = null, string? NegativePrompt = null, string? Aspect = null,
    string? Instruction = null, string? ImageId = null, List<string>? ReferenceImageIds = null,
    TriState RandomArtist = TriState.Unspecified,
    TriState RandomPrompt = TriState.Unspecified,
    [property: AllowNullable("null = the caller omitted it, so use the tag model's default sampling; 0.0 is a real (greedy) temperature")] double? Temperature = null,
    Dictionary<string, JsonElement>? Overrides = null,
    string? MaskImageId = null,
    string? LastFrameImageId = null,
    List<string>? TagTypes = null,
    string? OriginalPrompt = null,
    List<LoraSelection>? Loras = null,
    bool Background = false);

/// <summary>Batch enqueue payload: a mixed list of generate and edit items.</summary>
public sealed record EnqueueRequest(List<EnqueueItem>? Jobs = null);

/// <summary>Maps the render wire contracts to the Application render specs (hand-written; no AutoMapper).</summary>
public static class RenderContractMapping
{
    /// <summary>Map a batch item to a render item, or null when the item is invalid (skipped). An absent instruction is
    /// an empty one — some editors (upscale, matte) take none — coalesced here at the wire→domain boundary exactly as
    /// the generate path does its prompt, so <see cref="EditSpec.Instruction"/> is honestly non-null.</summary>
    public static RenderItem? ToRenderItem(this EnqueueItem it)
    {
        if (it.Edit)
        {
            if (string.IsNullOrWhiteSpace(it.Workflow) || string.IsNullOrWhiteSpace(it.ImageId))
            {
                return null;
            }

            return RenderItem.ForEdit(new EditSpec(it.Workflow, it.Instruction ?? "", it.ImageId,
                it.NegativePrompt, it.ReferenceImageIds, it.Overrides, it.MaskImageId, it.LastFrameImageId), it.Background);
        }

        if (string.IsNullOrWhiteSpace(it.Workflow))
        {
            return null;   // empty prompt allowed
        }

        return RenderItem.ForGenerate(new GenerateSpec(it.Workflow, it.Prompt ?? "", it.NegativePrompt, it.Aspect,
            it.RandomArtist, it.RandomPrompt, it.Temperature, it.Overrides, it.TagTypes, it.OriginalPrompt, it.Loras), it.Background);
    }
}
