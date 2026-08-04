using System.Text.Json;
using System.Text.RegularExpressions;

namespace ImageGen.Tests;

/// <summary>
/// A workflow that emits a custom-node pack's node must declare that pack as a requirement.
///
/// <para>Presence-gating is what stops a workflow being offered when its pack is missing. It only works if the
/// configuration says which pack it needs — and six of the seven node slots were required by nothing at all, so
/// removing any of those packs left the workflow reading READY and failing at render with a bare
/// <c>value_not_in_list</c> on the node type. Declaring them by hand does not stay true; a new workflow that
/// emits <c>ADE_*</c> and forgets the requirement is invisible until someone uninstalls the pack.</para>
/// </summary>
public sealed class NodeRequirementTests
{
    /// <summary>Node-name prefix to the catalogue slot standing for the pack that provides it.</summary>
    private static readonly (string Pattern, string Slot)[] PackNodes =
    [
        (@"""ACN_\w*""",                     "comfyui-advanced-controlnet"),
        (@"""ADE_\w*""",                     "comfyui-animatediff-evolved"),
        (@"""\w*Rebalance\w*""",             "comfyui-conditioning-rebalance"),
        (@"""IPAdapter\w*""",                "comfyui-ipadapter-plus"),
        (@"""SeedVR2Load\w*""",              "comfyui-seedvr2-node"),
        (@"""AnimaLLLite\w*""",              "comfyui-anima-lllite"),
    ];

    [Fact]
    public void Every_workflow_emitting_a_pack_node_declares_that_pack()
    {
        var root = RepoRoot();
        var needsByWorkflow = EmittedPacksByWorkflowName(Path.Combine(root, "src", "ImageGen.Comfy", "Workflows"));

        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "configurations", "workflows"), "*.json"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var cfg = doc.RootElement;
            var workflow = cfg.GetProperty("workflow").RequireString();
            if (!needsByWorkflow.TryGetValue(workflow, out var needed)) continue;

            var declared = DeclaredRequirements(cfg);
            foreach (var slot in needed.Where(n => !declared.Contains(n)))
                missing.Add($"{cfg.GetProperty("id").GetString()} (workflow {workflow}) does not declare {slot}");
        }

        Assert.True(missing.Count == 0,
            "These configurations emit a custom-node pack's nodes without declaring the pack, so presence-gating "
            + "cannot hide them when it is missing and they fail at render instead:\n  "
            + string.Join("\n  ", missing));
    }

    /// <summary>
    /// Workflow NAME to the packs its class emits. Attribution walks class bodies so a node emitted by an abstract
    /// base counts for every concrete workflow deriving from it — which is how animatediff-lightning-i2v and
    /// animatelcm-i2v inherit their IPAdapter and AnimateDiff requirements.
    /// </summary>
    private static Dictionary<string, HashSet<string>> EmittedPacksByWorkflowName(string workflowsDir)
    {
        var classRe = new Regex(@"public\s+(?:sealed|abstract)\s+class\s+(\w+)(?:\s*:\s*(\w+))?");
        var nameRe = new Regex(@"public\s+override\s+string\s+Name\s*=>\s*""([^""]+)""");

        var packsByClass = new Dictionary<string, HashSet<string>>();
        var baseOfClass = new Dictionary<string, string>();
        var nameOfClass = new Dictionary<string, string>();

        foreach (var file in Directory.EnumerateFiles(workflowsDir, "*.cs", SearchOption.AllDirectories))
        {
            var src = File.ReadAllText(file);
            var starts = classRe.Matches(src).Select(m => m).ToList();
            for (int i = 0; i < starts.Count; i++)
            {
                var from = starts[i].Index;
                var to = i + 1 < starts.Count ? starts[i + 1].Index : src.Length;
                var body = src[from..to];
                var cls = starts[i].Groups[1].Value;

                if (starts[i].Groups[2].Success) baseOfClass[cls] = starts[i].Groups[2].Value;
                var nm = nameRe.Match(body);
                if (nm.Success) nameOfClass[cls] = nm.Groups[1].Value;

                var packs = PackNodes.Where(p => Regex.IsMatch(body, p.Pattern)).Select(p => p.Slot).ToHashSet();
                if (packs.Count > 0) packsByClass[cls] = packs;
            }
        }

        // Roll base-class emissions down to the concrete workflows.
        var result = new Dictionary<string, HashSet<string>>();
        foreach (var (cls, name) in nameOfClass)
        {
            var packs = new HashSet<string>();
            for (var c = cls; c is not null; c = baseOfClass.TryGetValue(c, out var b) ? b : null)
                if (packsByClass.TryGetValue(c, out var p)) packs.UnionWith(p);
            if (packs.Count > 0) result[name] = packs;
        }
        return result;
    }

    private static HashSet<string> DeclaredRequirements(JsonElement cfg)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!cfg.TryGetProperty("requirements", out var req)) return set;
        foreach (var prop in req.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String) set.Add(prop.Value.RequireString());
            else if (prop.Value.ValueKind == JsonValueKind.Array)
                foreach (var e in prop.Value.EnumerateArray()) set.Add(e.RequireString());
        }
        return set;
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, "configurations", "workflows"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("repo root not found above the test binary.");
    }
}
