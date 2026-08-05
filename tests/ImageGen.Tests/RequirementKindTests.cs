using ImageGen.Comfy;
using System.Reflection;
using System.Text.Json;


namespace ImageGen.Tests;

/// <summary>
/// A slot's kind must name exactly one loader's file list.
///
/// <para>The failure mode is a shape that READS as reasonable. A single <c>Other</c> value that unions seven loaders
/// — loras, IP-adapters, CLIP vision, latent upsamplers, both SeedVR2 loaders and HunyuanImage3 — offers every slot
/// among them every file of all of them, so a LoRA becomes a selectable answer for a ControlNet pack. Nothing in the
/// code looks wrong; the defect is only visible by listing what a picker actually offers. Each test below fails if a
/// bucket comes back by any route.</para>
/// </summary>
public sealed class RequirementKindTests
{
    /// <summary>The loader table, read off ComfyClient so the tests cannot drift from what ships.</summary>
    private static (RequirementKind Kind, string Node, string Input)[] LoaderInputs()
    {
        FieldInfo field = typeof(ComfyClient).GetField("LoaderInputs", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ComfyClient.LoaderInputs not found — was it renamed?");
        return ((RequirementKind, string, string)[])(field.GetValue(null) ?? throw new InvalidOperationException("static field value was null"));
    }

    /// <summary>
    /// Alternate inputs onto the SAME file list. A kind may name more than one loader only when those loaders read
    /// one folder — UNETLoader and UnetLoaderGGUF offer the same diffusion models, and SeedVR2's DiT and VAE
    /// loaders both read the pack's own folder. Anything not listed here is two different sets of files, which is
    /// the union that must never return.
    /// </summary>
    private static readonly HashSet<string> AllowedAlternates =
    [
        "UnetGguf:UNETLoader", "UnetGguf:UnetLoaderGGUF",
        "Unet:UNETLoader", "Unet:UnetLoaderGGUF",
        "TextEncoder:CLIPLoader", "TextEncoder:CLIPLoaderGGUF", "TextEncoder:DualCLIPLoader",
        "SeedVr2:SeedVR2LoadDiTModel", "SeedVR2LoadVAEModel", "SeedVr2:SeedVR2LoadVAEModel",
    ];

    [Fact]
    public void No_kind_draws_from_more_than_one_loader()
    {
        var offenders = LoaderInputs()
            .GroupBy(l => l.Kind)
            .Select(g => new { g.Key, Nodes = g.Select(x => x.Node).Distinct().ToList() })
            .Where(g => g.Nodes.Count > 1
                        && g.Nodes.Any(n => !AllowedAlternates.Contains($"{g.Key}:{n}")))
            .ToList();

        Assert.True(offenders.Count == 0,
            "These kinds union loaders that read different folders, so their slots are offered each other's files: "
            + string.Join("; ", offenders.Select(o => $"{o.Key} <- {string.Join(", ", o.Nodes)}"))
            + ". A kind must name one file list; add a RequirementKind value instead.");
    }

    [Fact]
    public void Every_kind_a_slot_declares_is_served_by_a_loader()
    {
        // CustomNode is met by a registered node rather than a file, so it draws from no loader by design.
        HashSet<RequirementKind> served = LoaderInputs().Select(l => l.Kind).Distinct().Append(RequirementKind.CustomNode).ToHashSet();
        List<RequirementKind> declared = SlotKinds().Values.Distinct().ToList();

        List<RequirementKind> unserved = declared.Where(k => !served.Contains(k)).ToList();
        Assert.True(unserved.Count == 0,
            "Slots declare kinds that no loader serves, so nothing can ever fill them: "
            + string.Join(", ", unserved));
    }

    [Fact]
    public void No_kind_is_a_catch_all()
    {
        // The literal defect: a value whose name admits it holds unrelated things.
        string[] names = Enum.GetNames<RequirementKind>();
        Assert.DoesNotContain("Other", names);
        Assert.DoesNotContain("Misc", names);
        Assert.DoesNotContain("Unknown", names);
    }

    [Fact]
    public void An_unrecognised_kind_fails_rather_than_joining_a_pool()
    {
        // A catch-all `_ => Other` would let a typo silently produce a slot that shares a pool with six unrelated
        // model types. It must throw instead.
        MethodInfo parse = typeof(WorkflowCatalog).GetMethod("ParseKind", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("WorkflowCatalog.ParseKind not found — was it renamed?");

        TargetInvocationException ex = Assert.Throws<TargetInvocationException>(() => parse.Invoke(null, ["not_a_real_kind"]));
        Assert.IsType<ArgumentException>(ex.InnerException);
    }

    [Fact]
    public void Sibling_slots_of_one_model_share_a_kind()
    {
        // Both seedvr2 files live in the pack's own folder and are read by its own loaders, so they must share one
        // kind. Declaring seedvr2-vae as `vae` instead would drag a SeedVR2-private file into the candidate list of
        // every unrelated VAE slot.
        Dictionary<string, RequirementKind> kinds = SlotKinds();
        foreach (string[]? family in new[] { new[] { "seedvr2-3b", "seedvr2-vae" } })
        {
            List<string> present = family.Where(kinds.ContainsKey).ToList();
            if (present.Count < 2) continue;
            List<RequirementKind> distinct = present.Select(id => kinds[id]).Distinct().ToList();
            Assert.True(distinct.Count == 1,
                $"{string.Join(" and ", present)} belong to one model but declare "
                + string.Join(" / ", distinct) + "; a slot's kind decides which pool it is offered.");
        }
    }

    /// <summary>Every shipped slot id to the kind it declares, read from the catalogue on disk.</summary>
    private static Dictionary<string, RequirementKind> SlotKinds()
    {
        string dir = CatalogDir();
        MethodInfo? parse = typeof(WorkflowCatalog).GetMethod("ParseKind", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(parse);
        Dictionary<string, RequirementKind> map = new Dictionary<string, RequirementKind>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(dir, "*.json"))
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
            JsonElement root = doc.RootElement;
            string id = root.GetProperty("id").RequireString();
            map[id] = (RequirementKind)(parse.Invoke(null, [root.GetProperty("kind").GetString()]) ?? throw new InvalidOperationException("ParseKind returned null"));
        }
        return map;
    }

    private static string CatalogDir()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            string candidate = Path.Combine(dir, "configurations", "models");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("configurations/models not found above the test binary.");
    }
}
