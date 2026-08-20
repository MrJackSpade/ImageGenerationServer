using ImageGen.Application.Rendering;
using ImageGen.Domain.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>Deserializes a HunyuanVideo 1.5 params base into the concrete SR-or-not contract chosen by the boolean
/// <c>sr</c> toggle in the flat bag — the params equivalent of a discriminated union. STJ can key polymorphism on a
/// string/int discriminator but not a bool, and the SR knobs are flat model-ref config (can't be nested), so this reads
/// <c>sr</c> and materializes the matching concrete shape (audit #125 C). Registered against the abstract base only, so
/// deserializing the concrete subtype does not recurse.</summary>
public abstract class HunyuanSrToggleConverter<TBase, TSr, TNoSr> : JsonConverter<TBase>
    where TSr : TBase where TNoSr : TBase
{
    public override TBase Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument doc = JsonDocument.ParseValue(ref reader);
        JsonElement root = doc.RootElement;
        bool sr = false;
        if (root.TryGetProperty(WorkflowParamKeys.Sr, out JsonElement e))
        {
            if (e.ValueKind == JsonValueKind.True)
            {
                sr = true;
            }
            else if (e.ValueKind != JsonValueKind.False)
            {
                throw new RenderValidationException($"'{WorkflowParamKeys.Sr}' must be a JSON boolean.");
            }
        }

        TBase? dto = sr ? root.Deserialize<TSr>(options) : root.Deserialize<TNoSr>(options);
        return dto ?? throw new JsonException($"The merged parameters could not be read as {typeToConvert.Name}.");
    }

    [AllowMagicStrings("exception message")]
    public override void Write(Utf8JsonWriter writer, TBase value, JsonSerializerOptions options)
        => throw new NotSupportedException("Params DTOs are read from config, never written.");
}
