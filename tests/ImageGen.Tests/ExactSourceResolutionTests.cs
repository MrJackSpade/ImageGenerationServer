using ImageGen.Application.Rendering;
using ImageGen.Comfy;
using ImageGen.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace ImageGen.Tests;

/// <summary>Graph and catalogue contracts for the opt-in <c>exact_source_resolution</c> edit path and the per-VAE
/// round-trip configurations. Nothing here renders: the graphs are built and inspected as JSON.</summary>
public sealed class ExactSourceResolutionTests
{
    private static readonly string[] ResamplingNodes = ["ImageScale", "ImageScaleBy", "ImageScaleToTotalPixels"];

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "configurations", "workflows")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new DirectoryNotFoundException("configurations/workflows not found.");
    }

    private static (WorkflowCatalog Catalog, WorkflowRegistry Registry) Build()
    {
        WorkflowCatalog catalog = new(
            new ComfyOptions { CatalogPath = Path.Combine(RepoRoot(), "configurations") },
            NullLogger<WorkflowCatalog>.Instance);
        catalog.SetBindings(catalog.AllRequirements().ToDictionary(r => r.Id, r => r.Id + ".safetensors"));
        WorkflowRegistry registry = new ServiceCollection().AddWorkflows().BuildServiceProvider()
            .GetRequiredService<WorkflowRegistry>();
        return (catalog, registry);
    }

    private static Dictionary<string, object?> Merge(WorkflowCatalog catalog, IWorkflow workflow, WorkflowConfiguration config)
    {
        Dictionary<string, object?> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (ParamSpec spec in workflow.Schema.Where(s => s.Default is not null))
        {
            values[spec.Key] = spec.Default;
        }

        foreach ((string key, ConfigParam param) in config.Params)
        {
            values[key] = param.Value;
        }

        catalog.ResolveModelRefs(workflow, config.Id, values);
        return values;
    }

    /// <summary>Build <paramref name="configId"/>'s graph the way the submit path does for an exact-source request:
    /// the working canvas is the workflow's own padded size.</summary>
    private static JsonDocument BuildExact(string configId, int sourceWidth, int sourceHeight, out (int Width, int Height) padded)
    {
        (WorkflowCatalog catalog, WorkflowRegistry registry) = Build();
        WorkflowConfiguration config = Assert.IsType<WorkflowConfiguration>(catalog.FindConfig(configId));
        IWorkflow workflow = Assert.IsAssignableFrom<IWorkflow>(registry.Find(config.WorkflowName));
        padded = ExactSourceCanvas.Resolve(workflow, configId, sourceWidth, sourceHeight, hasSource: true, hasFixedAspect: false);
        WorkflowInputs inputs = new()
        {
            Positive = "cover the rear half with a box",
            SourceImageName = "source.png",
            SourceWidth = sourceWidth,
            SourceHeight = sourceHeight,
            TargetWidth = padded.Width,
            TargetHeight = padded.Height,
            ExactSourceResolution = true,
        };
        ComfyWorkflowGraph graph = workflow.Build(Merge(catalog, workflow, config), catalog.Resolve(config), inputs);
        return JsonDocument.Parse(JsonSerializer.Serialize(graph));
    }

    private static List<JsonProperty> NodesOf(JsonDocument document, string classType) =>
        [.. document.RootElement.EnumerateObject()
            .Where(n => n.Value.GetProperty("class_type").GetString() == classType)];

    [Theory]
    [InlineData(1216, 832, 16, 1216, 832)]
    [InlineData(500, 333, 16, 512, 336)]
    [InlineData(500, 333, 32, 512, 352)]
    [InlineData(1, 1, 32, 32, 32)]
    public void Padded_canvas_rounds_each_side_up_to_the_grid_and_never_below_the_source(
        int width, int height, int step, int paddedWidth, int paddedHeight) =>
        Assert.Equal((paddedWidth, paddedHeight), ExactSourceCanvas.Padded(width, height, step));

    [Theory]
    [InlineData("qwen-image-2-1-edit", 32)]
    [InlineData("qwen-image-edit", 16)]
    [InlineData("firered-image-edit", 16)]
    [InlineData("longcat-image-edit", 16)]
    [InlineData("qwen-rapid-aio", 16)]
    [InlineData("flux2-klein-4b-edit", 16)]
    [InlineData("flux2-klein-9b-edit", 16)]
    [InlineData("flux1-kontext", 16)]
    [InlineData("vae-roundtrip-qwen", 32)]
    public void Off_grid_source_is_padded_then_cropped_back_with_no_resampling_node(string configId, int step)
    {
        using JsonDocument document = BuildExact(configId, 500, 333, out (int Width, int Height) padded);
        Assert.Equal(ExactSourceCanvas.Padded(500, 333, step), padded);

        JsonProperty pad = Assert.Single(NodesOf(document, "ImagePadForOutpaint"));
        JsonElement padInputs = pad.Value.GetProperty("inputs");
        Assert.Equal(0, padInputs.GetProperty("left").GetInt32());
        Assert.Equal(0, padInputs.GetProperty("top").GetInt32());
        Assert.Equal(padded.Width - 500, padInputs.GetProperty("right").GetInt32());
        Assert.Equal(padded.Height - 333, padInputs.GetProperty("bottom").GetInt32());
        Assert.Equal(0, padInputs.GetProperty("feathering").GetInt32());
        string padSource = Assert.IsType<string>(padInputs.GetProperty("image")[0].GetString());
        Assert.Equal("LoadImage", document.RootElement.GetProperty(padSource).GetProperty("class_type").GetString());

        JsonProperty crop = Assert.Single(NodesOf(document, "ImageCrop"));
        JsonElement cropInputs = crop.Value.GetProperty("inputs");
        Assert.Equal(500, cropInputs.GetProperty("width").GetInt32());
        Assert.Equal(333, cropInputs.GetProperty("height").GetInt32());
        Assert.Equal(0, cropInputs.GetProperty("x").GetInt32());
        Assert.Equal(0, cropInputs.GetProperty("y").GetInt32());

        JsonProperty save = Assert.Single(NodesOf(document, "SaveImage"));
        Assert.Equal(crop.Name, save.Value.GetProperty("inputs").GetProperty("images")[0].GetString());

        Assert.DoesNotContain(document.RootElement.EnumerateObject(),
            n => ResamplingNodes.Contains(n.Value.GetProperty("class_type").GetString()));
    }

    [Theory]
    [InlineData("qwen-image-2-1-edit")]
    [InlineData("qwen-image-edit")]
    [InlineData("flux2-klein-4b-edit")]
    [InlineData("flux1-kontext")]
    [InlineData("vae-roundtrip-qwen")]
    public void On_grid_source_needs_no_pad_crop_or_resampling_node(string configId)
    {
        using JsonDocument document = BuildExact(configId, 1216, 832, out (int Width, int Height) padded);

        Assert.Equal((1216, 832), padded);
        Assert.Empty(NodesOf(document, "ImagePadForOutpaint"));
        Assert.Empty(NodesOf(document, "ImageCrop"));
        Assert.DoesNotContain(document.RootElement.EnumerateObject(),
            n => ResamplingNodes.Contains(n.Value.GetProperty("class_type").GetString()));
    }

    [Fact]
    public void Qwen_Image_21_tells_its_encoder_to_keep_the_source_size()
    {
        using JsonDocument document = BuildExact("qwen-image-2-1-edit", 500, 333, out _);

        JsonProperty encode = Assert.Single(NodesOf(document, "TextEncodeQwenImage21"));
        JsonElement inputs = encode.Value.GetProperty("inputs");
        Assert.Equal(0, inputs.GetProperty("resolution").GetInt32());
        JsonProperty pad = Assert.Single(NodesOf(document, "ImagePadForOutpaint"));
        Assert.Equal(pad.Name, inputs.GetProperty("images.image_1")[0].GetString());
    }

    [Fact]
    public void Default_requests_keep_their_normalized_graphs()
    {
        (WorkflowCatalog catalog, WorkflowRegistry registry) = Build();
        foreach (string configId in new[] { "qwen-image-2-1-edit", "qwen-image-edit", "flux2-klein-4b-edit", "flux1-kontext", "vae-roundtrip-qwen" })
        {
            WorkflowConfiguration config = Assert.IsType<WorkflowConfiguration>(catalog.FindConfig(configId));
            IWorkflow workflow = Assert.IsAssignableFrom<IWorkflow>(registry.Find(config.WorkflowName));
            WorkflowInputs inputs = new()
            {
                Positive = "make it red",
                SourceImageName = "source.png",
                SourceWidth = 500,
                SourceHeight = 333,
                TargetWidth = 1232,
                TargetHeight = 816,
            };
            ComfyWorkflowGraph graph = workflow.Build(Merge(catalog, workflow, config), catalog.Resolve(config), inputs);
            using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(graph));

            Assert.Empty(NodesOf(document, "ImagePadForOutpaint"));
            Assert.Empty(NodesOf(document, "ImageCrop"));
        }
    }

    [Fact]
    public void Workflows_without_an_exact_source_path_refuse_the_option_by_name()
    {
        (WorkflowCatalog catalog, WorkflowRegistry registry) = Build();
        int refused = 0;
        foreach (WorkflowConfiguration config in catalog.AllConfigs())
        {
            IWorkflow? workflow = registry.Find(config.WorkflowName);
            if (workflow is null || workflow.Kind == WorkflowKind.Generate || workflow.ExactSourceStep is not null)
            {
                continue;
            }

            refused++;
            RenderValidationException error = Assert.Throws<RenderValidationException>(() =>
                ExactSourceCanvas.Resolve(workflow, config.Id, 500, 333, hasSource: true, hasFixedAspect: false));
            Assert.Contains(config.Id, error.Message);
            Assert.Contains("exact_source_resolution", error.Message);
        }

        Assert.True(refused > 0, "Expected at least one edit workflow without an exact-source path.");
    }

    [Fact]
    public void Requests_that_cannot_keep_the_source_size_are_refused()
    {
        (WorkflowCatalog catalog, WorkflowRegistry registry) = Build();
        WorkflowConfiguration config = Assert.IsType<WorkflowConfiguration>(catalog.FindConfig("qwen-image-edit"));
        IWorkflow workflow = Assert.IsAssignableFrom<IWorkflow>(registry.Find(config.WorkflowName));

        _ = Assert.Throws<RenderValidationException>(() =>
            ExactSourceCanvas.Resolve(workflow, config.Id, 500, 333, hasSource: false, hasFixedAspect: false));
        _ = Assert.Throws<RenderValidationException>(() =>
            ExactSourceCanvas.Resolve(workflow, config.Id, 500, 333, hasSource: true, hasFixedAspect: true));

        foreach (string configId in new[] { "qwen-image-edit", "qwen-image-2-1-edit" })
        {
            WorkflowConfiguration withReferences = Assert.IsType<WorkflowConfiguration>(catalog.FindConfig(configId));
            IWorkflow referenceWorkflow = Assert.IsAssignableFrom<IWorkflow>(registry.Find(withReferences.WorkflowName));
            WorkflowInputs inputs = new()
            {
                Positive = "make it red",
                SourceImageName = "source.png",
                SourceWidth = 500,
                SourceHeight = 333,
                TargetWidth = 512,
                TargetHeight = 352,
                ExactSourceResolution = true,
                References = [new ReferenceInput("a.png", ReferenceKind.Image)],
            };
            RenderValidationException error = Assert.Throws<RenderValidationException>(() =>
                referenceWorkflow.Build(Merge(catalog, referenceWorkflow, withReferences), catalog.Resolve(withReferences), inputs));
            Assert.Contains("exact_source_resolution", error.Message);
        }
    }

    [Fact]
    public void The_request_option_reaches_the_submission_contract_and_defaults_off()
    {
        Dictionary<string, object?> bag = new(StringComparer.OrdinalIgnoreCase);
        Assert.False(ParamsCodec.Deserialize<SubmissionCommon>(bag).ExactSourceResolution);

        bag[WorkflowParamKeys.ExactSourceResolution] = JsonSerializer.SerializeToElement(true);
        Assert.True(ParamsCodec.Deserialize<SubmissionCommon>(bag).ExactSourceResolution);
        Assert.Equal("exact_source_resolution", WorkflowParamKeys.ExactSourceResolution);
    }

    [Fact]
    public void Every_separately_loaded_still_image_edit_vae_has_one_round_trip_configuration()
    {
        (WorkflowCatalog catalog, WorkflowRegistry registry) = Build();
        Dictionary<string, string> roundTripByVae = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> friendlyNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (WorkflowConfiguration config in catalog.AllConfigs().Where(c => c.WorkflowName == "vae-roundtrip"))
        {
            string vae = Assert.IsType<string>(config.Requirements.Vae);
            Assert.True(roundTripByVae.TryAdd(vae, config.Id), $"{config.Id} duplicates the round trip for VAE '{vae}'.");
            Assert.True(friendlyNames.Add(Assert.IsType<string>(config.FriendlyName)),
                $"{config.Id} shares a friendly name, so de-duplication would hide it.");
            Assert.NotNull(catalog.FindRequirement(vae));
        }

        Assert.Equal("vae-roundtrip-qwen", roundTripByVae["qwen-image-vae"]);
        Assert.Equal("vae-roundtrip-qwen-image-2-1", roundTripByVae["qwen-image-2-1-vae"]);

        foreach (WorkflowConfiguration config in catalog.AllConfigs())
        {
            IWorkflow? workflow = registry.Find(config.WorkflowName);
            if (workflow is null || workflow.Kind == WorkflowKind.Generate
                || workflow.Media != WorkflowMedia.Image || workflow.SourceMedia != WorkflowMedia.Image
                || config.Requirements.Vae is not { Length: > 0 } vae)
            {
                continue;
            }

            Assert.True(roundTripByVae.ContainsKey(vae),
                $"Edit configuration '{config.Id}' loads VAE '{vae}', which has no vae-roundtrip configuration.");
        }
    }
}
