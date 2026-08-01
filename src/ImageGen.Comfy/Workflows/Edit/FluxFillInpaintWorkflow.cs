namespace ImageGen.Comfy;

/// <summary>
/// Masked INPAINT on FLUX.1 Fill [dev]. The region arrives as a white-on-black mask upload
/// (<c>inputs.MaskImageName</c>, painted in the edit UI), falling back to the source's alpha.
/// </summary>
public sealed class FluxFillInpaintWorkflow : FluxFillBase
{
    public override string Name => "flux1-fill-inpaint";

    /// <summary>Fill's prompt names what appears IN the masked region (its official examples prompt the patch —
    /// "a white paper cup" — never the scene). A whole-scene prompt at guidance 30 is an instruction to render the
    /// whole scene INTO the hole: measured −60 luminance levels on a sky fill, vs −6 for a region prompt.</summary>
    public override PromptSemantics PromptSemantics => PromptSemantics.MaskedRegion;

    public override IReadOnlyList<ParamSpec> Schema => InpaintSchema;
    private static readonly IReadOnlyList<ParamSpec> InpaintSchema = FillSchema.Concat(new ParamSpec[]
    {
        // Grow pushes the whole soft band OUTWARD onto real pixels, so the painted region keeps a hard 1 and is
        // fully replaced. 16 ≈ 2σ places the band's midpoint just outside the painted edge.
        new() { Key = "mask_grow", Type = ParamType.Int, Default = 16, Min = 0, Max = 64, Label = "Mask grow (px)" },
    }).ToArray();

    protected override void ResolveCanvas(Dictionary<string, object> wf, ParamValues p, WorkflowInputs inputs,
        out object image, out object rawMask)
    {
        image = ComfyGraph.Ref("10", 0);
        if (!string.IsNullOrEmpty(inputs.MaskImageName))
        {
            wf["11"] = ComfyGraph.Node("LoadImageMask", new { image = inputs.MaskImageName, channel = "red" });
            rawMask = ComfyGraph.Ref("11", 0);
        }
        else rawMask = ComfyGraph.Ref("10", 1);   // source alpha
    }
}
