using System.Text.Json;

namespace ImageGen.Application.Rendering;

/// <summary>
/// One image-generation request as the orchestrator renders and persists it. <see cref="Workflow"/> is the workflow
/// configuration id; <see cref="Overrides"/> are optional values for its UI-exposed parameters. The spec carries no ban
/// list: the user's banned tags/artists are read from the store at render time, so a job resumed after a restart honours
/// the bans as they stand THEN, not as they stood at submit.
/// <para>This is an in-memory shape only. It used to be serialized whole into an encrypted <c>RequestJson</c> column,
/// which made its property NAMES a durable contract — and a rename then deserialized silently into a null. The slot's
/// spec is typed columns now, so renaming a property here is an ordinary refactor.</para>
/// </summary>
/// <param name="TagTypes">
/// The generation mask for THIS render: which tag types the random-prompt model may emit. Per-slot like
/// <paramref name="Temperature"/>, because the composer sets it right under the slider it qualifies — so a queued
/// batch keeps the mask it was submitted under even if the user changes the chips before it comes up. NULL means the
/// caller specified none (an API-key client, or a slot queued before this field existed), and the orchestrator then
/// falls back to the owner's stored mask; an EMPTY list is a real choice meaning every switchable type is off.
/// </param>
/// <param name="OriginalPrompt">
/// The prompt as the user TYPED it, carried alongside <paramref name="Prompt"/> because the composer resolves its own
/// syntax before submitting — <c>[a|b]</c> collapsed to the option it rolled, <c>{a|b}</c> fanned into separate slots,
/// an artist page's locked artist appended — and none of that is recoverable from the result. Purely a record: nothing
/// renders from it. Null when the caller sent none (an API-key client, or a slot queued before this field existed).
/// </param>
public sealed record GenerateSpec(
    string Workflow,
    string Prompt,
    string? NegativePrompt,
    string? Aspect,
    bool? RandomArtist = null,
    bool? RandomPrompt = null,
    double? Temperature = null,
    Dictionary<string, JsonElement>? Overrides = null,
    List<string>? TagTypes = null,
    string? OriginalPrompt = null);

/// <summary>
/// One image-edit request as the orchestrator renders and persists it. <see cref="Workflow"/> is the edit workflow
/// configuration id; <see cref="ImageId"/> the source. In-memory only — the slot's spec is stored as typed columns,
/// so these names are not a durable contract (see <see cref="GenerateSpec"/>).
/// </summary>
public sealed record EditSpec(
    string Workflow,
    string Instruction,
    string ImageId,
    string? NegativePrompt = null,
    List<string>? ReferenceImageIds = null,
    Dictionary<string, JsonElement>? Overrides = null,
    string? MaskImageId = null,
    string? LastFrameImageId = null);

/// <summary>
/// One slot of an enqueue: exactly one of a generate spec or an edit spec. Use the factories, which enforce the XOR.
/// </summary>
public sealed class RenderItem
{
    private RenderItem(GenerateSpec? gen, EditSpec? edit) { Gen = gen; Edit = edit; }

    /// <summary>The generate spec, or null when this is an edit item.</summary>
    public GenerateSpec? Gen { get; }

    /// <summary>The edit spec, or null when this is a generate item.</summary>
    public EditSpec? Edit { get; }

    /// <summary>A generate item.</summary>
    public static RenderItem ForGenerate(GenerateSpec spec) => new(spec, null);

    /// <summary>An edit item.</summary>
    public static RenderItem ForEdit(EditSpec spec) => new(null, spec);
}
