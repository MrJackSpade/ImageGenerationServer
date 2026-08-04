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
}

/// <summary>A <c>workflows/&lt;id&gt;.json</c> file: a workflow class bound to its settings layer, requirement links,
/// and decision card.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record WorkflowFileDto
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("workflow")] public string? Workflow { get; init; }
    [JsonPropertyName("friendly_name")] public string? FriendlyName { get; init; }
    [JsonPropertyName("requirements")] public RequirementLinksDto? Requirements { get; init; }
    [JsonPropertyName("params")] public Dictionary<string, ConfigParamDto>? Params { get; init; }
    [JsonPropertyName("effect_type")] public string? EffectType { get; init; }
    [JsonPropertyName("edit_group")] public string? EditGroup { get; init; }
    [JsonPropertyName("default")] public bool? Default { get; init; }
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
    [JsonPropertyName("min_w")] public int? MinW { get; init; }
    [JsonPropertyName("min_h")] public int? MinH { get; init; }
    [JsonPropertyName("max_w")] public int? MaxW { get; init; }
    [JsonPropertyName("max_h")] public int? MaxH { get; init; }
    [JsonPropertyName("step")] public int? Step { get; init; }
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
    [JsonPropertyName("measured_seconds")] public double? MeasuredSeconds { get; init; }
    [JsonPropertyName("measured_note")] public string? MeasuredNote { get; init; }
}

/// <summary>The card's <c>negative</c> block: whether the model uses a negative prompt, and a note if so.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record NegativeDto
{
    [JsonPropertyName("supported")] public bool? Supported { get; init; }
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

/// <summary>The card's <c>reference</c> block: how many reference images an editor takes, and a hint about them.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReferenceDto
{
    [JsonPropertyName("max")] public int? Max { get; init; }
    [JsonPropertyName("hint")] public string? Hint { get; init; }
}

/// <summary>The card's <c>tagging</c> block: the model's booru-tagging capability.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record TaggingDto
{
    [JsonPropertyName("tags")] public bool? Tags { get; init; }
    [JsonPropertyName("artists")] public bool? Artists { get; init; }
    [JsonPropertyName("keep_artist_marker")] public bool? KeepArtistMarker { get; init; }
    [JsonPropertyName("underscores_to_spaces")] public bool? UnderscoresToSpaces { get; init; }
}

/// <summary>
/// One entry of a configuration's <c>params</c> map, in either the bare-scalar shorthand (<c>"steps": 25</c>) or the
/// wrapped envelope form (<c>{ "value": 25, "exposed": true, "min": 1, "max": 50, "step": 1 }</c>). Handled by
/// <see cref="ConfigParamDtoConverter"/> because the two forms cannot be expressed as one static shape.
/// </summary>
[JsonConverter(typeof(ConfigParamDtoConverter))]
internal sealed record ConfigParamDto
{
    /// <summary>The parameter value, decoupled from the parsed document. A scalar, or an object/array (the aspect
    /// dims map, a reference-inputs list) captured whole.</summary>
    public required JsonElement Value { get; init; }

    /// <summary>Envelope form with <c>"exposed": true</c>: surfaced to the UI as an editable control.</summary>
    public bool Exposed { get; init; }

    /// <summary>Envelope form with an explicit <c>"exposed": false</c>: a baked, locked knob — hidden from the UI and
    /// not overridable by the request.</summary>
    public bool Locked { get; init; }

    public double? Min { get; init; }
    public double? Max { get; init; }
    public double? Step { get; init; }
}

/// <summary>
/// Reads a <c>params</c> entry in either form. The envelope form is signalled by an object carrying an explicit
/// <c>value</c> member; anything else — a bare scalar, a bare array, or an object without <c>value</c> (the aspect
/// map) — IS the value. The envelope's own keys are validated the same way the rest of the catalog is: a key other
/// than <c>value</c>/<c>exposed</c>/<c>min</c>/<c>max</c>/<c>step</c> is an error, not silently ignored.
/// </summary>
internal sealed class ConfigParamDtoConverter : JsonConverter<ConfigParamDto>
{
    /// <summary>A bare <c>null</c> param value is a real, meaningful value (e.g. <c>clip_type: null</c> = "let the
    /// loader decide"); without this, System.Text.Json short-circuits null to a null dictionary entry and it is lost.</summary>
    public override bool HandleNull => true;

    public override ConfigParamDto Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var pv = doc.RootElement;

        if (pv.ValueKind == JsonValueKind.Object && pv.TryGetProperty("value", out var value))
        {
            foreach (var member in pv.EnumerateObject())
                if (member.Name is not ("value" or "exposed" or "min" or "max" or "step"))
                    throw new JsonException(
                        $"Unknown key '{member.Name}' in a parameter envelope. A wrapped parameter may declare only "
                        + "value, exposed, min, max and step.");

            bool? exposed = pv.TryGetProperty("exposed", out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? e.GetBoolean()
                : null;
            return new ConfigParamDto
            {
                Value = value.Clone(),
                Exposed = exposed == true,
                Locked = exposed == false,
                Min = Number(pv, "min"),
                Max = Number(pv, "max"),
                Step = Number(pv, "step"),
            };
        }

        // Shorthand: the token itself is the value. Not exposed, not locked, no range overrides.
        return new ConfigParamDto { Value = pv.Clone() };
    }

    public override void Write(Utf8JsonWriter writer, ConfigParamDto value, JsonSerializerOptions options) =>
        throw new NotSupportedException("The catalog is read-only; configuration params are never serialized out.");

    private static double? Number(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
