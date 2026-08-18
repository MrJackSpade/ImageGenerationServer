using ImageGen.Application.Rendering;
using ImageGen.Application.Snapshots;
using ImageGen.Comfy;
using ImageGen.Comfy.Snapshots;
using ImageGen.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
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

    [Theory]
    [InlineData("ideogram4")]
    [InlineData("ideogram4-refine")]
    public void Ideogram4_workflows_ship_a_hidden_multiline_json_template(string configId)
    {
        string path = Path.Combine(RepoRoot(), "configurations", "workflows", configId + ".json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement parameter = document.RootElement.GetProperty("params").GetProperty(WorkflowParamKeys.PromptTemplate);
        Assert.Equal("hidden", parameter.GetProperty("visibility").GetString());

        string source = parameter.GetProperty("value").RequireString();
        string rendered = PromptTemplates.Render(source, "a lighthouse at dusk", configId);
        using JsonDocument output = JsonDocument.Parse(rendered);
        Assert.Equal("a lighthouse at dusk", output.RootElement.GetProperty("high_level_description").GetString());
        JsonElement element = output.RootElement.GetProperty("compositional_deconstruction")
            .GetProperty("elements")[0];
        Assert.Equal("obj", element.GetProperty("type").GetString());
        Assert.Equal("a lighthouse at dusk", element.GetProperty("desc").GetString());

        WorkflowRegistry registry = new ServiceCollection().AddWorkflows().BuildServiceProvider()
            .GetRequiredService<WorkflowRegistry>();
        IWorkflow workflow = Assert.IsAssignableFrom<IWorkflow>(registry.Find(configId));
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

    [Fact]
    public async Task Ideogram4_submission_posts_the_rendered_json_prompt_to_comfyui()
    {
        WorkflowCatalog catalog = new(
            new ComfyOptions { CatalogPath = Path.Combine(RepoRoot(), "configurations") },
            NullLogger<WorkflowCatalog>.Instance);
        catalog.SetBindings(catalog.AllRequirements().ToDictionary(r => r.Id, r => r.Id + ".safetensors"));
        WorkflowRegistry registry = new ServiceCollection().AddWorkflows().BuildServiceProvider()
            .GetRequiredService<WorkflowRegistry>();
        CapturePromptHandler handler = new();
        HttpClient http = new(handler);
        ComfyClient client = new(
            new FixedHttpClientFactory(http),
            new FixedEndpoint(),
            catalog,
            registry,
            new MediaProcessor(new MediaOptions()),
            new FixedSnapshot<ComfyFilesByKind>(new ComfyFilesByKind(
                new Dictionary<RequirementKind, IReadOnlyList<string>>())),
            NullLogger<ComfyClient>.Instance);
        const string prompt = "A sign reading \"hello\" beneath a lighthouse";

        SubmitResult submission = await client.SubmitGenerateAsync(
            prompt, null, "ideogram4", "square", null, null, CancellationToken.None);

        Assert.NotNull(handler.Body);
        using JsonDocument request = JsonDocument.Parse(handler.Body);
        JsonElement graph = request.RootElement.GetProperty("prompt");
        JsonElement encode = Assert.Single(graph.EnumerateObject(), node =>
            node.Value.GetProperty("class_type").GetString() == "CLIPTextEncode").Value;
        string modelPrompt = encode.GetProperty("inputs").GetProperty("text").RequireString();
        Assert.Equal(modelPrompt, submission.ModelPrompt);
        using JsonDocument rendered = JsonDocument.Parse(modelPrompt);
        Assert.Equal(prompt, rendered.RootElement.GetProperty("high_level_description").GetString());
        Assert.Equal(prompt, rendered.RootElement.GetProperty("compositional_deconstruction")
            .GetProperty("elements")[0].GetProperty("desc").GetString());
    }

    [Fact]
    public async Task Ideogram4_refine_submission_posts_the_rendered_json_prompt_to_comfyui()
    {
        WorkflowCatalog catalog = new(
            new ComfyOptions { CatalogPath = Path.Combine(RepoRoot(), "configurations") },
            NullLogger<WorkflowCatalog>.Instance);
        catalog.SetBindings(catalog.AllRequirements().ToDictionary(r => r.Id, r => r.Id + ".safetensors"));
        WorkflowRegistry registry = new ServiceCollection().AddWorkflows().BuildServiceProvider()
            .GetRequiredService<WorkflowRegistry>();
        CapturePromptHandler handler = new();
        ComfyClient client = new(
            new FixedHttpClientFactory(new HttpClient(handler)),
            new FixedEndpoint(),
            catalog,
            registry,
            new MediaProcessor(new MediaOptions()),
            new FixedSnapshot<ComfyFilesByKind>(new ComfyFilesByKind(
                new Dictionary<RequirementKind, IReadOnlyList<string>>())),
            NullLogger<ComfyClient>.Instance);
        const string prompt = "A sign reading \"hello\" beneath a lighthouse";
        byte[] source = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

        SubmitResult submission = await client.SubmitEditAsync(
            source, prompt, null, "ideogram4-refine", null, null, ct: CancellationToken.None);

        Assert.NotNull(handler.Body);
        using JsonDocument request = JsonDocument.Parse(handler.Body);
        JsonElement graph = request.RootElement.GetProperty("prompt");
        JsonElement encode = Assert.Single(graph.EnumerateObject(), node =>
            node.Value.GetProperty("class_type").GetString() == "CLIPTextEncode").Value;
        string modelPrompt = encode.GetProperty("inputs").GetProperty("text").RequireString();
        Assert.Equal(modelPrompt, submission.ModelPrompt);
        using JsonDocument rendered = JsonDocument.Parse(modelPrompt);
        Assert.Equal(prompt, rendered.RootElement.GetProperty("high_level_description").GetString());
        Assert.Equal(prompt, rendered.RootElement.GetProperty("compositional_deconstruction")
            .GetProperty("elements")[0].GetProperty("desc").GetString());
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

    private sealed class FixedHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class FixedEndpoint : IComfyEndpoint
    {
        public string BaseUrl => "http://comfy.test";
        public string GateToken => "test-token";
    }

    private sealed class FixedSnapshot<T>(T value) : ISnapshot<T>
    {
        public ValueTask<T> GetAsync(CancellationToken ct) => new(value);
        public T PeekCurrent() => value;
        public void Invalidate() { }
    }

    private sealed class CapturePromptHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri?.AbsolutePath == "/upload/image")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"name\":\"forgemcp_edit_src.png\"}", Encoding.UTF8, "application/json"),
                };
            }

            Assert.Equal("/prompt", request.RequestUri?.AbsolutePath);
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"prompt_id\":\"captured\"}", Encoding.UTF8, "application/json"),
            };
        }
    }
}
