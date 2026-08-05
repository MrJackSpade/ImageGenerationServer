using ImageGen.Api.Contracts;
using ImageGen.Application.Rendering;
using ImageGen.Domain;

namespace ImageGen.Tests;

/// <summary>
/// The wire → spec mapping is hand-written, so a field added to the request body but forgotten in the mapper compiles
/// fine and silently never reaches the renderer. These pin the per-generation values that have no other witness.
/// </summary>
public sealed class RenderContractMappingTests
{
    /// <summary>A single generate carries its generation mask through to the spec the orchestrator renders from.</summary>
    [Fact]
    public void A_generate_request_carries_its_mask_into_the_spec()
    {
        GenerateSpec spec = new GenerateRequest("anima", "a prompt", null, "square", RandomPrompt: TriState.True,
            TagTypes: ["general", "character"]).ToSpec();

        Assert.Equal(["general", "character"], spec.TagTypes);
    }

    /// <summary>
    /// And so does every item of a batch — the composer's Generate submits n slots through /enqueue, so a mask that
    /// only survived the single-image path would apply to exactly the one case nobody uses it for.
    /// </summary>
    [Fact]
    public void A_batch_item_carries_its_mask_into_the_spec()
    {
        EnqueueItem item = new EnqueueItem(Edit: false, Workflow: "anima", Prompt: "a prompt", NegativePrompt: null,
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
        GenerateSpec spec = new GenerateRequest("anima", "a prompt", null, "square").ToSpec();

        Assert.Null(spec.TagTypes);
    }
}
