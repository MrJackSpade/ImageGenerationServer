using ImageGen.Comfy;
using ImageGen.Application.Rendering;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImageGen.Tests;

/// <summary>Seed is a normal, revealable workflow parameter everywhere a typed workflow consumes it.</summary>
public sealed class SeedParameterTests
{
    [Fact]
    public void Blank_or_missing_seed_is_random_but_explicit_zero_is_pinned()
    {
        Dictionary<string, JsonElement> missing = RenderOrchestrator.WithSeed(null);
        Assert.InRange(missing[WorkflowParamKeys.Seed].GetInt64(), 0, RenderSeed.MaxExactValue);

        Dictionary<string, JsonElement> blank = RenderOrchestrator.WithSeed(new()
        {
            [WorkflowParamKeys.Seed] = JsonSerializer.SerializeToElement("  "),
        });
        Assert.Equal(JsonValueKind.Number, blank[WorkflowParamKeys.Seed].ValueKind);
        Assert.InRange(blank[WorkflowParamKeys.Seed].GetInt64(), 0, RenderSeed.MaxExactValue);

        Dictionary<string, JsonElement> zero = RenderOrchestrator.WithSeed(new()
        {
            [WorkflowParamKeys.Seed] = JsonSerializer.SerializeToElement(0L),
        });
        Assert.Equal(0, zero[WorkflowParamKeys.Seed].GetInt64());
        Assert.Equal(0, ComfyGraph.Seed(0));
    }

    [Fact]
    public void Every_seeded_workflow_configuration_declares_a_hidden_seed_control()
    {
        WorkflowRegistry registry = new ServiceCollection().AddWorkflows().BuildServiceProvider()
            .GetRequiredService<WorkflowRegistry>();
        List<string> offenders = [];

        foreach ((string id, JsonElement root) in Configurations())
        {
            string workflowName = root.GetProperty("workflow").RequireString();
            IWorkflow? workflow = registry.Find(workflowName);
            Assert.NotNull(workflow);
            if (!ConsumesSeed(workflow))
            {
                continue;
            }

            ParamSpec? spec = workflow.Schema.SingleOrDefault(p => p.Key == WorkflowParamKeys.Seed);
            if (spec is null)
            {
                offenders.Add($"{id}: workflow '{workflowName}' has a typed seed but no seed ParamSpec");
                continue;
            }

            if (spec.Type != ParamType.Int || spec.Label != "Seed")
            {
                offenders.Add($"{id}: seed ParamSpec must be an int labelled Seed");
            }

            if (!root.TryGetProperty("params", out JsonElement parameters)
                || !parameters.TryGetProperty(WorkflowParamKeys.Seed, out JsonElement seed)
                || seed.ValueKind != JsonValueKind.Object
                || !seed.TryGetProperty("visibility", out JsonElement visibility)
                || visibility.GetString() != "hidden")
            {
                offenders.Add($"{id}: params.seed must be present and hidden");
            }
        }

        Assert.True(offenders.Count == 0,
            "Every configuration backed by a typed seed parameter must make it hidden-but-revealable:\n  "
            + string.Join("\n  ", offenders));
    }

    private static bool ConsumesSeed(IWorkflow workflow)
    {
        for (Type? type = workflow.GetType(); type is not null; type = type.BaseType)
        {
            if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(Workflow<>))
            {
                continue;
            }

            Type parameters = type.GetGenericArguments()[0];
            return parameters.GetProperties().Any(property =>
                property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name == WorkflowParamKeys.Seed);
        }

        return false;
    }

    private static IEnumerable<(string Id, JsonElement Root)> Configurations()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "configurations", "workflows")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        if (dir is null)
        {
            throw new DirectoryNotFoundException("configurations/workflows not found.");
        }

        foreach (string file in Directory.EnumerateFiles(Path.Combine(dir, "configurations", "workflows"), "*.json"))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
            JsonElement root = document.RootElement.Clone();
            yield return (root.GetProperty("id").RequireString(), root);
        }
    }
}
