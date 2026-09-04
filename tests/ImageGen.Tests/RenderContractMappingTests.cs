using ImageGen.Api;
using ImageGen.Api.Contracts;
using ImageGen.Application.Rendering;
using ImageGen.Comfy;
using ImageGen.Domain;
using System.Text.Json;

namespace ImageGen.Tests;

/// <summary>
/// The wire → spec mapping is hand-written, so a field added to the request body but forgotten in the mapper compiles
/// fine and silently never reaches the renderer. These pin the per-generation values that have no other witness.
/// </summary>
public sealed class RenderContractMappingTests
{
    /// <summary>
    /// Every item of a batch carries its generation mask through to the spec the orchestrator renders from — the
    /// composer's Generate submits n slots through /enqueue (the only submission path), so a mask that didn't survive
    /// the mapping would silently never reach the renderer.
    /// </summary>
    [Fact]
    public void A_batch_item_carries_its_mask_into_the_spec()
    {
        EnqueueItem item = new(Edit: false, Workflow: "anima", Prompt: "a prompt", NegativePrompt: null,
            Aspect: "square", Instruction: null, ImageId: null, RandomPrompt: TriState.True, TagTypes: ["meta"]);

        RenderItem? ri = item.ToRenderItem();
        Assert.NotNull(ri);
        GenerateSpec? spec = ri.Gen;
        Assert.NotNull(spec);

        Assert.Equal(["meta"], spec.TagTypes);
    }

    /// <summary>An omitted mask stays null — the "use the owner's stored mask" signal, not "none of them".</summary>
    [Fact]
    public void An_omitted_mask_stays_null()
    {
        GenerateSpec? spec = new EnqueueItem(Workflow: "anima", Prompt: "a prompt", Aspect: "square").ToRenderItem()?.Gen;

        Assert.NotNull(spec);
        Assert.Null(spec.TagTypes);
    }

    /// <summary>The composer no longer sends an aspect name on the wire (#209): it submits width/height, and the enqueue
    /// boundary resolves the record's shape label and passes it in. That resolved label is what reaches the spec.</summary>
    [Fact]
    public void A_resolved_aspect_overrides_the_wire_aspect_on_the_spec()
    {
        GenerateSpec? spec = new EnqueueItem(Workflow: "anima", Prompt: "a prompt", Aspect: null)
            .ToRenderItem("landscape")?.Gen;

        Assert.NotNull(spec);
        Assert.Equal("landscape", spec.Aspect);
    }

    /// <summary>With no resolved aspect supplied, the item's own aspect still stands — the API aspect-only path.</summary>
    [Fact]
    public void An_unresolved_call_keeps_the_items_own_aspect()
    {
        GenerateSpec? spec = new EnqueueItem(Workflow: "anima", Prompt: "a prompt", Aspect: "portrait")
            .ToRenderItem()?.Gen;

        Assert.NotNull(spec);
        Assert.Equal("portrait", spec.Aspect);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Prompt_syntax_resolution_flag_reaches_generate_and_edit_specs(bool resolve)
    {
        RenderItem? generate = new EnqueueItem(
            Workflow: "anima", Prompt: "{red|blue}", ResolvePromptSyntax: resolve).ToRenderItem();
        RenderItem? edit = new EnqueueItem(
            Workflow: "editor", Edit: true, Instruction: "{red|blue}", ImageId: "source", ResolvePromptSyntax: resolve)
            .ToRenderItem();

        Assert.Equal(resolve, Assert.IsType<GenerateSpec>(generate?.Gen).ResolvePromptSyntax);
        Assert.Equal(resolve, Assert.IsType<EditSpec>(edit?.Edit).ResolvePromptSyntax);
    }

    [Fact]
    public void Omitted_wire_flag_defaults_to_resolving_prompt_syntax()
    {
        EnqueueItem? item = JsonSerializer.Deserialize<EnqueueItem>(
            """{"workflow":"anima","prompt":"{red|blue}"}""", Json.Options);

        Assert.True(Assert.IsType<EnqueueItem>(item).ResolvePromptSyntax);
    }

    [Fact]
    public void Reference_only_edit_mapping_preserves_an_absent_primary_source()
    {
        RenderItem? item = new EnqueueItem(
            Workflow: "qwen-image-edit", Edit: true, Instruction: "use the reference",
            ImageId: null, ReferenceIds: ["ref-1"]).ToRenderItem();

        EditSpec edit = Assert.IsType<EditSpec>(item?.Edit);
        Assert.Null(edit.ImageId);
        Assert.Equal(["ref-1"], edit.ReferenceIds);
    }

    [Fact]
    public void A_random_artist_choice_reaches_an_edit_spec()
    {
        RenderItem? item = new EnqueueItem(
            Workflow: "anima-redraw", Edit: true, Instruction: "#1girl", ImageId: "source",
            RandomArtist: TriState.True).ToRenderItem();

        EditSpec edit = Assert.IsType<EditSpec>(item?.Edit);
        Assert.Equal(TriState.True, edit.RandomArtist);
    }

    /// <summary>A submitted width/height IS a shape (#209): the recorded label follows the dims — wider is landscape,
    /// taller is portrait, equal is square.</summary>
    [Theory]
    [InlineData(1280, 720, "landscape")]
    [InlineData(720, 1280, "portrait")]
    [InlineData(1024, 1024, "square")]
    public void A_size_resolves_to_the_shape_it_is(int w, int h, string expected) =>
        Assert.Equal(expected, ComfyGraph.AspectFromDims(w, h));
}
