using System.Text.Json;
using System.Text.RegularExpressions;

namespace ImageGen.Tests;

/// <summary>
/// A workflow that emits a custom-node pack's node must declare that pack as a requirement.
///
/// <para>Presence-gating is what stops a workflow being offered when its pack is missing. It only works if the
/// configuration says which pack it needs — a workflow that emits a pack's nodes without declaring the requirement
/// reads READY and then fails at render with a bare <c>value_not_in_list</c> on the node type. Declaring them by
/// hand does not stay true; a new workflow that emits <c>ADE_*</c> and forgets the requirement is invisible until
/// someone uninstalls the pack.</para>
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
        (@"""Ideogram4CorrectionPatch""",     "comfyui-ideogram4-debanner"),
        (@"""H3AnimatedPreview""",            "comfyui-h3-preview"),
    ];

    [Fact]
    public void Every_workflow_emitting_a_pack_node_declares_that_pack()
    {
        string root = RepoRoot();
        Dictionary<string, HashSet<string>> needsByWorkflow = EmittedPacksByWorkflowName(Path.Combine(root, "src", "ImageGen.Comfy", "Workflows"));

        List<string> missing = [];
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "configurations", "workflows"), "*.json"))
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
            JsonElement cfg = doc.RootElement;
            string workflow = cfg.GetProperty("workflow").RequireString();
            if (!needsByWorkflow.TryGetValue(workflow, out HashSet<string>? needed))
            {
                continue;
            }

            HashSet<string> declared = DeclaredRequirements(cfg);
            foreach (string? slot in needed.Where(n => !declared.Contains(n)))
            {
                missing.Add($"{cfg.GetProperty("id").GetString()} (workflow {workflow}) does not declare {slot}");
            }
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
        Regex classRe = new(@"public\s+(?:sealed|abstract)\s+class\s+(\w+)(?:\s*:\s*(\w+))?");
        Regex nameRe = new(@"public\s+override\s+string\s+Name\s*=>\s*""([^""]+)""");

        Dictionary<string, HashSet<string>> packsByClass = [];
        Dictionary<string, string> baseOfClass = [];
        Dictionary<string, string> nameOfClass = [];

        foreach (string file in Directory.EnumerateFiles(workflowsDir, "*.cs", SearchOption.AllDirectories))
        {
            string src = File.ReadAllText(file);
            List<Match> starts = [.. classRe.Matches(src).Select(m => m)];
            for (int i = 0; i < starts.Count; i++)
            {
                int from = starts[i].Index;
                int to = i + 1 < starts.Count ? starts[i + 1].Index : src.Length;
                string body = src[from..to];
                string cls = starts[i].Groups[1].Value;

                if (starts[i].Groups[2].Success)
                {
                    baseOfClass[cls] = starts[i].Groups[2].Value;
                }

                Match nm = nameRe.Match(body);
                if (nm.Success)
                {
                    nameOfClass[cls] = nm.Groups[1].Value;
                }

                HashSet<string> packs = [.. PackNodes.Where(p => Regex.IsMatch(body, p.Pattern)).Select(p => p.Slot)];
                if (packs.Count > 0)
                {
                    packsByClass[cls] = packs;
                }
            }
        }

        // Roll base-class emissions down to the concrete workflows.
        Dictionary<string, HashSet<string>> result = [];
        foreach ((string? cls, string? name) in nameOfClass)
        {
            HashSet<string> packs = [];
            for (string? c = cls; c is not null; c = baseOfClass.TryGetValue(c, out string? b) ? b : null)
            {
                if (packsByClass.TryGetValue(c, out HashSet<string>? p))
                {
                    packs.UnionWith(p);
                }
            }

            if (packs.Count > 0)
            {
                result[name] = packs;
            }
        }

        return result;
    }

    private static HashSet<string> DeclaredRequirements(JsonElement cfg)
    {
        HashSet<string> set = new(StringComparer.OrdinalIgnoreCase);
        if (!cfg.TryGetProperty("requirements", out JsonElement req))
        {
            return set;
        }

        foreach (JsonProperty prop in req.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                _ = set.Add(prop.Value.RequireString());
            }
            else if (prop.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement e in prop.Value.EnumerateArray())
                {
                    _ = set.Add(e.RequireString());
                }
            }
        }

        return set;
    }

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, "configurations", "workflows")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException("repo root not found above the test binary.");
    }
}
