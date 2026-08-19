using ImageGen.Application.Rendering;
using ImageGen.Domain.CodeAnalysis;
using System.Text.Json;

namespace ImageGen.Comfy;

/// <summary>Shared Low/Medium/High edit-quality contract. Configurations own the three MP numbers; requests select a
/// label, and every workflow turns that number into aspect-preserving working dimensions on its own grid.</summary>
[AllowMagicStrings("validation messages and human-readable quality setting labels")]
internal static class EditQuality
{
    public static readonly IReadOnlyList<ParamSpec> Schema =
    [
        new() { Key = WorkflowParamKeys.EditQuality, Type = ParamType.Enum, Choices = [EditQualityValues.Low, EditQualityValues.Medium, EditQualityValues.High], Default = EditQualityValues.Medium,
            Label = "Quality", Help = "Working resolution budget; the source aspect ratio is preserved." },
        new() { Key = WorkflowParamKeys.EditQualityLowMp, Type = ParamType.Double, Min = 0.01, Max = 16, Step = 0.01,
            Label = "Low quality (MP)", Help = "Working megapixel budget selected by Low." },
        new() { Key = WorkflowParamKeys.EditQualityMediumMp, Type = ParamType.Double, Min = 0.01, Max = 16, Step = 0.01,
            Label = "Medium quality (MP)", Help = "Working megapixel budget selected by Medium." },
        new() { Key = WorkflowParamKeys.EditQualityHighMp, Type = ParamType.Double, Min = 0.01, Max = 16, Step = 0.01,
            Label = "High quality (MP)", Help = "Working megapixel budget selected by High." },
    ];

    public static ParamSpec? Spec(string key) =>
        Schema.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));

    public static double? Resolve(IWorkflow workflow, IReadOnlyDictionary<string, object?> values)
    {
        if (!workflow.SupportsEditQuality)
        {
            return null;
        }

        string quality = Text(values, WorkflowParamKeys.EditQuality) ?? EditQualityValues.Medium;
        string budgetKey = quality switch
        {
            EditQualityValues.Low => WorkflowParamKeys.EditQualityLowMp,
            EditQualityValues.Medium => WorkflowParamKeys.EditQualityMediumMp,
            EditQualityValues.High => WorkflowParamKeys.EditQualityHighMp,
            _ => throw new RenderValidationException($"Unknown edit quality '{quality}'. Choose Low, Medium, or High."),
        };

        double low = Number(workflow, values, WorkflowParamKeys.EditQualityLowMp);
        double medium = Number(workflow, values, WorkflowParamKeys.EditQualityMediumMp);
        double high = Number(workflow, values, WorkflowParamKeys.EditQualityHighMp);
        if (!(low <= medium && medium <= high))
        {
            throw new RenderValidationException("Edit quality MP budgets must be ordered Low <= Medium <= High.");
        }

        return budgetKey == WorkflowParamKeys.EditQualityLowMp ? low
            : budgetKey == WorkflowParamKeys.EditQualityHighMp ? high : medium;
    }

    private static string? Text(IReadOnlyDictionary<string, object?> values, string key)
    {
        if (!values.TryGetValue(key, out object? value) || value is null)
        {
            return null;
        }

        return value is JsonElement e ? e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString() : value.ToString();
    }

    private static double Number(IWorkflow workflow, IReadOnlyDictionary<string, object?> values, string key)
    {
        if (!values.TryGetValue(key, out object? value) || value is null)
        {
            throw new RenderValidationException($"Workflow '{workflow.Name}' is missing its '{key}' edit-quality budget.");
        }

        double number = ParamsCodec.AsDouble(value);
        if (number is < 0.01 or > 16)
        {
            throw new RenderValidationException($"Edit quality budget '{key}' must be between 0.01 and 16 MP.");
        }

        return number;
    }
}

/// <summary>Stable selector wire values.</summary>
internal static class EditQualityValues
{
    public const string Low = "Low";
    public const string Medium = "Medium";
    public const string High = "High";
}
