using ImageGen.Application.Rendering;
using System.Text.Json;

namespace ImageGen.Comfy;

/// <summary>
/// Owns the machine-level switch for a configuration field's explicitly declared wider range. The declaration lives
/// in the workflow JSON; the operator's choice lives with that workflow in <c>ConfigOverride</c>. A render request
/// carries only the value — never a per-generation bypass flag.
/// </summary>
internal static class WorkflowRangeOverridePolicy
{
    private static class Keys
    {
        public const string SettingPrefix = "allowRangeOverride.";
    }

    /// <summary>The synthetic workflow-setting key for one configuration parameter.</summary>
    public static string SettingKey(string paramKey) => Keys.SettingPrefix + paramKey;

    /// <summary>Whether this machine enabled the wider range for this workflow field.</summary>
    public static bool IsEnabled(IReadOnlyDictionary<string, JsonElement> machine, string paramKey)
    {
        if (!machine.TryGetValue(SettingKey(paramKey), out JsonElement value))
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

    /// <summary>
    /// Enforce the workflow's selected range after its normalizer has converted user-facing aliases (notably seconds)
    /// into graph parameters. The typed DTO still enforces the model/node hard limit; this additional gate enforces
    /// the workflow's narrower recommended range until its operator enables the declared extension.
    /// </summary>
    public static void Validate(
        WorkflowConfiguration config,
        IReadOnlyDictionary<string, JsonElement> machine,
        IReadOnlyDictionary<string, object?> values)
    {
        foreach ((string key, ConfigParam parameter) in config.Params)
        {
            if (parameter.RangeOverride is not { } alternate
                || !values.TryGetValue(key, out object? raw)
                || raw is null)
            {
                continue;
            }

            bool enabled = IsEnabled(machine, key);
            double? min = enabled ? alternate.Min ?? parameter.Min : parameter.Min;
            double? max = enabled ? alternate.Max ?? parameter.Max : parameter.Max;
            double value = ParamsCodec.AsDouble(raw);
            if ((min is double lo && value < lo) || (max is double hi && value > hi))
            {
                string allowed = min is double lower && max is double upper
                    ? $"between {lower} and {upper}"
                    : min is double floor ? $"at least {floor}" : $"at most {max}";
                string remedy = enabled
                    ? ""
                    : $" Enable '{alternate.Label}' on this workflow's settings page to use its extended range.";
                throw new RenderValidationException(
                    $"'{key}' must be {allowed}, but was {value}.{remedy}");
            }
        }
    }
}
