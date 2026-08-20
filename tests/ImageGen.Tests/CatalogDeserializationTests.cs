using ImageGen.Application.Workflows;
using ImageGen.Comfy;
using ImageGen.Domain;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace ImageGen.Tests;

/// <summary>
/// Every catalog file deserializes into its typed DTO with no unknown keys and no wrong-typed values. This is the
/// guard the typed reader buys: a misspelled or unread key in a <c>workflows/*.json</c> or <c>models/*.json</c> is a
/// NAMED failure here (the DTOs are <see cref="System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow"/>),
/// not a value silently dropped at load — the way an unread <c>resolution</c> block or a <c>kind</c> mistyped into a
/// shared bucket would otherwise slip through. The catalog itself skips such a file with an Error log; this
/// turns that quiet, count-only loss into an explicit list of file + offending key.
/// </summary>
public sealed class CatalogDeserializationTests
{
    [Fact]
    public void Every_workflow_file_deserializes_with_no_unknown_keys()
        => AssertAllParse("workflows", CatalogJsonContext.Default.WorkflowFileDto);

    [Fact]
    public void Every_model_file_deserializes_with_no_unknown_keys()
        => AssertAllParse("models", CatalogJsonContext.Default.ModelFileDto);

    private static void AssertAllParse<T>(string sub, JsonTypeInfo<T> type)
    {
        string dir = Path.Combine(RepoRoot(), "configurations", sub);
        List<string> failures = [];
        foreach (string? f in Directory.EnumerateFiles(dir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            try
            {
                if (JsonSerializer.Deserialize(File.ReadAllText(f), type) is null)
                {
                    failures.Add($"{Path.GetFileName(f)}: empty or null document");
                }
            }
            catch (JsonException ex)
            {
                failures.Add($"{Path.GetFileName(f)}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count} catalog file(s) in {sub}/ did not deserialize cleanly:\n  " + string.Join("\n  ", failures));
    }

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "configurations", "models")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new DirectoryNotFoundException("configurations/ not found above the test bin dir.");
    }

    private static WorkflowFileDto? Workflow(string json) =>
        JsonSerializer.Deserialize(json, CatalogJsonContext.Default.WorkflowFileDto);

    [Fact]
    public void Workflow_short_name_is_catalog_metadata()
    {
        WorkflowFileDto? dto = Workflow("""{"id":"x","workflow":"W","friendly_name":"Long descriptive name","short_name":"Compact"}""");

        Assert.NotNull(dto);
        Assert.Equal("Compact", dto.ShortName);
    }

    [Fact]
    public void An_unknown_top_level_key_is_rejected()
        => Assert.Throws<JsonException>(() => Workflow("""{"id":"x","workflow":"W","typo_here":true}"""));

    [Fact]
    public void An_unknown_key_in_a_nested_block_is_rejected()
        => Assert.Throws<JsonException>(() =>
            Workflow("""{"id":"x","workflow":"W","card":{"speed":{"clazz":"fast"}}}"""));

    [Fact]
    public void A_reference_block_cannot_mix_scalar_and_typed_shapes()
    {
        ReferenceDto reference = new()
        {
            Max = 2,
            Types = [new ReferenceTypeDto { Kind = ReferenceKindNames.Image, Max = 1 }],
        };

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => WorkflowCatalog.ValidateReferenceShape("mixed-reference", reference));

        Assert.Contains("exactly one", ex.Message, StringComparison.Ordinal);
        Assert.Contains("mixed-reference", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reference_block_must_choose_a_concrete_shape()
        => Assert.Throws<InvalidOperationException>(
            () => WorkflowCatalog.ValidateReferenceShape("empty-reference", new ReferenceDto()));

    [Fact]
    public void An_unknown_key_in_a_param_envelope_is_rejected()
        => Assert.Throws<JsonException>(() =>
            Workflow("""{"id":"x","workflow":"W","params":{"steps":{"value":8,"visibilty":"exposed"}}}"""));

    [Fact]
    public void An_unknown_key_in_a_range_override_is_rejected()
        => Assert.Throws<JsonException>(() => Workflow("""
            {"id":"x","workflow":"W","params":{"steps":{"value":8,"visibility":"exposed","max":10,
              "range_override":{"max":20,"label":"Advanced","warning":"Careful","typo":true}}}}
            """));

    [Fact]
    public void A_range_override_requires_a_bound_label_and_warning()
        => Assert.Throws<JsonException>(() => Workflow("""
            {"id":"x","workflow":"W","params":{"steps":{"value":8,"visibility":"exposed","max":10,
              "range_override":{"max":20,"label":"Advanced"}}}}
            """));

    [Fact]
    public void The_retired_exposed_envelope_key_is_rejected_not_aliased()
        => Assert.Throws<JsonException>(() =>
            Workflow("""{"id":"x","workflow":"W","params":{"steps":{"value":8,"exposed":true}}}"""));

    [Fact]
    public void An_envelope_without_a_visibility_is_rejected()
        => Assert.Throws<JsonException>(() =>
            Workflow("""{"id":"x","workflow":"W","params":{"steps":{"value":8}}}"""));

    [Theory]
    [InlineData(""""revealed"""")]   // a plausible misspelling must throw, never coerce to a default
    [InlineData("true")]             // the old bool shape
    [InlineData("null")]
    public void An_unrecognized_visibility_value_is_rejected(string visibility)
        => Assert.Throws<JsonException>(() =>
            Workflow("""{"id":"x","workflow":"W","params":{"steps":{"value":8,"visibility":""" + visibility + "}}}"));

    [Fact]
    public void Param_forms_all_deserialize_to_the_expected_shape()
    {
        WorkflowFileDto? dto = Workflow("""
            {
              "id":"x","workflow":"W",
              "params":{
                "loader": "unet",
                "cfg": { "value": 7, "visibility": "exposed", "min": 1, "max": 30, "step": 0.5,
                  "range_override": { "max": 50, "label": "Allow advanced values", "warning": "Results may vary." } },
                "steps": { "value": 8, "visibility": "hidden" },
                "baked": { "value": 4, "visibility": "locked" },
                "aspect": { "square": [1024,1024] },
                "clip_type": null
              }
            }
            """);
        Assert.NotNull(dto);
        Dictionary<string, ConfigParamDto>? p = dto.Params;
        Assert.NotNull(p);

        // Bare scalar shorthand: the token is the value, and the form IS the state — a locked structural constant.
        ConfigParamDto loader = p["loader"];
        Assert.Equal("unet", loader.Value.GetString());
        Assert.Equal(ParamVisibility.Locked, loader.Visibility);
        Assert.Null(loader.Min);

        // Envelope form: value plus the mandatory visibility and the min/max/step siblings. All three states round-trip.
        ConfigParamDto cfg = p["cfg"];
        Assert.Equal(7, cfg.Value.GetInt32());
        Assert.Equal(ParamVisibility.Exposed, cfg.Visibility);
        Assert.Equal(1, cfg.Min);
        Assert.Equal(30, cfg.Max);
        Assert.Equal(0.5, cfg.Step);
        Assert.Equal(50, cfg.RangeOverride?.Max);
        Assert.Equal("Allow advanced values", cfg.RangeOverride?.Label);
        Assert.Equal("Results may vary.", cfg.RangeOverride?.Warning);
        Assert.Equal(ParamVisibility.Hidden, p["steps"].Visibility);
        Assert.Equal(ParamVisibility.Locked, p["baked"].Visibility);

        // Object WITHOUT "value" (the aspect map) is the value itself, captured whole — and, being the bare form,
        // locked.
        Assert.Equal(JsonValueKind.Object, p["aspect"].Value.ValueKind);
        Assert.Equal(ParamVisibility.Locked, p["aspect"].Visibility);

        // A bare null is a real value, not a dropped entry.
        Assert.Equal(JsonValueKind.Null, p["clip_type"].Value.ValueKind);
    }
}
