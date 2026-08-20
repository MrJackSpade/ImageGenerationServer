using ImageGen.Application.Rendering;
using ImageGen.Comfy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace ImageGen.Tests;

/// <summary>
/// Closure coverage for the custom still-image graph builders that bypass the standard txt2img topology. Every
/// configuration bound to one of these families must keep the composer's runtime LoRAs, supported negative prompt,
/// and ck_attention toggle connected to the emitted graph.
/// </summary>
public sealed class CustomImageRuntimeInputTests
{
    private static class TestValues
    {
        public const string Lora = "closure/runtime-input.safetensors";
    }

    private static readonly HashSet<string> TargetWorkflows = new(StringComparer.OrdinalIgnoreCase)
    {
        "chroma",
        "hidream",
        "sd35-large-tri",
        "hunyuanimage21",
        "ideogram4",
        "krea2-refine",
        "mage-flow",
        "mage-flow-turbo",
    };

    public static IEnumerable<object[]> TargetConfigurationIds()
    {
        using WorkflowCatalog catalog = CreateCatalog();
        return [.. catalog.AllConfigs()
            .Where(c => TargetWorkflows.Contains(c.WorkflowName))
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .Select(c => new object[] { c.Id })];
    }

    [Theory]
    [MemberData(nameof(TargetConfigurationIds))]
    public void Cross_cutting_runtime_inputs_affect_every_custom_image_configuration(string configId)
    {
        using WorkflowCatalog catalog = CreateCatalog();
        WorkflowRegistry registry = CreateRegistry();
        WorkflowConfiguration cfg = Assert.IsType<WorkflowConfiguration>(catalog.FindConfig(configId));
        IWorkflow workflow = Assert.IsAssignableFrom<IWorkflow>(registry.Find(cfg.WorkflowName));
        Dictionary<string, object?> merged = Merge(catalog, workflow, cfg);

        string baseline = BuildJson(catalog, workflow, cfg, merged, "closure-negative-a", [], ckAttention: false);

        string withLora = BuildJson(catalog, workflow, cfg, merged, "closure-negative-a",
            [new LoraSelection(TestValues.Lora, 0.73)], ckAttention: false);
        Assert.NotEqual(baseline, withLora);
        using (JsonDocument loraGraph = JsonDocument.Parse(withLora))
        {
            JsonProperty lora = Assert.Single(NodesOfType(loraGraph, "LoraLoader"));
            JsonElement inputs = lora.Value.GetProperty("inputs");
            Assert.Equal(TestValues.Lora, inputs.GetProperty("lora_name").GetString());
            Assert.Equal(0.73, inputs.GetProperty("strength_model").GetDouble());
            Assert.Equal(0.73, inputs.GetProperty("strength_clip").GetDouble());
            Assert.True(HasConsumer(loraGraph, lora.Name, outputIndex: 0), "The LoRA MODEL output is disconnected.");
            Assert.True(HasConsumer(loraGraph, lora.Name, outputIndex: 1), "The LoRA CLIP output is disconnected.");
        }

        string withCkAttention = BuildJson(catalog, workflow, cfg, merged, "closure-negative-a", [], ckAttention: true);
        Assert.NotEqual(baseline, withCkAttention);
        using (JsonDocument attentionGraph = JsonDocument.Parse(withCkAttention))
        {
            JsonProperty[] attentionNodes = [.. NodesOfType(attentionGraph, "ModelAttentionBackend")];
            Assert.NotEmpty(attentionNodes);
            Assert.All(attentionNodes, node =>
                Assert.True(HasConsumer(attentionGraph, node.Name, outputIndex: 0), $"Attention node {node.Name} is disconnected."));
        }

        if (SupportsNegativePrompt(merged))
        {
            string changedNegative = BuildJson(catalog, workflow, cfg, merged, "closure-negative-b", [], ckAttention: false);
            Assert.NotEqual(baseline, changedNegative);
            Assert.Contains("closure-negative-b", changedNegative, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Runtime_input_matrix_keeps_all_seven_custom_families_in_scope()
    {
        using WorkflowCatalog catalog = CreateCatalog();
        string[] registered = [.. catalog.AllConfigs()
            .Where(c => TargetWorkflows.Contains(c.WorkflowName))
            .Select(c => c.WorkflowName)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

        Assert.Contains("chroma", registered);
        Assert.Contains("hidream", registered);
        Assert.Contains("sd35-large-tri", registered);
        Assert.Contains("hunyuanimage21", registered);
        Assert.Contains("ideogram4", registered);
        Assert.Contains("krea2-refine", registered);
        Assert.Contains(registered, name => name is "mage-flow" or "mage-flow-turbo");
    }

    private static string BuildJson(
        WorkflowCatalog catalog,
        IWorkflow workflow,
        WorkflowConfiguration cfg,
        IReadOnlyDictionary<string, object?> baseline,
        string negative,
        IReadOnlyList<LoraSelection> loras,
        bool ckAttention)
    {
        Dictionary<string, object?> parameters = new(baseline, StringComparer.OrdinalIgnoreCase)
        {
            [WorkflowParamKeys.CkAttention] = ckAttention,
        };
        WorkflowInputs inputs = new()
        {
            Positive = "a lighthouse in a storm",
            Negative = negative,
            Aspect = "square",
            Loras = loras,
        };

        ComfyWorkflowGraph graph = workflow.Build(parameters, catalog.Resolve(cfg), inputs);
        Assert.NotEmpty(graph.Raw);
        return JsonSerializer.Serialize(graph);
    }

    private static IEnumerable<JsonProperty> NodesOfType(JsonDocument graph, string classType) =>
        graph.RootElement.EnumerateObject()
            .Where(node => node.Value.GetProperty("class_type").GetString() == classType);

    private static bool HasConsumer(JsonDocument graph, string producerId, int outputIndex) =>
        graph.RootElement.EnumerateObject().Any(node => ContainsEdge(node.Value.GetProperty("inputs"), producerId, outputIndex));

    private static bool ContainsEdge(JsonElement element, string producerId, int outputIndex)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            JsonElement.ArrayEnumerator values = element.EnumerateArray();
            if (values.MoveNext() && values.Current.ValueKind == JsonValueKind.String
                && values.Current.GetString() == producerId
                && values.MoveNext() && values.Current.ValueKind == JsonValueKind.Number
                && values.Current.GetInt32() == outputIndex
                && !values.MoveNext())
            {
                return true;
            }

            return element.EnumerateArray().Any(item => ContainsEdge(item, producerId, outputIndex));
        }

        return element.ValueKind == JsonValueKind.Object
            && element.EnumerateObject().Any(property => ContainsEdge(property.Value, producerId, outputIndex));
    }

    private static bool SupportsNegativePrompt(Dictionary<string, object?> parameters)
    {
        if (!parameters.TryGetValue(WorkflowParamKeys.NegativeSupported, out object? value))
        {
            return true;
        }

        return value switch
        {
            bool supported => supported,
            JsonElement json when json.ValueKind is JsonValueKind.True or JsonValueKind.False => json.GetBoolean(),
            _ => Convert.ToBoolean(value),
        };
    }

    private static Dictionary<string, object?> Merge(WorkflowCatalog catalog, IWorkflow workflow, WorkflowConfiguration cfg)
    {
        Dictionary<string, object?> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (ParamSpec spec in workflow.Schema)
        {
            if (spec.Default is not null)
            {
                values[spec.Key] = spec.Default;
            }
        }

        foreach (KeyValuePair<string, ConfigParam> parameter in cfg.Params)
        {
            values[parameter.Key] = parameter.Value.Value;
        }

        foreach (KeyValuePair<string, JsonElement> parameter in catalog.ParamOverridesFor(cfg.Id))
        {
            values[parameter.Key] = parameter.Value;
        }

        catalog.ResolveModelRefs(workflow, cfg.Id, values);
        return values;
    }

    private static WorkflowCatalog CreateCatalog()
    {
        WorkflowCatalog catalog = new(
            new ComfyOptions { CatalogPath = Path.Combine(RepoRoot(), "configurations") },
            NullLogger<WorkflowCatalog>.Instance);
        catalog.SetBindings(catalog.AllRequirements().ToDictionary(r => r.Id, r => r.Id + ".safetensors"));
        return catalog;
    }

    private static WorkflowRegistry CreateRegistry()
    {
        IWorkflow[] workflows = [.. new ServiceCollection()
            .AddWorkflows()
            .BuildServiceProvider()
            .GetServices<IWorkflow>()];
        return new WorkflowRegistry(workflows);
    }

    private static string RepoRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !Directory.Exists(Path.Combine(directory, "configurations", "models")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory ?? throw new DirectoryNotFoundException("configurations/ not found above the test bin directory.");
    }
}
