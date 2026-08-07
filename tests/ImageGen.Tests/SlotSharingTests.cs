using ImageGen.Application.Workflows;
using ImageGen.Comfy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImageGen.Tests;

/// <summary>
/// A model binding is global per <c>(machine, slot)</c>, so changing it from one workflow's detail page changes it for
/// every workflow that shares the slot. <see cref="SlotSharing.Others"/> is what the picker uses to warn about that
/// fan-out (issue #195): the OTHER workflows that require a slot, current one excluded. These lock its semantics and
/// prove the real catalogue actually contains both shared and single-use slots for it to matter.
/// </summary>
public sealed class SlotSharingTests
{
    private static WorkflowStatus Wf(string id, string name, params string[] slots) =>
        new(id, name, Ready: true, MissingSlots: [], RequiredSlots: slots, Kind: "generate");

    [Fact]
    public void A_shared_slot_names_the_other_workflows_excluding_the_current_one()
    {
        List<WorkflowStatus> all =
        [
            Wf("wan-i2v", "Wan I2V", "wan-vae", "wan-model"),
            Wf("wan-t2v", "Wan T2V", "wan-vae", "wan-model"),
            Wf("seedvr", "SeedVR2 Upscale", "wan-vae"),
        ];

        IReadOnlyList<string> shared = SlotSharing.Others(all, "wan-i2v", "wan-vae");

        Assert.Equal(["SeedVR2 Upscale", "Wan T2V"], shared);   // alphabetical, current workflow excluded
    }

    [Fact]
    public void A_single_use_slot_names_nobody()
    {
        List<WorkflowStatus> all =
        [
            Wf("wan-i2v", "Wan I2V", "wan-vae", "wan-model"),
            Wf("wan-t2v", "Wan T2V", "wan-vae"),
        ];

        Assert.Empty(SlotSharing.Others(all, "wan-i2v", "wan-model"));
    }

    [Fact]
    public void A_workflow_that_lists_a_slot_twice_is_not_counted_twice()
    {
        // Two configs sharing one display name (the shared-friendly-name case) must not double up in the warning.
        List<WorkflowStatus> all =
        [
            Wf("a", "Wan I2V", "wan-vae"),
            Wf("b-hi", "Wan T2V", "wan-vae"),
            Wf("b-lo", "Wan T2V", "wan-vae"),   // same friendly name, disjoint VRAM band
        ];

        Assert.Equal(["Wan T2V"], SlotSharing.Others(all, "a", "wan-vae"));
    }

    [Fact]
    public void The_real_catalogue_contains_both_a_shared_and_a_single_use_slot()
    {
        // Without this the feature is untested on real data: build every workflow's required-slot union exactly as the
        // status endpoint does (requirements ∪ IsModelRef params), then confirm at least one slot is used by more than
        // one workflow (so a warning is genuinely needed) and at least one by exactly one (so it's genuinely omitted).
        WorkflowCatalog catalog = new(
            new ComfyOptions { CatalogPath = Path.Combine(RepoRoot(), "configurations") },
            NullLogger<WorkflowCatalog>.Instance);
        WorkflowRegistry registry = new ServiceCollection().AddWorkflows().BuildServiceProvider().GetRequiredService<WorkflowRegistry>();

        List<WorkflowStatus> workflows = [];
        foreach (WorkflowConfiguration cfg in catalog.AllConfigs())
        {
            IWorkflow? wf = registry.Find(cfg.WorkflowName);
            if (wf is null)
            {
                continue;
            }

            List<string> required = [.. cfg.Requirements.All()
                .Concat(catalog.ModelRefSlots(wf, cfg))
                .Distinct(StringComparer.OrdinalIgnoreCase)];
            workflows.Add(new WorkflowStatus(cfg.Id, cfg.FriendlyName ?? cfg.Id, true, [], required, "generate"));
        }

        Dictionary<string, int> uses = new(StringComparer.OrdinalIgnoreCase);
        foreach (WorkflowStatus w in workflows)
        {
            foreach (string slot in w.RequiredSlots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                uses[slot] = uses.GetValueOrDefault(slot) + 1;
            }
        }

        Assert.Contains(uses, kv => kv.Value > 1);    // a slot changing it would fan out from
        Assert.Contains(uses, kv => kv.Value == 1);   // a slot used by exactly one workflow

        // And Others reflects that: pick a shared slot and assert its warning is non-empty from one of its users, and a
        // single-use slot yields none.
        string sharedSlot = uses.First(kv => kv.Value > 1).Key;
        WorkflowStatus user = workflows.First(w => w.RequiredSlots.Contains(sharedSlot, StringComparer.OrdinalIgnoreCase));
        Assert.NotEmpty(SlotSharing.Others(workflows, user.Id, sharedSlot));

        string soloSlot = uses.First(kv => kv.Value == 1).Key;
        WorkflowStatus soloUser = workflows.First(w => w.RequiredSlots.Contains(soloSlot, StringComparer.OrdinalIgnoreCase));
        Assert.Empty(SlotSharing.Others(workflows, soloUser.Id, soloSlot));
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
