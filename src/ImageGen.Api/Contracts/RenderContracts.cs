using ImageGen.Application.Rendering;
using ImageGen.Domain.CodeAnalysis;
using System.Text.Json;

namespace ImageGen.Api.Contracts;

/// <summary>One image-generation request body. <c>Workflow</c> is the workflow configuration id; <c>Overrides</c> are
/// optional values for its UI-exposed parameters. The caller does NOT declare its bans: the user's banned tags/artists
/// are a server-side fact the orchestrator reads at render time.</summary>
/// <param name="TagTypes">
/// Optional generation mask for this render — the tag types the random-prompt model may emit. Omit it (null) to
/// generate under the owner's stored mask; an empty list is a real choice ("none of them"), not an omission.
/// </param>
/// <param name="OriginalPrompt">
/// Optional: the prompt as the user typed it, before the CALLER resolved its own syntax into <c>Prompt</c>. Recorded
/// with the image and never rendered from. Omit it when the caller does no such resolution — nothing is inferred.
/// </param>
/// <param name="Background">
/// Optional: enqueue as a BACKGROUND (idle-time) job. It runs only once the queue has been idle of foreground work for
/// the configured delay, and a foreground submission preempts it. Omit/false for an ordinary foreground render.
/// </param>
public sealed record GenerateRequest(
    string Workflow, string? Prompt = null, string? NegativePrompt = null, string? Aspect = null,
    [property: AllowNullable("null = the caller omitted it, distinct from an explicit false; passed through to the orchestrator's tri-state spec")] bool? RandomArtist = null,
    [property: AllowNullable("null = the caller omitted it, distinct from an explicit false; passed through to the orchestrator's tri-state spec")] bool? RandomPrompt = null,
    [property: AllowNullable("null = the caller omitted it, so use the tag model's default sampling; 0.0 is a real (greedy) temperature")] double? Temperature = null,
    Dictionary<string, JsonElement>? Overrides = null,
    List<string>? TagTypes = null,
    string? OriginalPrompt = null,
    List<LoraSelection>? Loras = null,
    bool Background = false);

/// <summary>One image-edit request body. <c>Workflow</c> is the edit workflow configuration id; <c>ImageId</c> the source.
/// Both are non-nullable and non-optional, so the serializer rejects a payload that omits or nulls either. Required
/// members lead so the optional (defaulted) ones can follow; binding is by name, so declaration order is cosmetic.</summary>
public sealed record EditRequest(
    string Workflow, string ImageId, string? Instruction = null, string? NegativePrompt = null,
    List<string>? ReferenceImageIds = null,
    Dictionary<string, JsonElement>? Overrides = null,
    string? MaskImageId = null,
    string? LastFrameImageId = null,
    bool Background = false);

/// <summary>One item of a batch enqueue (Edit=true marks an edit item). <c>Workflow</c> is non-nullable and required for
/// both item kinds; <c>ImageId</c> stays nullable because a generate item legitimately omits it — its presence is the
/// discriminated-union concern validated by the <c>Edit == true</c> branch, not a missing-member check.</summary>
public sealed record EnqueueItem(
    string Workflow, bool Edit = false, string? Prompt = null, string? NegativePrompt = null, string? Aspect = null,
    string? Instruction = null, string? ImageId = null, List<string>? ReferenceImageIds = null,
    [property: AllowNullable("null = the caller omitted it, distinct from an explicit false; passed through to the orchestrator's tri-state spec")] bool? RandomArtist = null,
    [property: AllowNullable("null = the caller omitted it, distinct from an explicit false; passed through to the orchestrator's tri-state spec")] bool? RandomPrompt = null,
    [property: AllowNullable("null = the caller omitted it, so use the tag model's default sampling; 0.0 is a real (greedy) temperature")] double? Temperature = null,
    Dictionary<string, JsonElement>? Overrides = null,
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
    /// <summary>Map a generate request body to the orchestration spec (an empty prompt is valid).</summary>
    public static GenerateSpec ToSpec(this GenerateRequest r) => new(
        r.Workflow, r.Prompt ?? "", r.NegativePrompt, r.Aspect,
        r.RandomArtist, r.RandomPrompt, r.Temperature, r.Overrides, r.TagTypes, r.OriginalPrompt, r.Loras);

    /// <summary>Map an edit request body to the orchestration spec. An absent instruction is an empty one — some
    /// editors (upscale, matte) take none — coalesced here at the wire→domain boundary exactly as the generate path
    /// does its prompt, so <see cref="EditSpec.Instruction"/> is honestly non-null and nothing downstream re-checks it.
    /// (The batch and requeue paths already normalize it the same way.)</summary>
    public static EditSpec ToSpec(this EditRequest r) => new(
        r.Workflow, r.Instruction ?? "", r.ImageId, r.NegativePrompt, r.ReferenceImageIds, r.Overrides, r.MaskImageId, r.LastFrameImageId);

    /// <summary>Map a batch item to a render item, or null when the item is invalid (skipped).</summary>
    public static RenderItem? ToRenderItem(this EnqueueItem it)
    {
        if (it.Edit)
        {
            if (string.IsNullOrWhiteSpace(it.Workflow) || string.IsNullOrWhiteSpace(it.ImageId)) return null;
            return RenderItem.ForEdit(new EditSpec(it.Workflow, it.Instruction ?? "", it.ImageId,
                it.NegativePrompt, it.ReferenceImageIds, it.Overrides, LastFrameImageId: it.LastFrameImageId), it.Background);
        }
        if (string.IsNullOrWhiteSpace(it.Workflow)) return null;   // empty prompt allowed
        return RenderItem.ForGenerate(new GenerateSpec(it.Workflow, it.Prompt ?? "", it.NegativePrompt, it.Aspect,
            it.RandomArtist, it.RandomPrompt, it.Temperature, it.Overrides, it.TagTypes, it.OriginalPrompt, it.Loras), it.Background);
    }
}
