using ImageGen.Domain;
using ImageGen.Domain.CodeAnalysis;
using System.Text.Json;

namespace ImageGen.Application.Rendering;

/// <summary>One user-selected LoRA for a generation: <see cref="Name"/> is the subfolder-qualified filename ComfyUI
/// reports (e.g. <c>anime/foo.safetensors</c>), passed verbatim as <c>lora_name</c>; <see cref="Weight"/> is the
/// strength applied to BOTH the diffusion model and CLIP. User LoRAs stack additively on top of a preset LoRA.</summary>
public sealed record LoraSelection(string Name, double Weight);

/// <summary>
/// One image-generation request as the orchestrator renders and persists it. <see cref="Workflow"/> is the workflow
/// configuration id; <see cref="Overrides"/> are optional values for its UI-exposed parameters. The spec carries no ban
/// list: the user's banned tags/artists are read from the store at render time, so a job resumed after a restart honours
/// the bans as they stand THEN, not as they stood at submit.
/// <para>This is an in-memory shape only: the slot's spec is stored as typed columns, so these property NAMES are not
/// a durable contract and renaming one here is an ordinary refactor. Serializing the spec whole into an encrypted
/// <c>RequestJson</c> column would make the names a durable contract, where a rename deserializes silently into a null.</para>
/// </summary>
/// <param name="TagTypes">
/// The generation mask for THIS render: which tag types the random-prompt model may emit. Per-slot like
/// <paramref name="Temperature"/>, because the composer sets it right under the slider it qualifies — so a queued
/// batch keeps the mask it was submitted under even if the user changes the chips before it comes up. NULL means the
/// caller specified none (an API-key client, or a slot queued before this field existed), and the orchestrator then
/// falls back to the owner's stored mask; an EMPTY list is a real choice meaning every switchable type is off.
/// </param>
/// <param name="OriginalPrompt">
/// The prompt as the user TYPED it, carried alongside <paramref name="Prompt"/> because that syntax is resolved before
/// this slot renders — the orchestrator fans a <c>{{a|b}}</c> into one slot per combo and picks each <c>{a|b}</c> at
/// enqueue (so <paramref name="Prompt"/> is the RESOLVED text), and an artist page's locked artist is appended — none
/// of it recoverable from the result. Purely a record: nothing renders from it. Null when the caller sent none.
/// </param>
/// <param name="Loras">
/// The user's LoRA stack for THIS render (empty/null for none): each a subfolder-qualified <c>lora_name</c> + weight,
/// chained through <c>LoraLoader</c> (model + CLIP) on top of any preset LoRA. Per-slot like <paramref name="Overrides"/>,
/// so a queued batch keeps the LoRAs it was submitted under. Recorded with the image so Reload can reproduce it.
/// </param>
/// <param name="ResolvePromptSyntax">False only when <paramref name="Prompt"/> is already concrete replay text.</param>
public sealed record GenerateSpec(
    string Workflow,
    string Prompt,
    string? NegativePrompt,
    string? Aspect,
    TriState RandomArtist = TriState.Unspecified,
    TriState RandomPrompt = TriState.Unspecified,
    [property: AllowNullable("null = the caller sent none, so use the tag model's default sampling; 0.0 is a real (greedy) temperature")] double? Temperature = null,
    Dictionary<string, JsonElement>? Overrides = null,
    List<string>? TagTypes = null,
    string? OriginalPrompt = null,
    IReadOnlyList<LoraSelection>? Loras = null,
    bool ResolvePromptSyntax = true);

/// <summary>
/// One image-edit request as the orchestrator renders and persists it. <see cref="Workflow"/> is the edit workflow
/// configuration id; <see cref="ImageId"/> the optional primary source. In-memory only — the slot's spec is stored as typed columns,
/// so these names are not a durable contract (see <see cref="GenerateSpec"/>).
/// </summary>
/// <param name="ResolvePromptSyntax">False only when <paramref name="Instruction"/> is already concrete replay text.</param>
public sealed record EditSpec(
    string Workflow,
    string Instruction,
    string? ImageId,
    string? NegativePrompt = null,
    List<string>? ReferenceIds = null,
    Dictionary<string, JsonElement>? Overrides = null,
    string? MaskImageId = null,
    string? LastFrameImageId = null,
    bool ResolvePromptSyntax = true,
    TriState RandomArtist = TriState.Unspecified);

/// <summary>
/// One slot of an enqueue: exactly one of a generate spec or an edit spec. Use the factories, which enforce the XOR.
/// </summary>
public sealed class RenderItem
{
    private RenderItem(GenerateSpec? gen, EditSpec? edit, bool background)
    {
        Gen = gen;
        Edit = edit;
        Background = background;
    }

    /// <summary>The generate spec, or null when this is an edit item.</summary>
    public GenerateSpec? Gen { get; }

    /// <summary>The edit spec, or null when this is a generate item.</summary>
    public EditSpec? Edit { get; }

    /// <summary>When true, this is a BACKGROUND (idle-time) render: it runs only once the queue has been idle of
    /// foreground work for the configured delay, and a foreground submission preempts it. A scheduling property of the
    /// slot, not part of the render spec — it changes WHEN the slot runs, never WHAT it renders.</summary>
    public bool Background { get; }

    /// <summary>A generate item. <paramref name="background"/> marks it as idle-time work (see <see cref="Background"/>).</summary>
    public static RenderItem ForGenerate(GenerateSpec spec, bool background = false) => new(spec, null, background);

    /// <summary>An edit item. <paramref name="background"/> marks it as idle-time work (see <see cref="Background"/>).</summary>
    public static RenderItem ForEdit(EditSpec spec, bool background = false) => new(null, spec, background);
}
