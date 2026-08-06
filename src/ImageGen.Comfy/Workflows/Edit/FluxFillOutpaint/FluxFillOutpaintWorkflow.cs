using ImageGen.Domain;

namespace ImageGen.Comfy.Edit.FluxFillOutpaint;

/// <summary>
/// OUTPAINT on FLUX.1 Fill [dev]. <c>ImagePadForOutpaint</c> supplies the enlarged canvas and the border mask.
///
/// <para>Its 0.5-grey pad is harmless here and deliberately left alone: <c>InpaintModelConditioning</c> re-blanks the
/// masked region to that exact grey anyway as the model's trained fill signal, and nothing alpha-blends the pad into
/// the output. (On the ControlNet path this same grey has to be engineered away — see
/// <see cref="QwenImageOutpaintWorkflow"/> — because there it IS blended.)</para>
/// </summary>
public sealed class FluxFillOutpaintWorkflow : FluxFillBase
{
    public override string Name => "flux1-fill-outpaint";

    /// <summary>An outpaint's masked region IS the scene's continuation, so a scene-level prompt is the right ask
    /// (the official outpaint example is just "beautiful scenery").</summary>
    public override PromptSemantics PromptSemantics => PromptSemantics.WholeImage;

    /// <summary>The ceiling applies to the PADDED canvas — outpainting is what actually grows the frame.</summary>
    protected override (int W, int H) CanvasSize(FluxFillParams p, WorkflowInputs inputs)
    {
        _ = Ensure.GreaterThanZero(inputs.SourceWidth);
        _ = Ensure.GreaterThanZero(inputs.SourceHeight);
        return (inputs.SourceWidth + p.PadLeft + p.PadRight,
                inputs.SourceHeight + p.PadTop + p.PadBottom);
    }

    public override IReadOnlyList<ParamSpec> Schema => OutpaintSchema;
    private static readonly IReadOnlyList<ParamSpec> OutpaintSchema =
    [
        .. FillSchema,
        new() { Key = WorkflowParamKeys.PadLeft,   Type = ParamType.Int, Min = 0, Max = 4096, Label = "Extend left (px)" },
        new() { Key = WorkflowParamKeys.PadTop,    Type = ParamType.Int, Min = 0, Max = 4096, Label = "Extend top (px)" },
        new() { Key = WorkflowParamKeys.PadRight,  Type = ParamType.Int, Min = 0, Max = 4096, Label = "Extend right (px)" },
        new() { Key = WorkflowParamKeys.PadBottom, Type = ParamType.Int, Min = 0, Max = 4096, Label = "Extend bottom (px)" },
        new() { Key = WorkflowParamKeys.MaskGrow,  Type = ParamType.Int, Min = 0, Max = 64, Label = "Mask grow (px)" },
    ];

    protected override void ResolveCanvas(ComfyWorkflowGraph g, FluxFillParams p, WorkflowInputs inputs,
        out Output<Slot.Image> image, out Output<Slot.Mask> rawMask)
    {
        g[Nodes.Pad] = new ImagePadForOutpaint
        {
            Image = LoadImage.ImageOut(EditNodes.Source),
            Left = p.PadLeft,
            Top = p.PadTop,
            Right = p.PadRight,
            Bottom = p.PadBottom,
            // Softening happens once, in SoftenMask — the node's own feathering would stack with it.
            Feathering = 0,
        };
        image = ImagePadForOutpaint.ImageOut(Nodes.Pad);
        rawMask = ImagePadForOutpaint.MaskOut(Nodes.Pad);
    }
}