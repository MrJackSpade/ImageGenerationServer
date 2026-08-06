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
    public void An_unknown_top_level_key_is_rejected()
        => Assert.Throws<JsonException>(() => Workflow("""{"id":"x","workflow":"W","typo_here":true}"""));

    [Fact]
    public void An_unknown_key_in_a_nested_block_is_rejected()
        => Assert.Throws<JsonException>(() =>
            Workflow("""{"id":"x","workflow":"W","card":{"speed":{"clazz":"fast"}}}"""));

    [Fact]
    public void An_unknown_key_in_a_param_envelope_is_rejected()
        => Assert.Throws<JsonException>(() =>
            Workflow("""{"id":"x","workflow":"W","params":{"steps":{"value":8,"expsoed":true}}}"""));

    [Fact]
    public void Param_forms_all_deserialize_to_the_expected_shape()
    {
        WorkflowFileDto? dto = Workflow("""
            {
              "id":"x","workflow":"W",
              "params":{
                "steps": 8,
                "cfg": { "value": 7, "exposed": true, "min": 1, "max": 30, "step": 0.5 },
                "baked": { "value": 4, "exposed": false },
                "aspect": { "square": [1024,1024] },
                "clip_type": null
              }
            }
            """);
        Assert.NotNull(dto);
        Dictionary<string, ConfigParamDto>? p = dto.Params;
        Assert.NotNull(p);

        // Bare scalar shorthand: the token is the value, nothing exposed or bounded.
        ConfigParamDto steps = p["steps"];
        Assert.Equal(8, steps.Value.GetInt32());
        Assert.False(steps.Exposed);
        Assert.False(steps.Locked);
        Assert.Null(steps.Min);

        // Envelope form: value plus the exposed/min/max/step siblings.
        ConfigParamDto cfg = p["cfg"];
        Assert.Equal(7, cfg.Value.GetInt32());
        Assert.True(cfg.Exposed);
        Assert.False(cfg.Locked);
        Assert.Equal(1, cfg.Min);
        Assert.Equal(30, cfg.Max);
        Assert.Equal(0.5, cfg.Step);

        // Explicit "exposed": false is a baked, locked knob.
        ConfigParamDto baked = p["baked"];
        Assert.False(baked.Exposed);
        Assert.True(baked.Locked);

        // Object WITHOUT "value" (the aspect map) is the value itself, captured whole.
        Assert.Equal(JsonValueKind.Object, p["aspect"].Value.ValueKind);

        // A bare null is a real value, not a dropped entry.
        Assert.Equal(JsonValueKind.Null, p["clip_type"].Value.ValueKind);
    }
}
