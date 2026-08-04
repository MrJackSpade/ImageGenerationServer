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
/// output's file extension — a single-frame render comes back as .webp exactly like a clip does, which is how seven
/// LTX configurations reported success having produced stills.</param>
public sealed record WorkflowInfo(
    string FriendlyName, WorkflowTagging? Tagging, bool PreservesComposition, bool ProducesVideo = false);

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
    string Key, string Type, object? Value, double? Min, double? Max, double? Step, string Label, string? Help, string[]? Choices);

/// <summary>The reference-image capability of an editor: how many extra images it takes and how to phrase them.</summary>
/// <param name="Max">Maximum reference images accepted.</param>
/// <param name="Hint">How to phrase the instruction for references, or null.</param>
public sealed record WorkflowReference(int Max, string? Hint);

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
    double? ExpectedGenSeconds,
    bool? NegativeSupported,
    string[]? EditUseCases,
    WorkflowTagging? Tagging);

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
    int? AvgSeconds,
    IReadOnlyList<WorkflowExposedParam> ExposedParams,
    bool CanEdit,
    WorkflowReference? Reference,
    WorkflowCardSummary Card,
    string? LoraFolder = null,
    bool HasAudio = false);

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
    bool? NegativeSupported,
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
public sealed record WorkflowStatus(
    string Id, string FriendlyName, bool Ready, IReadOnlyList<string> MissingSlots,
    IReadOnlyList<string> RequiredSlots);

/// <summary>
/// One setting on one workflow, as its settings page sees it: what the catalogue ships, what this machine has
/// changed it to, and which of the two is in force.
/// </summary>
/// <param name="Shipped">The value in the catalogue file. What "reset" restores.</param>
/// <param name="Override">This machine's value, or null when it has not been changed here.</param>
public sealed record ConfigSetting(
    string Key, string Label, string? Help, string Type,
    double? Min, double? Max, double? Step, IReadOnlyList<string>? Choices,
    object? Shipped, object? Override);

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
