using ImageGen.Application.Workflows;
using ImageGen.Comfy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImageGen.Tests;

/// <summary>
/// The masked-sibling (<c>mask_workflow</c>) link validation (Part A): a plain Edit config may name an Inpaint,
/// composition-preserving sibling submit routes to when a mask is drawn. A broken link is a boot/authoring error and
/// THROWS; a valid link marks the target hidden-from-picker while keeping it in the catalogue for enqueue/panel-swap.
/// </summary>
public sealed class MaskWorkflowLinkTests
{
    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "configurations", "models")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new DirectoryNotFoundException("configurations/ not found above the test bin dir.");
    }

    private static WorkflowRegistry Registry() =>
        new ServiceCollection().AddWorkflows().BuildServiceProvider().GetRequiredService<WorkflowRegistry>();

    private static WorkflowCatalog Catalog() =>
        new(new ComfyOptions { CatalogPath = Path.Combine(RepoRoot(), "configurations") }, NullLogger<WorkflowCatalog>.Instance);

    [Fact]
    public void The_shipped_qwen_pair_marks_the_inpaint_sibling_hidden_but_keeps_it_listed()
    {
        IReadOnlyList<WorkflowConfiguration> configs = Catalog().AllConfigs();
        HashSet<string> targets = WorkflowCatalogService.ValidateMaskLinks(configs, Registry());

        // The source carries the link; the target is marked hidden yet remains in the catalogue listing.
        WorkflowConfiguration source = configs.Single(c => c.Id == "qwen-image-edit");
        Assert.Equal("qwen-image-edit-inpaint", source.MaskWorkflow);
        Assert.Contains("qwen-image-edit-inpaint", targets);
        Assert.Contains(configs, c => c.Id == "qwen-image-edit-inpaint");
    }

    [Fact]
    public void A_link_to_a_missing_target_throws()
    {
        List<WorkflowConfiguration> configs =
        [
            new() { Id = "src", WorkflowName = "qwen-image-edit", MaskWorkflow = "does-not-exist" },
        ];

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => WorkflowCatalogService.ValidateMaskLinks(configs, Registry()));
        Assert.Contains("does-not-exist", ex.Message);
    }

    [Fact]
    public void A_link_to_a_non_inpaint_target_throws()
    {
        // The target names a plain Edit workflow, not an Inpaint one — the only kind that consumes a mask in-graph.
        List<WorkflowConfiguration> configs =
        [
            new() { Id = "src", WorkflowName = "qwen-image-edit", MaskWorkflow = "target" },
            new() { Id = "target", WorkflowName = "qwen-image-edit" },
        ];

        _ = Assert.Throws<InvalidOperationException>(() => WorkflowCatalogService.ValidateMaskLinks(configs, Registry()));
    }

    [Fact]
    public void A_config_with_no_link_yields_no_targets()
    {
        List<WorkflowConfiguration> configs =
        [
            new() { Id = "plain", WorkflowName = "qwen-image-edit" },
        ];

        Assert.Empty(WorkflowCatalogService.ValidateMaskLinks(configs, Registry()));
    }
}

public sealed class ReferenceWorkflowLinkTests
{
    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "configurations", "models")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new DirectoryNotFoundException("configurations/ not found above the test bin dir.");
    }

    private static WorkflowRegistry Registry() =>
        new ServiceCollection().AddWorkflows().BuildServiceProvider().GetRequiredService<WorkflowRegistry>();

    private static WorkflowCatalog Catalog() =>
        new(new ComfyOptions { CatalogPath = Path.Combine(RepoRoot(), "configurations") }, NullLogger<WorkflowCatalog>.Instance);

    [Fact]
    public void The_shipped_H3_pairs_link_visible_FL_configs_to_hidden_REF_targets()
    {
        IReadOnlyList<WorkflowConfiguration> configs = Catalog().AllConfigs();
        HashSet<string> targets = WorkflowCatalogService.ValidateReferenceLinks(configs, Registry());

        Assert.Equal("minimax-h3-ref2v", configs.Single(c => c.Id == "minimax-h3-i2v").ReferenceWorkflow);
        Assert.Equal("minimax-h3-ref2v-turbo", configs.Single(c => c.Id == "minimax-h3-i2v-turbo").ReferenceWorkflow);
        Assert.Contains("minimax-h3-ref2v", targets);
        Assert.Contains("minimax-h3-ref2v-turbo", targets);
    }

    [Fact]
    public void A_reference_link_to_a_missing_target_throws()
    {
        List<WorkflowConfiguration> configs =
        [
            new() { Id = "src", WorkflowName = "minimax-h3-i2v", ReferenceWorkflow = "missing" },
        ];

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => WorkflowCatalogService.ValidateReferenceLinks(configs, Registry()));
        Assert.Contains("missing", ex.Message);
    }

    [Fact]
    public void A_reference_link_to_a_non_reference_target_throws()
    {
        List<WorkflowConfiguration> configs =
        [
            new() { Id = "src", WorkflowName = "minimax-h3-i2v", ReferenceWorkflow = "target" },
            new() { Id = "target", WorkflowName = "minimax-h3-i2v" },
        ];

        _ = Assert.Throws<InvalidOperationException>(() => WorkflowCatalogService.ValidateReferenceLinks(configs, Registry()));
    }
}
