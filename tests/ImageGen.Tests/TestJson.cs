using System.Text.Json;

namespace ImageGen.Tests;

/// <summary>Reading helpers for JSON fixtures, where a missing/null string is a broken fixture, not a case to handle.</summary>
internal static class TestJson
{
    /// <summary>The element's string value, or throws — for a required string in a test fixture.</summary>
    public static string RequireString(this JsonElement element) =>
        element.GetString() ?? throw new JsonException("Expected a JSON string value in the test fixture.");
}
