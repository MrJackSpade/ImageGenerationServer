using ImageGen.Comfy;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace ImageGen.Tests;

/// <summary>
/// Every slot has a consumer, and every consumer's slots exist.
///
/// <para>Both directions matter. A model slot required by no configuration makes the models page ask you to bind
/// — and download — a file that could never affect a render. The other direction is worse: a configuration naming
/// a slot that does not exist resolves to an empty filename and is hidden by presence-gating, so it silently never
/// appears.</para>
///
/// <para>A slot id can appear in TWO places, so both must be swept. Besides the <c>requirements</c> block, any
/// parameter whose <see cref="ParamSpec.IsModelRef"/> is set carries a slot id in <c>params</c>, which
/// <c>ComfyClient.MergeParams</c> resolves through <c>WorkflowCatalog.ResolveSlot</c>. Reading only
/// <c>requirements</c> would make a slot used only from <c>params</c> look orphaned — and deleting it would break
/// the configuration that names it there, in exactly the silent way described above.</para>
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
        (Dictionary<string, string>? slots, Dictionary<string, List<string>>? required) = Load();
        List<string> orphans = [.. slots.Keys.Where(id => !required.ContainsKey(id)).OrderBy(x => x)];

        Assert.True(orphans.Count == 0,
            "These slots are required by no configuration, so binding them cannot affect anything and they are "
            + "only noise in the models page. Either the configuration that needs them was never written, or the "
            + "slot should go:\n  " + string.Join("\n  ", orphans));
    }

    [Fact]
    public void No_configuration_requires_a_slot_that_does_not_exist()
    {
        (Dictionary<string, string>? slots, Dictionary<string, List<string>>? required) = Load();
        List<string> dangling = [.. required
            .Where(kv => !slots.ContainsKey(kv.Key))
            .Select(kv => $"{kv.Key} <- {string.Join(", ", kv.Value.Order())}")
            .OrderBy(x => x)];

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
        WorkflowRegistry registry = Registry();
        List<string> unknown = [];
        foreach ((string? id, JsonElement root) in Configurations())
        {
            string? name = root.TryGetProperty("workflow", out JsonElement w) ? w.GetString() : null;
            if (registry.Find(name) is null)
            {
                unknown.Add($"{id} -> {name ?? "(none)"}");
            }
        }

        Assert.True(unknown.Count == 0,
            "These configurations name a workflow with no class registered in WorkflowRegistration.AddWorkflows, "
            + "so they cannot run at all:\n  " + string.Join("\n  ", unknown.Order()));
    }

    private static WorkflowRegistry Registry() =>
        new ServiceCollection().AddWorkflows().BuildServiceProvider().GetRequiredService<WorkflowRegistry>();

    private static IEnumerable<(string Id, JsonElement Root)> Configurations()
    {
        foreach (string f in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "configurations", "workflows"), "*.json"))
        {
            JsonDocument doc = JsonDocument.Parse(File.ReadAllText(f));
            yield return (doc.RootElement.GetProperty("id").RequireString(), doc.RootElement.Clone());
        }
    }

    private static (Dictionary<string, string> Slots, Dictionary<string, List<string>> Required) Load()
    {
        Dictionary<string, string> slots = new(StringComparer.OrdinalIgnoreCase);
        foreach (string f in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "configurations", "models"), "*.json"))
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(f));
            slots[doc.RootElement.GetProperty("id").RequireString()] = f;
        }

        WorkflowRegistry registry = Registry();
        Dictionary<string, List<string>> required = new(StringComparer.OrdinalIgnoreCase);
        void Note(string slot, string by)
        {
            if (!required.TryGetValue(slot, out List<string>? list))
            {
                required[slot] = list = [];
            }

            list.Add(by);
        }

        foreach ((string? id, JsonElement root) in Configurations())
        {
            if (root.TryGetProperty("requirements", out JsonElement req))
            {
                foreach (JsonProperty prop in req.EnumerateObject())
                {
                    IEnumerable<string> names = prop.Value.ValueKind switch
                    {
                        JsonValueKind.Array => prop.Value.EnumerateArray().Select(e => e.RequireString()),
                        JsonValueKind.String => [prop.Value.RequireString()],
                        _ => [],
                    };
                    foreach (string n in names)
                    {
                        Note(n, id);
                    }
                }
            }

            // The second place a slot id lives. The workflow's own schema says which keys those are.
            IWorkflow? wf = registry.Find(root.TryGetProperty("workflow", out JsonElement w) ? w.GetString() : null);
            if (wf is null || !root.TryGetProperty("params", out JsonElement ps))
            {
                continue;
            }

            foreach (ParamSpec? spec in wf.Schema.Where(s => s.IsModelRef))
            {
                if (!ps.TryGetProperty(spec.Key, out JsonElement p))
                {
                    continue;
                }
                // A param is either the bare value or the {value, exposed, min, ...} envelope form.
                if (p.ValueKind == JsonValueKind.Object && !p.TryGetProperty("value", out p))
                {
                    continue;
                }

                if (p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString()))
                {
                    Note(p.RequireString(), $"{id}.params.{spec.Key}");
                }
            }
        }

        return (slots, required);
    }

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "configurations", "models")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new DirectoryNotFoundException("repo root not found.");
    }
}