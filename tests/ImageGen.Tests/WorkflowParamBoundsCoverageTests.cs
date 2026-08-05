using ImageGen.Comfy;
using Microsoft.Extensions.DependencyInjection;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json.Serialization;

namespace ImageGen.Tests;

/// <summary>
/// Completeness + no-drift for the #102 bounds: every param a workflow's SCHEMA declares a <c>Min</c>/<c>Max</c> for
/// AND that its typed params DTO actually reads must carry a matching <c>[Range]</c> on that DTO member — so the bound
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

        List<string> gaps = new List<string>();
        foreach (IWorkflow wf in registry.All)
        {
            Type? dto = ParamsType(wf.GetType());
            if (dto is null) continue;   // a workflow not built on Workflow<TParams> exposes no typed DTO to annotate

            Dictionary<string, PropertyInfo> byWireKey = new Dictionary<string, PropertyInfo>();
            foreach (PropertyInfo p in dto.GetProperties())
                if (p.GetCustomAttribute<JsonPropertyNameAttribute>() is { } jn)
                    byWireKey[jn.Name] = p;

            foreach (ParamSpec spec in wf.Schema)
            {
                if (spec.Min is null && spec.Max is null) continue;
                // Only params the DTO actually READS need enforcement; one the workflow ignores is inert (STJ drops it).
                if (!byWireKey.TryGetValue(spec.Key, out PropertyInfo? prop)) continue;

                RangeAttribute? range = prop.GetCustomAttribute<RangeAttribute>();
                if (range is null)
                {
                    gaps.Add($"{wf.Name}.{spec.Key}: schema declares [{spec.Min}, {spec.Max}] but the DTO member '{prop.Name}' has no [Range]");
                    continue;
                }
                double? rmin = ToDouble(range.Minimum), rmax = ToDouble(range.Maximum);
                if (spec.Min is double smin && rmin != smin)
                    gaps.Add($"{wf.Name}.{spec.Key}: [Range] min {rmin} != schema min {smin}");
                if (spec.Max is double smax && rmax != smax)
                    gaps.Add($"{wf.Name}.{spec.Key}: [Range] max {rmax} != schema max {smax}");
            }
        }

        Assert.True(gaps.Count == 0,
            "These declared bounds are shown to the UI but not enforced on the typed model (or the two disagree), "
            + "so a value past the slider reaches the graph unchecked:\n  " + string.Join("\n  ", gaps.OrderBy(x => x)));
    }

    private static Type? ParamsType(Type? t)
    {
        while (t is not null)
        {
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Workflow<>))
                return t.GetGenericArguments()[0];
            t = t.BaseType;
        }
        return null;
    }

    private static double? ToDouble(object? o) => o is null ? null : Convert.ToDouble(o);
}
