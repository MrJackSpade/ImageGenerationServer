using ImageGen.Domain;
using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Application.Workflows;

/// <summary>Per-configuration booru tagging capability: whether the model speaks '#' tags and/or '@' artists, and how
/// their markers/underscores are rendered. Drives prompt finalization and the per-job random-artist append.</summary>
/// <param name="Tags">The model understands standard '#' tags.</param>
/// <param name="Artists">The model understands '@' artist tokens.</param>
/// <param name="KeepArtistMarker">Keep the leading '@' on a rendered artist token (rather than stripping it).</param>
/// <param name="UnderscoresToSpaces">Render underscores as spaces (score_ tags excepted).</param>
public sealed record WorkflowTagging(bool Tags, bool Artists, bool KeepArtistMarker, bool UnderscoresToSpaces);

/// <summary>
/// The compact, orchestrator-facing view of a workflow configuration: what the render pipeline needs to finalize a
/// prompt, tag it, name it in history, and decide the no-change gate — with no ComfyUI graph detail leaking through.
/// </summary>
/// <param name="FriendlyName">Display name for the configuration (falls back to its id).</param>
/// <param name="Tagging">The booru tagging rules, or null when the model doesn't speak tags.</param>
/// <param name="PreservesComposition">True when the workflow intentionally preserves composition (inpaint / pixel
/// transforms) so the whole-image no-change gate must be skipped for it.</param>
/// <param name="ProducesVideo">The workflow's DECLARED output: true for a video workflow. Not a guess from the
/// output's file extension — a single-frame render comes back as .webp exactly like a clip does.</param>
/// <param name="HasAudio">Video only: the clip carries a native audio track (MiniMax-H3). Rides the job/slot wire
/// views so a client rendering a result it did not submit still knows to offer an unmute control.</param>
public sealed record WorkflowInfo(
    string FriendlyName, WorkflowTagging? Tagging, bool PreservesComposition, bool ProducesVideo = false,
    WorkflowReference? Reference = null, bool HasAudio = false, string Kind = "");

/// <summary>The wire tokens for a workflow's resolved kind — the one vocabulary shared by the descriptor's
/// <see cref="WorkflowDescriptor.Kind"/> badge/routing, <see cref="WorkflowInfo.Kind"/>, and the render orchestrator's
/// mask-routing preflight. Const-extracted so no surface spells a kind as a magic string.</summary>
public static class WorkflowKindTokens
{
    public const string Generate = "generate";
    public const string Edit = "edit";
    public const string Inpaint = "inpaint";
    public const string Outpaint = "outpaint";
    public const string Redraw = "redraw";
    public const string Upscale = "upscale";
    public const string Effect = "effect";
    public const string Animate = "animate";
    public const string VideoEdit = "videoedit";
}

/// <summary>How one workflow parameter may be surfaced, per its configuration file. Three explicit states — there is
/// no fourth "absent" meaning, and no flag pair welding visibility to lockability (issue #191).</summary>
public enum ParamVisibility
{
    /// <summary>Shown in the composer by default. A user may hide it per-account; any caller may override it at submit.</summary>
    Exposed,

    /// <summary>Hidden from the composer by default, but a user may reveal it per-account. Overridable at submit by any
    /// caller regardless of who has revealed it — visibility is a UI concern, lockability is the submit gate.</summary>
    Hidden,

    /// <summary>Never surfaced and never overridable at submit: a structural constant (loader switches, model-slot
    /// refs, memory/device knobs). The one state that gates the submit path.</summary>
    Locked,
}

/// <summary>The wire spellings of <see cref="ParamVisibility"/> — the catalog JSON's <c>visibility</c> envelope values
/// and the tokens the settings/descriptor DTOs carry to the client.</summary>
public static class ParamVisibilityTokens
{
    public const string Exposed = "exposed";
    public const string Hidden = "hidden";
    public const string Locked = "locked";
}

/// <summary>Enum ↔ wire-token bridging for <see cref="ParamVisibility"/>.</summary>
public static class ParamVisibilityExtensions
{
    /// <summary>The wire token for <paramref name="visibility"/>.</summary>
    public static string Token(this ParamVisibility visibility) => visibility switch
    {
        ParamVisibility.Exposed => ParamVisibilityTokens.Exposed,
        ParamVisibility.Hidden => ParamVisibilityTokens.Hidden,
        ParamVisibility.Locked => ParamVisibilityTokens.Locked,
        _ => throw new ArgumentOutOfRangeException(nameof(visibility), visibility, null),
    };
}

/// <summary>One UI-exposed parameter of a workflow configuration, joined to its schema for type/range/label.</summary>
/// <param name="Key">Parameter key.</param>
/// <param name="Type">CLR type token, lowercased (int/double/string/bool/enum).</param>
/// <param name="Value">The configuration's current value.</param>
/// <param name="Min">Minimum for a numeric control, or null.</param>
/// <param name="Max">Maximum for a numeric control, or null.</param>
/// <param name="Step">UI increment for a numeric control, or null.</param>
/// <param name="Label">UI label.</param>
/// <param name="Help">Optional help text.</param>
/// <param name="Choices">Enum choices, or null.</param>
public sealed record WorkflowExposedParam(
    string Key, string Type, object? Value,
    [property: AllowNullable("null = the numeric control has no minimum bound; 0 is a real minimum, distinct from unbounded")] double? Min,
    [property: AllowNullable("null = the numeric control has no maximum bound; 0 is a real maximum, distinct from unbounded")] double? Max,
    [property: AllowNullable("null = the control declares no increment (free-entry); distinct from a 0 step")] double? Step,
    [AllowMagicStrings("human-readable UI parameter label")] string Label,
    [AllowMagicStrings("human-readable UI parameter help text")] string? Help,
    string[]? Choices);

/// <summary>How many references of ONE media kind a workflow accepts.</summary>
/// <param name="Kind">The media kind's wire token (see <see cref="ReferenceKinds.Wire"/>): image / audio / video.</param>
/// <param name="Max">Maximum references of this kind the workflow accepts (&gt; 0 to be offered at all).</param>
public sealed record ReferenceAllowance(string Kind, int Max);

/// <summary>The reference capability of an editor: which media KINDS it accepts and how many of each (most editors take
/// only images; a multi-modal one — e.g. MiniMax-H3 reference→video — takes image + audio + video), plus a phrasing
/// hint. The <c>＋ ref</c> button, the upload <c>accept</c> filter, and the enqueue validation all read this so no
/// workflow can be handed a reference kind it doesn't accept.</summary>
/// <param name="Types">The accepted per-kind allowances (only kinds with a positive max).</param>
/// <param name="Hint">How to phrase the instruction for references, or null.</param>
public sealed record WorkflowReference(IReadOnlyList<ReferenceAllowance> Types, string? Hint)
{
    /// <summary>The max references of <paramref name="kind"/> this workflow accepts (0 = not accepted).</summary>
    public int MaxOf(ReferenceKind kind)
    {
        string token = ReferenceKinds.Wire(kind);
        foreach (ReferenceAllowance t in Types)
        {
            if (string.Equals(t.Kind, token, StringComparison.Ordinal))
            {
                return t.Max;
            }
        }

        return 0;
    }

    /// <summary>Whether this workflow accepts any reference of <paramref name="kind"/>.</summary>
    public bool Accepts(ReferenceKind kind) => MaxOf(kind) > 0;

    /// <summary>The image-reference max — the back-compat scalar for surfaces (MCP model info) that predate multi-kind
    /// references and only know about reference IMAGES.</summary>
    public int MaxImages => MaxOf(ReferenceKind.Image);
}

/// <summary>A UI help link (text + url).</summary>
/// <param name="Text">Link text.</param>
/// <param name="Url">Link url.</param>
public sealed record WorkflowLink(string? Text, string? Url);

/// <summary>The decision-card summary the SPA renders on a workflow row (a projection of the model's prompting card).</summary>
public sealed record WorkflowCardSummary(
    string? FriendlyName,
    string? Architecture,
    string? Summary,
    string[]? UseCases,
    string? PromptFormat,
    string? RequiredPrefix,
    string? PromptGuidance,
    string? Example,
    string? UiGoodFor,
    string? UiNote,
    WorkflowLink? UiLink,
    string? NsfwCapable,
    string? CommercialUse,
    string? Speed,
    [property: AllowNullable("null = no measured ETA for the card; 0.0 would be a real (instant) estimate")] double? ExpectedGenSeconds,
    [property: AllowNullable("null = the card doesn't state negative-prompt support (unknown); distinct from an explicit false")] bool? NegativeSupported,
    string[]? EditUseCases,
    WorkflowTagging? Tagging,
    // The workflow's BASE categorization tags from its definition, or null when it declares none. The client merges
    // these with the user's per-workflow added/removed delta to show the effective tag set; a base tag added to the
    // definition later shows up for everyone who has not explicitly removed that value.
    string[]? Tags = null);

/// <summary>
/// One eligible workflow configuration as offered to the SPA/MCP <c>/workflows</c> list: its identity + capability
/// flags, UI-exposed parameters, per-machine average runtime, and decision card. Produced by the catalog after
/// VRAM/requirement-presence eligibility filtering and shared-friendly-name de-duplication.
/// </summary>
public sealed record WorkflowDescriptor(
    string Id,
    string Workflow,
    string Kind,
    string Media,
    string SourceMedia,
    string? EffectType,
    string? EditGroup,
    bool PromptDirectsMotion,
    string PromptSemantics,
    bool TakesPrompt,
    bool SupportsLastFrame,
    string? FriendlyName,
    bool Default,
    [property: AllowNullable("null = no timing samples yet on this machine; 0 would be a real (instant) average")] int? AvgSeconds,
    IReadOnlyList<WorkflowExposedParam> ExposedParams,
    IReadOnlyList<WorkflowExposedParam> HiddenParams,
    bool CanEdit,
    WorkflowReference? Reference,
    WorkflowCardSummary Card,
    string? LoraFolder = null,
    bool HasAudio = false,
    bool CustomSizeEnabled = false,
    bool IsVariant = false,
    // The configuration's aspect→[w,h] dims map (this machine's override applied), or null for a config with none.
    // The composer writes a clicked shape's dims into its (possibly hidden) width/height controls from THIS map and
    // submits the dims, not an aspect name (#209); the server derives the ratio from the submitted width/height.
    IReadOnlyDictionary<string, int[]>? Aspects = null,
    // This Edit config's masked sibling (the Inpaint config submit routes to when a mask is drawn), or "" when none.
    // The client swaps the param/refs/negative panel to the sibling's descriptor and sends the sibling id at enqueue.
    string MaskWorkflow = "",
    // True for a config that is the TARGET of another's MaskWorkflow link: suppressed from the picker UI but kept in
    // this payload (the client needs its ExposedParams/Reference/Card for the panel swap) and still enqueue-resolvable.
    bool HiddenFromPicker = false);

/// <summary>The per-model prompting guide surfaced by <c>/prompting</c> — how to write a prompt for a chosen model.</summary>
public sealed record PromptingGuide(
    string Name,
    string? FriendlyName,
    string? Architecture,
    bool CanEdit,
    string? Format,
    string? Overview,
    string? Guidance,
    string? Instructions,
    string? RequiredPrefix,
    [property: AllowNullable("null = the guide doesn't state negative-prompt support (unknown); distinct from an explicit false")] bool? NegativeSupported,
    string? NegativeGuidance,
    string[]? Do,
    string[]? Dont,
    string[]? Examples,
    string? Source,
    int MaxReferenceImages,
    string? ReferenceTechnique);

/// <summary>One model slot and what this machine has done about it.</summary>
/// <param name="Id">Slot id.</param>
/// <param name="Label">Human name, for the binding UI.</param>
/// <param name="Kind">Which loader's list it draws from — a slot may only be bound to a file of its own kind.</param>
/// <param name="BoundFile">The file bound to it, or null when nothing is.</param>
/// <param name="IsAuto">True when a match pattern chose <see cref="BoundFile"/> rather than a person.</param>
/// <param name="Candidates">Files of the right kind that its patterns recognised — what the UI offers first.</param>
/// <param name="Available">Every file of the right kind, so any of them can be picked.</param>
/// <param name="Kind">The loader family — which file list this slot can be bound from.</param>
/// <param name="Category">
/// The catalogue's own word for what it is ("lora", "clip_vision", …). What the UI groups by, because Kind folds
/// most of them into Other and "Other (22)" is not a category.
/// </param>
public sealed record ModelSlotStatus(
    string Id, string Label, string Kind, string? BoundFile, bool IsAuto,
    IReadOnlyList<string> Candidates, IReadOnlyList<string> Available);

/// <summary>One model slot a single workflow uses, for the picker on that workflow's detail page: the slot's binding
/// status (as <see cref="ModelSlotStatus"/>) plus the OTHER workflows that share it. Model bindings are global per
/// <c>(machine, slot)</c>, so changing one from a workflow page changes it for every workflow referencing that slot —
/// <see cref="SharedWith"/> names those so the change isn't silent.</summary>
/// <param name="SharedWith">Display names of the OTHER workflows that also require this slot (this workflow excluded).
/// Empty when the slot is used by this workflow alone — then no cross-workflow warning is shown.</param>
public sealed record ConfigSlotStatus(
    string Id, string Label, string Kind, string? BoundFile, bool IsAuto,
    IReadOnlyList<string> Candidates, IReadOnlyList<string> Available,
    IReadOnlyList<string> SharedWith);

/// <summary>One LoRA file present on this machine, for the composer's LoRA picker.</summary>
/// <param name="Name">The subfolder-qualified <c>lora_name</c> exactly as ComfyUI reports it (e.g. <c>anime/foo.safetensors</c>).</param>
/// <param name="Compatible">Whether it will actually apply to the given workflow's base model (its keys resolve). True
/// when no workflow was named (compatibility not evaluated), so the picker shows everything.</param>
/// <param name="ClipCapable">Whether it carries text-encoder keys — false means model-only, so its CLIP strength no-ops.</param>
public sealed record LoraCatalogEntry(string Name, bool Compatible, bool ClipCapable);

/// <summary>Why one workflow is or is not offered on this machine.</summary>
/// <param name="Id">Configuration id.</param>
/// <param name="FriendlyName">Display name.</param>
/// <param name="Ready">True when it can run right now.</param>
/// <param name="MissingSlots">Slots with nothing usable bound — the reason it is unavailable, named.</param>
/// <param name="RequiredSlots">
/// Every slot this workflow needs, satisfied or not — so the fix-it dialog can offer them all rather than only the
/// empty ones, and a wrong binding can be corrected without hunting for it.
/// </param>
/// <param name="Kind">The workflow's gen/edit kind — from the registered CLASS, so it is known even when the workflow
/// is unavailable (a slot file isn't bound). The workflows page badges every row off this, disabled ones included.</param>
public sealed record WorkflowStatus(
    string Id, string FriendlyName, bool Ready, IReadOnlyList<string> MissingSlots,
    IReadOnlyList<string> RequiredSlots, string Kind);

/// <summary>
/// One setting on one workflow, as its settings page sees it: what the catalogue ships, what this machine has
/// changed it to, and which of the two is in force.
/// </summary>
/// <param name="Shipped">The value in the catalogue file. What "reset" restores.</param>
/// <param name="Override">This machine's value, or null when it has not been changed here.</param>
/// <param name="Visibility">The param's SHIPPED <see cref="ParamVisibilityTokens"/> token. Everything but
/// <see cref="ParamVisibilityTokens.Locked"/> gets a per-account show/hide checkbox in the editor. Null = not a config
/// param at all (a synthetic per-machine setting like the custom-size toggle), which likewise gets no checkbox.</param>
public sealed record ConfigSetting(
    string Key,
    [AllowMagicStrings("human-readable UI setting label")] string Label,
    [AllowMagicStrings("human-readable UI setting help text")] string? Help,
    string Type,
    [property: AllowNullable("null = the setting has no minimum bound; 0 is a real minimum, distinct from unbounded")] double? Min,
    [property: AllowNullable("null = the setting has no maximum bound; 0 is a real maximum, distinct from unbounded")] double? Max,
    [property: AllowNullable("null = the setting declares no increment (free-entry); distinct from a 0 step")] double? Step,
    IReadOnlyList<string>? Choices,
    object? Shipped, object? Override,
    [property: AllowNullable("null = not a configuration param (a synthetic per-machine setting) — no visibility checkbox")] string? Visibility = null);

/// <summary>
/// The model's documented output-resolution envelope: the smallest and largest side it supports, and the latent
/// step its dimensions must be a multiple of. The size editor is bounded by THESE numbers rather than by anything
/// the UI made up — a model that wants a minimum of 480 and multiples of 16 is not served by a box that accepts
/// 64 in steps of 8.
/// </summary>
public sealed record ResolutionEnvelope(int MinW, int MinH, int MaxW, int MaxH, int Step);

/// <summary>Everything editable about one workflow on this machine.</summary>
public sealed record WorkflowSettings(
    string Id, string FriendlyName, IReadOnlyList<ConfigSetting> Settings, ResolutionEnvelope? Resolution);

/// <summary>The whole picture: what this machine can run, and what it needs pointing at.</summary>
/// <param name="Workflows">Every workflow, ready or not.</param>
/// <param name="Slots">Every model slot and its binding.</param>
/// <param name="TotalVramMb">What the GPU reports, or null when ComfyUI did not say.</param>
public sealed record CatalogStatus(
    IReadOnlyList<WorkflowStatus> Workflows, IReadOnlyList<ModelSlotStatus> Slots);
