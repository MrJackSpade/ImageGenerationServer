using ImageGen.Api.Contracts;
using ImageGen.Application.Rendering;
using ImageGen.Comfy;
using ImageGen.Domain;

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

    /// <summary>A submitted width/height IS a shape (#209): the recorded label follows the dims — wider is landscape,
    /// taller is portrait, equal is square.</summary>
    [Theory]
    [InlineData(1280, 720, "landscape")]
    [InlineData(720, 1280, "portrait")]
    [InlineData(1024, 1024, "square")]
    public void A_size_resolves_to_the_shape_it_is(int w, int h, string expected) =>
        Assert.Equal(expected, ComfyGraph.AspectFromDims(w, h));
}
