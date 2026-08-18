using ImageGen.Application.Rendering;
using ImageGen.Comfy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace ImageGen.Tests;

/// <summary>Workflow prompt templates use native Jinja syntax and stay opt-in at the configuration layer.</summary>
public sealed class PromptTemplateTests
{
    [Fact]
    public void Missing_template_is_identity_and_tojson_preserves_the_exact_prompt()
    {
        const string prompt = "A sign reading \"hello\"\nunder blue light";
        Assert.Equal(prompt, PromptTemplates.Render(null, prompt, "plain-workflow"));
        Assert.Equal(prompt, PromptTemplates.Render("   ", prompt, "plain-workflow"));

        string rendered = PromptTemplates.Render(
            "{\"high_level_description\":{{ prompt | tojson }}}", prompt, "json-workflow");
        using JsonDocument document = JsonDocument.Parse(rendered);
        Assert.Equal(prompt, document.RootElement.GetProperty("high_level_description").GetString());
    }

    [Fact]
    public void Undefined_template_variable_is_a_workflow_specific_render_validation_error()
    {
        RenderValidationException error = Assert.Throws<RenderValidationException>(() =>
            PromptTemplates.Render("{{ missing_variable }}", "test", "broken-workflow"));

        Assert.Contains("broken-workflow", error.Message);
        Assert.Contains("invalid prompt template", error.Message);
    }

    [Fact]
    public void Ideogram4_ships_a_hidden_multiline_json_template()
    {
        string path = Path.Combine(RepoRoot(), "configurations", "workflows", "ideogram4.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement parameter = document.RootElement.GetProperty("params").GetProperty(WorkflowParamKeys.PromptTemplate);
        Assert.Equal("hidden", parameter.GetProperty("visibility").GetString());

        string source = parameter.GetProperty("value").RequireString();
        string rendered = PromptTemplates.Render(source, "a lighthouse at dusk", "ideogram4");
        using JsonDocument output = JsonDocument.Parse(rendered);
        Assert.Equal("a lighthouse at dusk", output.RootElement.GetProperty("high_level_description").GetString());
        JsonElement element = output.RootElement.GetProperty("compositional_deconstruction")
            .GetProperty("elements")[0];
        Assert.Equal("obj", element.GetProperty("type").GetString());
        Assert.Equal("a lighthouse at dusk", element.GetProperty("desc").GetString());

        WorkflowRegistry registry = new ServiceCollection().AddWorkflows().BuildServiceProvider()
            .GetRequiredService<WorkflowRegistry>();
        IWorkflow workflow = Assert.IsAssignableFrom<IWorkflow>(registry.Find("ideogram4"));
        ParamSpec spec = Assert.Single(workflow.Schema, p => p.Key == WorkflowParamKeys.PromptTemplate);
        Assert.Equal(ParamType.Multiline, spec.Type);
        Assert.Equal("Prompt template", spec.Label);
    }

    [Fact]
    public void Json_looking_machine_override_remains_template_text()
    {
        WorkflowCatalog catalog = new(
            new ComfyOptions { CatalogPath = Path.Combine(RepoRoot(), "configurations") },
            NullLogger<WorkflowCatalog>.Instance);
        const string template = "{\"high_level_description\":\"fixed caption\"}";

        catalog.SetParamOverrides(new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["ideogram4"] = new Dictionary<string, string>
            {
                ["param.prompt_template"] = template,
            },
        });

        JsonElement stored = catalog.ParamOverridesFor("ideogram4")[WorkflowParamKeys.PromptTemplate];
        Assert.Equal(JsonValueKind.String, stored.ValueKind);
        Assert.Equal(template, stored.GetString());
    }

    private static string RepoRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "ImageGen.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
