using ImageGen.Comfy;
using Microsoft.Extensions.DependencyInjection;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImageGen.Tests;

/// <summary>
/// Completeness + no-drift for the #102 bounds: every param a workflow's SCHEMA declares a <c>Min</c>/<c>Max</c> for
/// must be read by its typed params DTO and carry a matching <c>[Range]</c> on that DTO member — so the bound
/// the UI slider shows is the bound the server enforces, on every workflow, not just a hand-picked few. A schema bound
/// with no attribute (or an attribute whose numbers disagree) is the gap this fails on, naming each offender.
/// </summary>
public sealed class WorkflowParamBoundsCoverageTests
{
    [Fact]
    public void Every_schema_declared_bound_is_enforced_by_a_matching_range_attribute_on_the_dto()
    {
        WorkflowRegistry registry = new ServiceCollection().AddWorkflows().BuildServiceProvider()
            .GetRequiredService<WorkflowRegistry>();
        Dictionary<string, HashSet<string>> configured = ConfiguredKeys();

        List<string> gaps = [];
        foreach (IWorkflow wf in registry.All)
        {
            Dictionary<string, List<PropertyInfo>> byWireKey = [];
            foreach (Type contract in wf.ParameterContracts.Append(typeof(SubmissionCommon)))
            {
                foreach (PropertyInfo p in contract.GetProperties())
                {
                    if (p.GetCustomAttribute<JsonPropertyNameAttribute>() is { } jn)
                    {
                        if (!byWireKey.TryGetValue(jn.Name, out List<PropertyInfo>? properties))
                        {
                            byWireKey[jn.Name] = properties = [];
                        }

                        properties.Add(p);
                    }
                }
            }

            foreach (ParamSpec spec in wf.Schema)
            {
                if ((spec.Min is null && spec.Max is null)
                    || !configured.GetValueOrDefault(wf.Name, []).Contains(spec.Key))
                {
                    continue;
                }

                if (!byWireKey.TryGetValue(spec.Key, out List<PropertyInfo>? properties))
                {
                    gaps.Add($"{wf.Name}.{spec.Key}: schema declares [{spec.Min}, {spec.Max}] but its typed DTO does not read the key");
                    continue;
                }

                PropertyInfo prop = properties.FirstOrDefault(p => p.GetCustomAttribute<RangeAttribute>() is not null)
                    ?? properties[0];
                RangeAttribute? range = prop.GetCustomAttribute<RangeAttribute>();
                if (range is null)
                {
                    gaps.Add($"{wf.Name}.{spec.Key}: schema declares [{spec.Min}, {spec.Max}] but the DTO member '{prop.Name}' has no [Range]");
                    continue;
                }

                double? rmin = ToDouble(range.Minimum), rmax = ToDouble(range.Maximum);
                if (spec.Min is double smin && rmin != smin)
                {
                    gaps.Add($"{wf.Name}.{spec.Key}: [Range] min {rmin} != schema min {smin}");
                }

                if (spec.Max is double smax && rmax != smax)
                {
                    gaps.Add($"{wf.Name}.{spec.Key}: [Range] max {rmax} != schema max {smax}");
                }
            }
        }

        Assert.True(gaps.Count == 0,
            "These declared bounds are shown to the UI but not enforced on the typed model (or the two disagree), "
            + "so a value past the slider reaches the graph unchecked:\n  " + string.Join("\n  ", gaps.OrderBy(x => x)));
    }

    private static Dictionary<string, HashSet<string>> ConfiguredKeys()
    {
        Dictionary<string, HashSet<string>> result = new(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "configurations", "workflows"), "*.json"))
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("params", out JsonElement parameters))
            {
                continue;
            }

            string workflow = root.GetProperty("workflow").RequireString();
            HashSet<string> keys = result.GetValueOrDefault(workflow)
                ?? (result[workflow] = new(StringComparer.Ordinal));
            keys.UnionWith(parameters.EnumerateObject().Select(p => p.Name));
        }

        return result;
    }

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "configurations", "workflows")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new DirectoryNotFoundException("repo root not found.");
    }

    private static double? ToDouble(object? o) => o is null ? null : Convert.ToDouble(o);
}
