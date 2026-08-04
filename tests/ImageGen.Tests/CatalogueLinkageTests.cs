//TODO: CHECK FOR FALLBACKS
using System.Text.Json;
using ImageGen.Comfy;
using Microsoft.Extensions.DependencyInjection;

namespace ImageGen.Tests;

/// <summary>
/// Every slot has a consumer, and every consumer's slots exist.
///
/// <para>Both directions had failed. Nine model slots were required by no configuration, so the models page asked
/// you to bind files that could never affect a render — and three of them were downloaded overnight, ~28 GB, for
/// nothing. The other direction is worse: a configuration naming a slot that does not exist resolves to an empty
/// filename and is hidden by presence-gating, so it silently never appears.</para>
///
/// <para>A slot id can appear in TWO places, and an earlier version of this test only looked at one. Besides the
/// <c>requirements</c> block, any parameter whose <see cref="ParamSpec.IsModelRef"/> is set carries a slot id in
/// <c>params</c>, which <c>ComfyClient.MergeParams</c> resolves through <c>WorkflowCatalog.ResolveSlot</c>. Reading
/// only <c>requirements</c> made three genuinely-used slots look orphaned — <c>v3-sd15-mm</c>,
/// <c>mm-sdxl-v10-beta</c> and <c>chronoedit-distill-lora</c> — and deleting them broke the three configurations
/// that name them from <c>params</c>, in exactly the silent way described above.</para>
///
/// <para>Which parameters are model refs is taken from the workflow classes themselves, via the real DI
/// registration, so this cannot drift from what the resolver does. It is deliberately NOT inferred from the value:
/// <c>ideogram4</c>'s <c>clip_type</c> is the string "ideogram4", which collides with a slot id and is not one.</para>
/// </summary>
public sealed class CatalogueLinkageTests
{
    [Fact]
    public void No_slot_is_required_by_nothing()
    {
        var (slots, required) = Load();
        var orphans = slots.Keys.Where(id => !required.ContainsKey(id)).OrderBy(x => x).ToList();

        Assert.True(orphans.Count == 0,
            "These slots are required by no configuration, so binding them cannot affect anything and they are "
            + "only noise in the models page. Either the configuration that needs them was never written, or the "
            + "slot should go:\n  " + string.Join("\n  ", orphans));
    }

    [Fact]
    public void No_configuration_requires_a_slot_that_does_not_exist()
    {
        var (slots, required) = Load();
        var dangling = required
            .Where(kv => !slots.ContainsKey(kv.Key))
            .Select(kv => $"{kv.Key} <- {string.Join(", ", kv.Value.Order())}")
            .OrderBy(x => x)
            .ToList();

        Assert.True(dangling.Count == 0,
            "These configurations require a slot with no definition. It resolves to an empty filename and the "
            + "configuration is then hidden by presence-gating — it never appears, and nothing says why:\n  "
            + string.Join("\n  ", dangling));
    }

    /// <summary>
    /// Every configuration names a workflow that exists. Without this, a typo in <c>workflow</c> would make the
    /// model-ref sweep above silently skip that configuration's params — the same blind spot, one level up.
    /// </summary>
    [Fact]
    public void Every_configuration_names_a_workflow_that_exists()
    {
        var registry = Registry();
        var unknown = new List<string>();
        foreach (var (id, root) in Configurations())
        {
            var name = root.TryGetProperty("workflow", out var w) ? w.GetString() : null;
            if (registry.Find(name) is null) unknown.Add($"{id} -> {name ?? "(none)"}");
        }

        Assert.True(unknown.Count == 0,
            "These configurations name a workflow with no class registered in WorkflowRegistration.AddWorkflows, "
            + "so they cannot run at all:\n  " + string.Join("\n  ", unknown.Order()));
    }

    private static WorkflowRegistry Registry() =>
        new ServiceCollection().AddWorkflows().BuildServiceProvider().GetRequiredService<WorkflowRegistry>();

    private static IEnumerable<(string Id, JsonElement Root)> Configurations()
    {
        foreach (var f in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "configurations", "workflows"), "*.json"))
        {
            var doc = JsonDocument.Parse(File.ReadAllText(f));
            yield return (doc.RootElement.GetProperty("id").GetString()!, doc.RootElement.Clone());
        }
    }

    private static (Dictionary<string, string> Slots, Dictionary<string, List<string>> Required) Load()
    {
        var slots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "configurations", "models"), "*.json"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(f));
            slots[doc.RootElement.GetProperty("id").GetString()!] = f;
        }

        var registry = Registry();
        var required = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        void Note(string slot, string by)
        {
            if (!required.TryGetValue(slot, out var list)) required[slot] = list = [];
            list.Add(by);
        }

        foreach (var (id, root) in Configurations())
        {
            if (root.TryGetProperty("requirements", out var req))
            {
                foreach (var prop in req.EnumerateObject())
                {
                    var names = prop.Value.ValueKind switch
                    {
                        JsonValueKind.Array  => prop.Value.EnumerateArray().Select(e => e.GetString()!),
                        JsonValueKind.String => [prop.Value.GetString()!],
                        _                    => Array.Empty<string>(),
                    };
                    foreach (var n in names) Note(n, id);
                }
            }

            // The second place a slot id lives. The workflow's own schema says which keys those are.
            var wf = registry.Find(root.TryGetProperty("workflow", out var w) ? w.GetString() : null);
            if (wf is null || !root.TryGetProperty("params", out var ps)) continue;

            foreach (var spec in wf.Schema.Where(s => s.IsModelRef))
            {
                if (!ps.TryGetProperty(spec.Key, out var p)) continue;
                // A param is either the bare value or the {value, exposed, min, ...} envelope form.
                if (p.ValueKind == JsonValueKind.Object && !p.TryGetProperty("value", out p)) continue;
                if (p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString()))
                    Note(p.GetString()!, $"{id}.params.{spec.Key}");
            }
        }
        return (slots, required);
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "configurations", "models")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new DirectoryNotFoundException("repo root not found.");
    }
}
