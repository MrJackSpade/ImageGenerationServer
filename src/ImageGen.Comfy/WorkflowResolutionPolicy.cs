using System.Text.Json;

namespace ImageGen.Comfy;

/// <summary>
/// The per-machine, per-workflow decision to permit image generation outside the model's documented training
/// envelope. The envelope remains metadata for warnings and ordinary operation; enabling this policy removes its
/// min/max/grid enforcement while retaining the universal requirement that both dimensions are positive.
/// </summary>
internal static class WorkflowResolutionText
{
    public const string SettingKey = "allowUntrainedResolution";
    public const string Label = "Allow untrained resolutions";
    public const string Warning =
        "Allows any positive width and height, including sizes outside this model's trained range and grid. "
        + "Those sizes may fail, degrade output, or exhaust memory.";
}

internal static class WorkflowResolutionPolicy
{

    /// <summary>Whether this workflow enabled arbitrary positive image dimensions on this machine.</summary>
    public static bool IsEnabled(IReadOnlyDictionary<string, JsonElement> machine)
    {
        if (!machine.TryGetValue(WorkflowResolutionText.SettingKey, out JsonElement value))
        {
            return false;
        }

        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean();
        }

        return value.ValueKind == JsonValueKind.String
            && bool.TryParse(value.GetString(), out bool enabled)
            && enabled;
    }

    /// <summary>A durable warning for a requested size outside the trained envelope, or null when no warning applies.</summary>
    public static string? WarningFor(ModelResolution? envelope, int width, int height)
    {
        if (envelope is null || ResolutionGuard.RenderSizeViolation(envelope, width, height) is null)
        {
            return null;
        }

        return $"{width}×{height} is outside this model's trained resolution range. "
            + "This workflow allows it, but the render may fail, degrade, or exhaust memory.";
    }
}
