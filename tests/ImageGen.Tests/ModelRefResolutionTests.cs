using ImageGen.Application.Rendering;
using ImageGen.Comfy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImageGen.Tests;

/// <summary>
/// A model reference that does not resolve must FAIL, naming the slot — never quietly become "".
///
/// <para>Yielding "" and carrying on would let each consumer substitute a hardcoded filename of its own
/// (<c>p.Str("motion_model") ?? "v3_sd15_mm.ckpt"</c>, six times over), so a configuration whose slot was deleted
/// from the catalogue would still render on the one machine that happens to have that file on disk and report
/// success — the catalogue wrong for an entire run with nothing anywhere disagreeing.</para>
///
/// <para>These tests exercise <see cref="WorkflowCatalog.ResolveModelRefs"/> — the same method the renderer calls,
/// not a copy of its rules.</para>
/// </summary>
public sealed class ModelRefResolutionTests
{
    [Fact]
    public void An_unresolvable_model_ref_fails_and_names_the_slot()
    {
        (WorkflowCatalog? catalog, IWorkflow? wf) = Build(bindEverything: true);
        Dictionary<string, object?> v = Bag(("motion_model", "a-slot-that-does-not-exist"));

        RenderValidationException ex = Assert.Throws<RenderValidationException>(() => catalog.ResolveModelRefs(wf, "animatediff-sd15", v));
        Assert.Contains("a-slot-that-does-not-exist", ex.Message);
        Assert.Contains("motion_model", ex.Message);
        Assert.Contains("animatediff-sd15", ex.Message);
    }

    [Fact]
    public void A_slot_that_exists_but_is_unbound_fails_too()
    {
        // The distinction that matters: the catalogue knows the slot, this machine simply has no file for it.
        // Silently rendering here would make a missing binding indistinguishable from a working one.
        (WorkflowCatalog? catalog, IWorkflow? wf) = Build(bindEverything: false);
        Dictionary<string, object?> v = Bag(("motion_model", "v3-sd15-mm"));

        RenderValidationException ex = Assert.Throws<RenderValidationException>(() => catalog.ResolveModelRefs(wf, "animatediff-sd15", v));
        Assert.Contains("v3-sd15-mm", ex.Message);
    }

    [Fact]
    public void A_bound_slot_resolves_to_its_file()
    {
        (WorkflowCatalog? catalog, IWorkflow? wf) = Build(bindEverything: true);
        Dictionary<string, object?> v = Bag(("motion_model", "v3-sd15-mm"));

        catalog.ResolveModelRefs(wf, "animatediff-sd15", v);
        Assert.Equal("v3-sd15-mm.safetensors", v["motion_model"]);
    }

    [Fact]
    public void An_absent_model_ref_is_left_alone()
    {
        // An optional LoRA nobody set is ABSENT, not unbound — a configuration without one must still build.
        (WorkflowCatalog? catalog, IWorkflow? wf) = Build(bindEverything: true);
        Dictionary<string, object?> v = Bag(("motion_model", "v3-sd15-mm"));   // no "lora" key at all

        catalog.ResolveModelRefs(wf, "animatediff-sd15", v);
        Assert.False(v.ContainsKey("lora"));
    }

    [Fact]
    public void No_workflow_substitutes_a_model_filename_of_its_own()
    {
        // A literal model filename in a graph builder would reintroduce exactly the bug above, on exactly one
        // machine, so the shape itself is banned rather than the instances.
        List<string> offenders = [];
        string dir = Path.Combine(RepoRoot(), "src", "ImageGen.Comfy", "Workflows");
        foreach (string f in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            string[] lines = File.ReadAllLines(f);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string code = line.TrimStart();
                if (code.StartsWith("//") || code.StartsWith("///"))
                {
                    continue;   // prose may name a file
                }

                if (!line.Contains(".safetensors\"") && !line.Contains(".ckpt\"") && !line.Contains(".pth\""))
                {
                    continue;
                }

                offenders.Add($"{Path.GetFileName(f)}:{i + 1}  {code.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "A model filename is written into a workflow class. That is one machine's disk baked into the "
            + "application: it works for whoever named their copy that way and silently substitutes itself for a "
            + "slot nobody bound. Declare a slot and read it through an IsModelRef parameter instead.\n  "
            + string.Join("\n  ", offenders));
    }


    [Fact]
    public void Eligibility_counts_the_slots_a_configuration_asks_for_through_params()
    {
        // The gating half of the same bug: checking requirements but not params model refs would offer a
        // configuration with an unbound one and then fail at submit. wan22-i2v-a14b names its second MoE expert
        // nowhere but params, so it is the case that proves the rule.
        (WorkflowCatalog? catalog, IWorkflow _) = Build(bindEverything: true);
        WorkflowRegistry registry = new ServiceCollection().AddWorkflows().BuildServiceProvider().GetRequiredService<WorkflowRegistry>();
        WorkflowConfiguration? cfg = catalog.FindConfig("wan22-i2v-a14b");
        Assert.NotNull(cfg);
        IWorkflow? wf = registry.Find(cfg.WorkflowName);
        Assert.NotNull(wf);

        List<string> asked = catalog.ModelRefSlots(wf, cfg).ToList();
        Assert.Contains("wan2-2-i2v-low-noise-14b", asked);
        Assert.DoesNotContain(asked, s => s == "lora");   // unset optional params ask for nothing
    }

    [Fact]
    public void A_param_the_configuration_does_not_set_asks_for_nothing()
    {
        // `anima` sets no model-ref param at all — its models are all in `requirements`. It must therefore ask for
        // nothing here, or gating on this would hide every configuration that simply has no optional LoRA.
        (WorkflowCatalog? catalog, IWorkflow _) = Build(bindEverything: true);
        WorkflowRegistry registry = new ServiceCollection().AddWorkflows().BuildServiceProvider().GetRequiredService<WorkflowRegistry>();
        WorkflowConfiguration? cfg = catalog.FindConfig("anima");
        Assert.NotNull(cfg);
        IWorkflow? wf = registry.Find(cfg.WorkflowName);
        Assert.NotNull(wf);

        Assert.DoesNotContain(catalog.ModelRefSlots(wf, cfg), _ => true);
    }

    [Fact]
    public void Every_slot_a_configuration_asks_for_through_params_exists_in_the_catalogue()
    {
        // The catalogue-wide version: an id here that has no slot file resolves to "" at submit and FAILS the
        // render, rather than silently producing a graph with an empty filename.
        (WorkflowCatalog? catalog, IWorkflow _) = Build(bindEverything: true);
        WorkflowRegistry registry = new ServiceCollection().AddWorkflows().BuildServiceProvider().GetRequiredService<WorkflowRegistry>();
        List<string> dangling = [];
        foreach (WorkflowConfiguration cfg in catalog.AllConfigs())
        {
            IWorkflow? wf = registry.Find(cfg.WorkflowName);
            if (wf is null)
            {
                continue;
            }

            foreach (string slot in catalog.ModelRefSlots(wf, cfg))
            {
                if (catalog.FindRequirement(slot) is null)
                {
                    dangling.Add($"{cfg.Id} -> {slot}");
                }
            }
        }

        Assert.True(dangling.Count == 0,
            "These configurations name a slot from params that the catalogue does not define, so the render fails:\n  "
            + string.Join("\n  ", dangling.Order()));
    }

    private static Dictionary<string, object?> Bag(params (string Key, object? Value)[] pairs)
    {
        Dictionary<string, object?> v = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string? k, object? val) in pairs)
        {
            v[k] = val;
        }

        return v;
    }

    private static (WorkflowCatalog Catalog, IWorkflow Workflow) Build(bool bindEverything)
    {
        WorkflowCatalog catalog = new(
            new ComfyOptions { CatalogPath = Path.Combine(RepoRoot(), "configurations") },
            NullLogger<WorkflowCatalog>.Instance);
        catalog.SetBindings(bindEverything
            ? catalog.AllRequirements().ToDictionary(r => r.Id, r => r.Id + ".safetensors")
            : []);

        WorkflowRegistry registry = new ServiceCollection().AddWorkflows().BuildServiceProvider().GetRequiredService<WorkflowRegistry>();
        IWorkflow? wf = registry.Find("animatediff-sd15");
        Assert.NotNull(wf);
        return (catalog, wf);
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
