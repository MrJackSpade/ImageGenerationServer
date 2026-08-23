using ImageGen.Application.Workflows;
using ImageGen.Domain.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>
/// The on-disk shape of the catalog files, deserialized directly by <see cref="System.Text.Json"/> instead of being
/// pulled apart property-by-property. Every object here is <see cref="JsonUnmappedMemberHandling.Disallow"/>: a key
/// in a catalog file that maps to no member is an error at parse time, not data that is silently dropped. That is the
/// point of modelling the shape at all — a misspelled or unread key would otherwise survive unnoticed (a whole
/// <c>resolution</c> block going unread, or a mistyped <c>kind</c> falling into a shared bucket), and the
/// only way that becomes visible is to name every key the file is allowed to carry.
///
/// <para>These are the wire types. <see cref="WorkflowCatalog"/> maps them onto the domain records
/// (<see cref="WorkflowConfiguration"/>, <see cref="Requirement"/>, <see cref="ModelCard"/>) so the rest of the app
/// never sees a <see cref="JsonElement"/> or a snake_case key.</para>
/// </summary>
[JsonSourceGenerationOptions]
[JsonSerializable(typeof(ModelFileDto))]
[JsonSerializable(typeof(WorkflowFileDto))]
[JsonSerializable(typeof(ConfigParamRangeOverrideDto))]
internal sealed partial class CatalogJsonContext : JsonSerializerContext;

/// <summary>A <c>models/&lt;id&gt;.json</c> file: one bindable slot's identity.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ModelFileDto
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("kind")] public string? Kind { get; init; }
    [JsonPropertyName("label")] public string? Label { get; init; }
    [JsonPropertyName("match")] public string[]? Match { get; init; }
    [JsonPropertyName("node")] public string? Node { get; init; }
    [JsonPropertyName("page")] public string? Page { get; init; }
}

/// <summary>A <c>workflows/&lt;id&gt;.json</c> file: a workflow class bound to its settings layer, requirement links,
/// and decision card.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record WorkflowFileDto
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("workflow")] public string? Workflow { get; init; }
    [JsonPropertyName("friendly_name")] public string? FriendlyName { get; init; }
    [JsonPropertyName("short_name")] public string? ShortName { get; init; }
    [JsonPropertyName("requirements")] public RequirementLinksDto? Requirements { get; init; }
    [JsonPropertyName("params")] public Dictionary<string, ConfigParamDto>? Params { get; init; }
    [JsonPropertyName("effect_type")] public string? EffectType { get; init; }
    [JsonPropertyName("edit_group")] public string? EditGroup { get; init; }
    [JsonPropertyName("default")][AllowNullable("null = the \"default\" key was absent in the config JSON; distinct from an explicit false")] public bool? Default { get; init; }
    [JsonPropertyName("mask_workflow")] public string? MaskWorkflow { get; init; }
    [JsonPropertyName("reference_workflow")] public string? ReferenceWorkflow { get; init; }
    [JsonPropertyName("resolution")] public ResolutionDto? Resolution { get; init; }
    [JsonPropertyName("card")] public CardDto? Card { get; init; }
}

/// <summary>The <c>requirements</c> block: a configuration's soft links to its model slots, by id.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RequirementLinksDto
{
    [JsonPropertyName("checkpoint")] public string? Checkpoint { get; init; }
    [JsonPropertyName("text_encoders")] public string[]? TextEncoders { get; init; }
    [JsonPropertyName("vae")] public string? Vae { get; init; }
    [JsonPropertyName("motion_model")] public string? MotionModel { get; init; }
    [JsonPropertyName("controlnet")] public string? ControlNet { get; init; }
    [JsonPropertyName("extra")] public string[]? Extra { get; init; }
}

/// <summary>The <c>resolution</c> block: a model's documented output-resolution envelope. The block is optional, but
/// a block that is present must be complete — the fields are nullable here so a missing one is caught (and named) by
/// the mapper rather than silently defaulted.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ResolutionDto
{
    [JsonPropertyName("min_w")][AllowNullable("null = the key was absent; kept nullable so the mapper catches and names a missing field rather than silently defaulting it")] public int? MinW { get; init; }
    [JsonPropertyName("min_h")][AllowNullable("null = the key was absent; kept nullable so the mapper catches and names a missing field rather than silently defaulting it")] public int? MinH { get; init; }
    [JsonPropertyName("max_w")][AllowNullable("null = the key was absent; kept nullable so the mapper catches and names a missing field rather than silently defaulting it")] public int? MaxW { get; init; }
    [JsonPropertyName("max_h")][AllowNullable("null = the key was absent; kept nullable so the mapper catches and names a missing field rather than silently defaulting it")] public int? MaxH { get; init; }
    [JsonPropertyName("step")][AllowNullable("null = the key was absent; kept nullable so the mapper catches and names a missing field rather than silently defaulting it")] public int? Step { get; init; }
}

/// <summary>The <c>card</c> block: the LLM/UI-facing decision + prompting metadata for a configuration.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CardDto
{
    [JsonPropertyName("friendly_name")] public string? FriendlyName { get; init; }
    [JsonPropertyName("architecture")] public string? Architecture { get; init; }
    [JsonPropertyName("summary")] public string? Summary { get; init; }
    [JsonPropertyName("notes")] public string? Notes { get; init; }
    [JsonPropertyName("use_cases")] public string[]? UseCases { get; init; }
    [JsonPropertyName("tags")] public string[]? Tags { get; init; }
    [JsonPropertyName("prompt")] public PromptDto? Prompt { get; init; }
    [JsonPropertyName("speed")] public SpeedDto? Speed { get; init; }
    [JsonPropertyName("negative")] public NegativeDto? Negative { get; init; }
    [JsonPropertyName("ui_help")] public UiHelpDto? UiHelp { get; init; }
    [JsonPropertyName("reference")] public ReferenceDto? Reference { get; init; }
    [JsonPropertyName("nsfw_capable")] public string? NsfwCapable { get; init; }
    [JsonPropertyName("commercial_use")] public string? CommercialUse { get; init; }
    [JsonPropertyName("pick_when")] public string? PickWhen { get; init; }
    [JsonPropertyName("edit_use_cases")] public string[]? EditUseCases { get; init; }
    [JsonPropertyName("tagging")] public TaggingDto? Tagging { get; init; }
}

/// <summary>The card's <c>prompt</c> block: how to write a prompt for this model.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PromptDto
{
    [JsonPropertyName("format")] public string? Format { get; init; }
    [JsonPropertyName("required_prefix")] public string? RequiredPrefix { get; init; }
    [JsonPropertyName("optional_tags")] public string[]? OptionalTags { get; init; }
    [JsonPropertyName("guidance")] public string? Guidance { get; init; }
    [JsonPropertyName("overview")] public string? Overview { get; init; }
    [JsonPropertyName("instructions")] public string? Instructions { get; init; }
    [JsonPropertyName("example")] public string? Example { get; init; }
    [JsonPropertyName("do")] public string[]? Do { get; init; }
    [JsonPropertyName("dont")] public string[]? Dont { get; init; }
    [JsonPropertyName("examples")] public string[]? Examples { get; init; }
    [JsonPropertyName("source")] public string? Source { get; init; }
    [JsonPropertyName("negative_guidance")] public string? NegativeGuidance { get; init; }
}

/// <summary>The card's <c>speed</c> block: a qualitative class, an optional short note, and any measured timing.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SpeedDto
{
    [JsonPropertyName("class")] public string? Class { get; init; }
    [JsonPropertyName("note")] public string? Note { get; init; }
    [JsonPropertyName("measured_seconds")][AllowNullable("null = no measured timing declared in the card; 0.0 would be a real (instant) measurement")] public double? MeasuredSeconds { get; init; }
    [JsonPropertyName("measured_note")] public string? MeasuredNote { get; init; }
}

/// <summary>The card's <c>negative</c> block: whether the model uses a negative prompt, and a note if so.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record NegativeDto
{
    [JsonPropertyName("supported")][AllowNullable("null = the \"supported\" key was absent (unknown); distinct from an explicit false")] public bool? Supported { get; init; }
    [JsonPropertyName("note")] public string? Note { get; init; }
}

/// <summary>The card's <c>ui_help</c> block: the short in-UI hints for the model picker.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record UiHelpDto
{
    [JsonPropertyName("good_for")] public string? GoodFor { get; init; }
    [JsonPropertyName("note")] public string? Note { get; init; }
    [JsonPropertyName("link")] public UiLinkDto? Link { get; init; }
}

/// <summary>A <c>ui_help.link</c>: a labelled external link shown with the model.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record UiLinkDto
{
    [JsonPropertyName("text")] public string? Text { get; init; }
    [JsonPropertyName("url")] public string? Url { get; init; }
}

/// <summary>The card's <c>reference</c> block: which reference media KINDS an editor takes (and how many of each), plus
/// a hint. Two spellings: the back-compat scalar <c>max</c> declares reference IMAGES only (every existing card); the
/// explicit <c>types</c> array declares per-kind maxes for a multi-modal editor (image / audio / video). Exactly one
/// shape is required; catalog loading rejects both-or-neither.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReferenceDto
{
    [JsonPropertyName("max")][AllowNullable("null = the \"max\" key was absent; distinct from a 0 reference count")] public int? Max { get; init; }
    [JsonPropertyName("hint")] public string? Hint { get; init; }
    [JsonPropertyName("types")] public ReferenceTypeDto[]? Types { get; init; }
}

/// <summary>One entry of a <c>reference.types</c> array: a media kind (<c>image</c>/<c>audio</c>/<c>video</c>) and how
/// many references of it the editor accepts.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReferenceTypeDto
{
    [JsonPropertyName("kind")] public string? Kind { get; init; }
    [JsonPropertyName("max")][AllowNullable("null = the \"max\" key was absent; distinct from a 0 reference count")] public int? Max { get; init; }
}

/// <summary>The card's <c>tagging</c> block: the model's booru-tagging capability.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record TaggingDto
{
    [JsonPropertyName("tags")][AllowNullable("null = the key was absent; kept nullable so the mapper catches and names a missing field rather than silently defaulting it")] public bool? Tags { get; init; }
    [JsonPropertyName("artists")][AllowNullable("null = the key was absent; kept nullable so the mapper catches and names a missing field rather than silently defaulting it")] public bool? Artists { get; init; }
    [JsonPropertyName("keep_artist_marker")][AllowNullable("null = the key was absent; kept nullable so the mapper catches and names a missing field rather than silently defaulting it")] public bool? KeepArtistMarker { get; init; }
    [JsonPropertyName("underscores_to_spaces")][AllowNullable("null = the key was absent; kept nullable so the mapper catches and names a missing field rather than silently defaulting it")] public bool? UnderscoresToSpaces { get; init; }
}

/// <summary>
/// One entry of a configuration's <c>params</c> map, in either the bare-scalar shorthand (<c>"steps": 25</c>) or the
/// wrapped envelope form (<c>{ "value": 25, "visibility": "exposed", "min": 1, "max": 50, "step": 1 }</c>). Handled by
/// <see cref="ConfigParamDtoConverter"/> because the two forms cannot be expressed as one static shape.
/// </summary>
[JsonConverter(typeof(ConfigParamDtoConverter))]
internal sealed record ConfigParamDto
{
    /// <summary>The parameter value, decoupled from the parsed document. A scalar, or an object/array (the aspect
    /// dims map, a reference-inputs list) captured whole.</summary>
    public required JsonElement Value { get; init; }

    /// <summary>The param's explicit surfacing state. An envelope declares it in full (<c>"visibility"</c> is
    /// mandatory there); the bare-scalar shorthand IS the third state — a structural constant, always
    /// <see cref="ParamVisibility.Locked"/>.</summary>
    public required ParamVisibility Visibility { get; init; }

    [AllowNullable("null = the envelope declared no minimum bound; 0 is a real minimum, distinct from unbounded")] public double? Min { get; init; }
    [AllowNullable("null = the envelope declared no maximum bound; 0 is a real maximum, distinct from unbounded")] public double? Max { get; init; }
    [AllowNullable("null = the envelope declared no increment (free-entry); distinct from a 0 step")] public double? Step { get; init; }
    public ConfigParamRangeOverrideDto? RangeOverride { get; init; }
}

/// <summary>The strict wire shape of a field's explicitly enabled alternate numeric range.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ConfigParamRangeOverrideDto
{
    [JsonPropertyName("min")][AllowNullable("null = the alternate range keeps the field's normal minimum")] public double? Min { get; init; }
    [JsonPropertyName("max")][AllowNullable("null = the alternate range keeps the field's normal maximum")] public double? Max { get; init; }
    [JsonPropertyName("label")] public string? Label { get; init; }
    [JsonPropertyName("warning")] public string? Warning { get; init; }
}

/// <summary>
/// Reads a <c>params</c> entry in either form. The envelope form is signalled by an object carrying an explicit
/// <c>value</c> member; anything else — a bare scalar, a bare array, or an object without <c>value</c> (the aspect
/// map) — IS the value, and the bare form always means a locked structural constant. The envelope's own keys are
/// validated the same way the rest of the catalog is: a key other than
/// <c>value</c>/<c>visibility</c>/<c>min</c>/<c>max</c>/<c>step</c> is an error, not silently ignored — and
/// <c>visibility</c> itself is mandatory with exactly three spellings; a missing or unrecognized one throws rather
/// than coercing to a default.
/// </summary>
internal sealed class ConfigParamDtoConverter : JsonConverter<ConfigParamDto>
{
    /// <summary>A bare <c>null</c> param value is a real, meaningful value (e.g. <c>clip_type: null</c> = "let the
    /// loader decide"); without this, System.Text.Json short-circuits null to a null dictionary entry and it is lost.</summary>
    public override bool HandleNull => true;

    /// <summary>The wrapped-parameter envelope's member names — the only keys a params entry may carry beyond being a
    /// bare value. Named once so the presence check, the unknown-key guard, and the range reads share one spelling.</summary>
    private static class EnvelopeMember
    {
        public const string Value = "value";
        public const string Visibility = "visibility";
        public const string Min = "min";
        public const string Max = "max";
        public const string Step = "step";
        public const string RangeOverride = "range_override";
    }

    public override ConfigParamDto Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument doc = JsonDocument.ParseValue(ref reader);
        JsonElement pv = doc.RootElement;

        if (pv.ValueKind == JsonValueKind.Object && pv.TryGetProperty(EnvelopeMember.Value, out JsonElement value))
        {
            foreach (JsonProperty member in pv.EnumerateObject())
            {
                if (member.Name is not (EnvelopeMember.Value or EnvelopeMember.Visibility or EnvelopeMember.Min
                    or EnvelopeMember.Max or EnvelopeMember.Step or EnvelopeMember.RangeOverride))
                {
                    throw new JsonException(
                        $"Unknown key '{member.Name}' in a parameter envelope. A wrapped parameter may declare only "
                        + "value, visibility, min, max, step and range_override.");
                }
            }

            return new ConfigParamDto
            {
                Value = value.Clone(),
                Visibility = ReadVisibility(pv),
                Min = Number(pv, EnvelopeMember.Min),
                Max = Number(pv, EnvelopeMember.Max),
                Step = Number(pv, EnvelopeMember.Step),
                RangeOverride = ReadRangeOverride(pv),
            };
        }

        // Shorthand: the token itself is the value, and the form is the state — a locked structural constant
        // (loader switches, model-slot refs, plumbing). A param that is a real knob is written as an envelope
        // with an explicit visibility instead.
        return new ConfigParamDto { Value = pv.Clone(), Visibility = ParamVisibility.Locked };
    }

    /// <summary>The envelope's mandatory <c>visibility</c>, with no coercion: absent, non-string and unrecognized
    /// spellings all throw — a typo that silently defaulted would flip a param's exposure without a trace.</summary>
    private static ParamVisibility ReadVisibility(JsonElement envelope)
    {
        if (!envelope.TryGetProperty(EnvelopeMember.Visibility, out JsonElement v))
        {
            throw new JsonException("A parameter envelope must declare visibility: \"exposed\", \"hidden\" or \"locked\".");
        }

        string? token = v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return token switch
        {
            ParamVisibilityTokens.Exposed => ParamVisibility.Exposed,
            ParamVisibilityTokens.Hidden => ParamVisibility.Hidden,
            ParamVisibilityTokens.Locked => ParamVisibility.Locked,
            _ => throw new JsonException(
                $"Unrecognized visibility {v.GetRawText()}. A parameter's visibility is exactly one of "
                + "\"exposed\", \"hidden\" or \"locked\"."),
        };
    }

    public override void Write(Utf8JsonWriter writer, ConfigParamDto value, JsonSerializerOptions options) =>
        throw new NotSupportedException("The catalog is read-only; configuration params are never serialized out.");

    private static double? Number(JsonElement e, string key) =>
        e.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static ConfigParamRangeOverrideDto? ReadRangeOverride(JsonElement envelope)
    {
        if (!envelope.TryGetProperty(EnvelopeMember.RangeOverride, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("A parameter's range_override must be an object.");
        }

        ConfigParamRangeOverrideDto result = value.Deserialize(CatalogJsonContext.Default.ConfigParamRangeOverrideDto)
            ?? throw new JsonException("A parameter's range_override cannot be null.");
        if (result.Min is null && result.Max is null)
        {
            throw new JsonException("A parameter's range_override must declare min, max, or both.");
        }

        if (string.IsNullOrWhiteSpace(result.Label) || string.IsNullOrWhiteSpace(result.Warning))
        {
            throw new JsonException("A parameter's range_override must declare non-empty label and warning text.");
        }

        if (result.Min is double min && result.Max is double max && min > max)
        {
            throw new JsonException($"A parameter's range_override min ({min}) cannot exceed its max ({max}).");
        }

        return result;
    }
}
