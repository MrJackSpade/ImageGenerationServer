using System.Text.Json;

namespace ImageGen.Tests;

/// <summary>
/// Presence-gating for a custom-node pack that loads nothing.
///
/// <para>Most packs gate themselves: their loaders are where their filenames come from, so an uninstalled pack
/// takes its files with it and every configuration behind it disappears. <c>AnimaLLLiteApply</c> patches a model
/// it is handed and loads nothing, so it contributes no filenames — and <c>anima-outpaint</c>, which gates on the
/// LLLite <i>weight</i>, would read as perfectly ready on a box without the pack and then fail at submit on an
/// unregistered node. A requirement that names a node is how that becomes checkable.</para>
/// </summary>
public sealed class CustomNodePresenceTests
{
    /// <summary>
    /// Every catalogue requirement that names a node, and the workflow requirement wiring that depends on it.
    /// Reads the shipped configuration rather than a fixture: the thing that can break is the JSON.
    /// </summary>
    [SkippableFact]
    public void Anima_outpaint_requires_the_pack_that_provides_its_node()
    {
        string? repo = RepositoryRoot();
        Skip.If(repo is null, "not running from a source checkout");
        Assert.NotNull(repo);

        string models = Path.Combine(repo, "configurations", "models");
        JsonElement slot = JsonDocument.Parse(File.ReadAllText(Path.Combine(models, "comfyui-anima-lllite.json"))).RootElement;

        Assert.Equal("custom_node", slot.GetProperty("kind").GetString());
        Assert.Equal("AnimaLLLiteApply", slot.GetProperty("node").GetString());

        // The workflow has to actually ask for it, or the requirement exists and gates nothing.
        JsonElement config = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(repo, "configurations", "workflows", "anima-outpaint.json"))).RootElement;
        List<string?> extra = config.GetProperty("requirements").GetProperty("extra").EnumerateArray()
            .Select(e => e.GetString()).ToList();
        Assert.Contains("comfyui-anima-lllite", extra);
    }

    /// <summary>
    /// A custom_node requirement is only checkable if it names a node — one that says nothing can never be
    /// unsatisfied, which is how a pack goes missing without anything noticing.
    /// </summary>
    [SkippableFact]
    public void Every_custom_node_requirement_names_the_node_that_proves_it()
    {
        string? repo = RepositoryRoot();
        Skip.If(repo is null, "not running from a source checkout");
        Assert.NotNull(repo);

        List<string> missing = new List<string>();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(repo, "configurations", "models"), "*.json"))
        {
            JsonElement root = JsonDocument.Parse(File.ReadAllText(file)).RootElement;
            if (root.TryGetProperty("kind", out JsonElement kind) && kind.GetString() == "custom_node"
                && !(root.TryGetProperty("node", out JsonElement node) && !string.IsNullOrWhiteSpace(node.GetString())))
                missing.Add(Path.GetFileName(file));
        }

        Assert.True(missing.Count == 0, $"custom_node requirements with no \"node\": {string.Join(", ", missing)}");
    }

    /// <summary>
    /// Against a LIVE ComfyUI. This pins the behaviour the whole mechanism rests on: ComfyUI answers
    /// <c>200</c> with an EMPTY OBJECT for a node it does not have, so checking the status code would report
    /// every node on earth as present. The body is the test, and if ComfyUI ever changes that, this is what
    /// catches it rather than every gated workflow silently becoming available.
    /// </summary>
    [SkippableFact]
    public async Task Live_object_info_distinguishes_a_known_node_from_an_unknown_one()
    {
        string? baseUrl = Environment.GetEnvironmentVariable("COMFY_URL");
        Skip.If(string.IsNullOrWhiteSpace(baseUrl), "set COMFY_URL to run this against a live ComfyUI");
        Assert.NotNull(baseUrl);

        using HttpClient http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };

        using HttpResponseMessage known = await http.GetAsync("object_info/AnimaLLLiteApply");
        known.EnsureSuccessStatusCode();
        Assert.True(JsonDocument.Parse(await known.Content.ReadAsStringAsync())
            .RootElement.TryGetProperty("AnimaLLLiteApply", out _), "the installed node was not reported");

        using HttpResponseMessage unknown = await http.GetAsync("object_info/NoSuchNodeXYZ");
        Assert.True(unknown.IsSuccessStatusCode,
            "ComfyUI used to answer 200 for an unknown node; if this is now an error status the presence check " +
            "still works, but the comment explaining why the body is checked is stale.");
        Assert.False(JsonDocument.Parse(await unknown.Content.ReadAsStringAsync())
            .RootElement.TryGetProperty("NoSuchNodeXYZ", out _), "an unknown node was reported as present");
    }

    private static string? RepositoryRoot()
    {
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "configurations", "models"))) return directory.FullName;
            directory = directory.Parent;
        }
        return null;
    }
}
