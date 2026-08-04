using System.Text.Json;

namespace ImageGen.Api;

/// <summary>
/// Shared JSON settings and an explicit body reader. Endpoints deserialize bodies through this rather
/// than relying on minimal-API model binding, so request parsing/validation is hand-controlled.
/// </summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Deserialize the request body, or null if the body is empty/invalid JSON or fails a required-member check.</summary>
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
