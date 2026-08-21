using System.Text.Json;

namespace ImageGen.Comfy;

/// <summary>Stable setting and warning text for the per-workflow arbitrary frame-count policy.</summary>
internal static class WorkflowFrameCountText
{
    public const string SettingKey = "allowUntrainedFrameCounts";
    public const string Label = "Allow untrained frame counts";
    public const string Warning =
        "Allows any positive frame count, including counts outside this model's trained range or temporal cadence. "
        + "ComfyUI may reject them, and renders may fail, degrade, or produce a non-video result.";
}

/// <summary>
/// The machine-level decision to pass an image-to-video or text-to-video frame count through without trained-range
/// validation or cadence snapping. The request still has to be a positive 32-bit integer. The old H3-only
/// <c>allowRangeOverride.length</c> value is read as a migration fallback; an explicit new value always wins.
/// </summary>
internal static class WorkflowFrameCountPolicy
{
    public static bool IsEnabled(IReadOnlyDictionary<string, JsonElement> machine)
    {
        if (machine.TryGetValue(WorkflowFrameCountText.SettingKey, out JsonElement value))
        {
            return Bool(value);
        }

        return WorkflowRangeOverridePolicy.IsEnabled(machine, WorkflowParamKeys.Length);
    }

    /// <summary>A slot warning when the preserved count violates the configured trained range or temporal cadence.</summary>
    public static string? WarningFor(
        WorkflowConfiguration config,
        FrameRule? rule,
        IReadOnlyDictionary<string, object?> values)
    {
        if (!values.TryGetValue(WorkflowParamKeys.Length, out object? raw) || raw is null)
        {
            return null;
        }

        int frames = ParamsCodec.AsInt(raw);
        ConfigParam? parameter = config.Params.GetValueOrDefault(WorkflowParamKeys.Length);
        bool outsideRange = (parameter?.Min is double min && frames < min)
            || (parameter?.Max is double max && frames > max);
        bool outsideCadence = rule is not null && !rule.IsValid(frames);
        if (!outsideRange && !outsideCadence)
        {
            return null;
        }

        string contract = rule is null
            ? "trained range"
            : $"trained range or {rule.Step}n+{rule.Base} cadence";
        return $"{frames} frames is outside this model's {contract}. "
            + "This workflow allows it, but ComfyUI may reject it and the render may fail, degrade, or not produce a video.";
    }

    private static bool Bool(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean();
        }

        return value.ValueKind == JsonValueKind.String
            && bool.TryParse(value.GetString(), out bool enabled)
            && enabled;
    }
}
