using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImageGen.Domain;

/// <summary>
/// A user's true / false / not-provided selection, as three NAMED states instead of a <c>bool?</c> whose <c>null</c>
/// means "not provided" only by a comment. <see cref="Unspecified"/> is 0 — so <c>default(TriState)</c> reproduces the
/// old <c>bool? = null</c> semantics without a nullable value type: the value is guaranteed at the DTO/API boundary and
/// the "not provided" case is a real state no downstream layer has to <c>?? default</c> or <c>== true</c> around.
/// <para>Use this ONLY where the three states are a user selection with a fall-back default (e.g. random-artist:
/// true = do it, false = don't, Unspecified = use the account default). A <c>bool?</c> whose <c>null</c> means
/// <i>unknown</i> or <i>not applicable</i> is a different shape and is not this.</para>
/// <para>On the wire it is a boolean-or-absent, not an enum name: <see cref="TriStateJsonConverter"/> maps JSON
/// <c>true</c>→<see cref="True"/>, <c>false</c>→<see cref="False"/>, and <c>null</c>/omitted→<see cref="Unspecified"/>,
/// so existing clients that send <c>randomArtist: true|false</c> or omit it are unaffected. Persisted as a nullable
/// bit (<c>NULL</c>→<see cref="Unspecified"/>), mapped at the repository boundary.</para>
/// </summary>
[JsonConverter(typeof(TriStateJsonConverter))]
public enum TriState : byte
{
    /// <summary>The caller sent nothing — fall back to the default. MUST be 0 so it is <c>default(TriState)</c>.</summary>
    Unspecified = 0,

    /// <summary>An explicit yes.</summary>
    True = 1,

    /// <summary>An explicit no, distinct from <see cref="Unspecified"/>.</summary>
    False = 2,
}

/// <summary>
/// Maps <see cref="TriState"/> to and from its boolean-or-absent wire spelling — the single place the enum meets JSON,
/// so a client posting <c>randomArtist: true|false</c> or omitting it keeps working. Kept beside the type (like
/// <c>TokenKindWire</c>) rather than as a private converter in one endpoint, so every serializer agrees.
/// </summary>
public sealed class TriStateJsonConverter : JsonConverter<TriState>
{
    /// <summary>An omitted property never reaches a converter (the record default, Unspecified, stands); an explicit
    /// null does, so this is on to fold a null into Unspecified rather than let STJ throw on a null for a
    /// non-nullable value type.</summary>
    public override bool HandleNull => true;

    public override TriState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.True => TriState.True,
            JsonTokenType.False => TriState.False,
            JsonTokenType.Null => TriState.Unspecified,
            _ => throw new JsonException($"Expected true, false or null for {nameof(TriState)}; got {reader.TokenType}."),
        };

    public override void Write(Utf8JsonWriter writer, TriState value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case TriState.True: writer.WriteBooleanValue(true); break;
            case TriState.False: writer.WriteBooleanValue(false); break;
            default: writer.WriteNullValue(); break;   // Unspecified — the "not provided" wire value
        }
    }
}
