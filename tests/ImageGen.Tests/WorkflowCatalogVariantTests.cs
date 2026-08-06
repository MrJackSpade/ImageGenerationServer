using ImageGen.Comfy;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace ImageGen.Tests;

/// <summary>
/// DB-backed variants folded into the in-memory catalogue: a variant is a first-class configuration that inherits its
/// base's structure and carries its own snapshotted parameter values, resolvable everywhere a shipped config is.
/// </summary>
public sealed class WorkflowCatalogVariantTests : IDisposable
{
    private readonly string _root;

    public WorkflowCatalogVariantTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "imggen-cat-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(Path.Combine(_root, "workflows"));
        _ = Directory.CreateDirectory(Path.Combine(_root, "models"));
        File.WriteAllText(
            Path.Combine(_root, "workflows", "base.json"),
            """{ "id": "base", "workflow": "Test", "friendly_name": "Base", "params": { "steps": 8, "cfg": 3.5 } }""");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is harmless; cleanup is best-effort.
        }
    }

    private WorkflowCatalog NewCatalog() => new(new ComfyOptions { CatalogPath = _root }, NullLogger<WorkflowCatalog>.Instance);

    private static VariantSpec Spec(string id, string baseId, string name, string paramsJson) =>
        new(id, baseId, name, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(paramsJson) ?? []);

    [Fact]
    public void A_variant_is_folded_in_as_a_first_class_config_with_its_snapshot_values()
    {
        WorkflowCatalog cat = NewCatalog();
        cat.SetVariants([Spec("base-2", "base", "Base hi-res", """{"steps":40}""")]);

        WorkflowConfiguration v = cat.FindConfig("base-2") ?? throw new Xunit.Sdk.XunitException("variant 'base-2' did not resolve");
        Assert.Equal("base-2", v.Id);
        Assert.Equal("Test", v.WorkflowName);            // inherits the base's workflow class
        Assert.Equal("Base hi-res", v.FriendlyName);     // its own name
        Assert.Equal(40L, Convert.ToInt64(v.Params["steps"].Value));   // snapshot value replaced the base's 8
        Assert.Equal(3.5d, Convert.ToDouble(v.Params["cfg"].Value));   // an un-snapshotted param keeps the base's value
        Assert.True(cat.IsVariant("base-2"));
        Assert.False(cat.IsVariant("base"));
        Assert.Contains(cat.AllConfigs(), c => c.Id == "base-2");
    }

    [Fact]
    public void HasConfigId_is_exact_unlike_the_loose_FindConfig()
    {
        WorkflowCatalog cat = NewCatalog();
        cat.SetVariants([Spec("base-2", "base", "V", "{}")]);

        Assert.True(cat.HasConfigId("base"));
        Assert.True(cat.HasConfigId("base-2"));
        Assert.False(cat.HasConfigId("base-3"));   // FindConfig("base-3") would loosely match the base; HasConfigId must not
    }

    [Fact]
    public void A_variant_naming_an_unknown_base_is_skipped()
    {
        WorkflowCatalog cat = NewCatalog();
        cat.SetVariants([Spec("ghost-2", "ghost", "Ghost", "{}")]);

        Assert.Null(cat.FindConfig("ghost-2"));
        Assert.False(cat.IsVariant("ghost-2"));
    }

    [Fact]
    public void Setting_variants_again_replaces_the_previous_set()
    {
        WorkflowCatalog cat = NewCatalog();
        cat.SetVariants([Spec("base-2", "base", "First", "{}")]);
        cat.SetVariants([Spec("base-3", "base", "Second", "{}")]);

        Assert.False(cat.IsVariant("base-2"));
        Assert.True(cat.IsVariant("base-3"));
        Assert.False(cat.HasConfigId("base-2"));   // gone from the effective catalogue (FindConfig would loose-match the base)
    }
}
