//TODO: CHECK FOR FALLBACKS
using System.Text.Json;
using ImageGen.Application.Rendering;

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
public sealed record GenerateRequest(
    string Workflow, string Prompt, string? NegativePrompt, string? Aspect,
    bool? RandomArtist = null, bool? RandomPrompt = null,
    double? Temperature = null,
    Dictionary<string, JsonElement>? Overrides = null,
    List<string>? TagTypes = null,
    string? OriginalPrompt = null,
    List<LoraSelection>? Loras = null);

/// <summary>One image-edit request body. <c>Workflow</c> is the edit workflow configuration id; <c>ImageId</c> the source.</summary>
public sealed record EditRequest(
    string Workflow, string Instruction, string ImageId, string? NegativePrompt = null,
    List<string>? ReferenceImageIds = null,
    Dictionary<string, JsonElement>? Overrides = null,
    string? MaskImageId = null,
    string? LastFrameImageId = null);

/// <summary>One item of a batch enqueue (Edit=true marks an edit item).</summary>
public sealed record EnqueueItem(
    bool? Edit, string? Workflow, string? Prompt, string? NegativePrompt, string? Aspect, string? Instruction,
    string? ImageId, List<string>? ReferenceImageIds = null, bool? RandomArtist = null, bool? RandomPrompt = null,
    double? Temperature = null,
    Dictionary<string, JsonElement>? Overrides = null,
    string? LastFrameImageId = null,
    List<string>? TagTypes = null,
    string? OriginalPrompt = null,
    List<LoraSelection>? Loras = null);

/// <summary>Batch enqueue payload: a mixed list of generate and edit items.</summary>
public sealed record EnqueueRequest(List<EnqueueItem>? Jobs);

/// <summary>Maps the render wire contracts to the Application render specs (hand-written; no AutoMapper).</summary>
public static class RenderContractMapping
{
    /// <summary>Map a generate request body to the orchestration spec (an empty prompt is valid).</summary>
    public static GenerateSpec ToSpec(this GenerateRequest r) => new(
        r.Workflow, r.Prompt ?? "", r.NegativePrompt, r.Aspect,
        r.RandomArtist, r.RandomPrompt, r.Temperature, r.Overrides, r.TagTypes, r.OriginalPrompt, r.Loras);

    /// <summary>Map an edit request body to the orchestration spec.</summary>
    public static EditSpec ToSpec(this EditRequest r) => new(
        r.Workflow, r.Instruction, r.ImageId, r.NegativePrompt, r.ReferenceImageIds, r.Overrides, r.MaskImageId, r.LastFrameImageId);

    /// <summary>Map a batch item to a render item, or null when the item is invalid (skipped).</summary>
    public static RenderItem? ToRenderItem(this EnqueueItem it)
    {
        if (it.Edit == true)
        {
            if (string.IsNullOrWhiteSpace(it.Workflow) || string.IsNullOrWhiteSpace(it.ImageId)) return null;
            return RenderItem.ForEdit(new EditSpec(it.Workflow!, it.Instruction ?? "", it.ImageId!,
                it.NegativePrompt, it.ReferenceImageIds, it.Overrides, LastFrameImageId: it.LastFrameImageId));
        }
        if (string.IsNullOrWhiteSpace(it.Workflow)) return null;   // empty prompt allowed
        return RenderItem.ForGenerate(new GenerateSpec(it.Workflow!, it.Prompt ?? "", it.NegativePrompt, it.Aspect,
            it.RandomArtist, it.RandomPrompt, it.Temperature, it.Overrides, it.TagTypes, it.OriginalPrompt, it.Loras));
    }
}
