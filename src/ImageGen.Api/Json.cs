using System.Text.Json;

namespace ImageGen.Api;

/// <summary>
/// Shared JSON settings and an explicit body reader. Endpoints deserialize bodies through this rather
/// than relying on minimal-API model binding, so request parsing/validation is hand-controlled.
/// </summary>
public static class Json
{
    /// <summary>
    /// The two <c>Respect*</c> flags make the non-nullable/required annotations on the wire DTOs actually enforced at the
    /// boundary instead of documentation: a non-optional ctor parameter (no default) or a <c>required</c> member missing
    /// from the payload throws, and a present <c>null</c> in a non-nullable member throws — a clean rejection here beats
    /// a silent <c>null</c> that blows up deeper in the pipeline. Both default to <c>false</c> even on .NET 10, so they
    /// must be set explicitly; the minimal-API binding path is configured to match via <c>ConfigureHttpJsonOptions</c>.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };

    /// <summary>Deserialize the request body, or null if the body is empty/invalid JSON or a declared-required member
    /// (a non-optional ctor parameter, a <c>required</c> property, or a non-nullable member given a present <c>null</c>)
    /// is missing/invalid.</summary>
    public static async Task<T?> ReadAsync<T>(HttpContext context) where T : class
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(context.Request.Body, Options, context.RequestAborted);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
