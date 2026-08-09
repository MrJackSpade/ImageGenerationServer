namespace ImageGen.Comfy.Edit.Krea2AnyPaint;

/// <summary>
/// Arbitrary-mask INPAINT on Krea 2 Turbo (AnyPaint). The region(s) to regenerate arrive as a white-on-black mask
/// upload (<c>inputs.MaskImageName</c>, painted in the edit UI), falling back to the source's alpha. The mask may be
/// any shape and may cover several disconnected areas; no padding is added.
/// </summary>
public sealed class Krea2AnyPaintInpaintWorkflow : Krea2AnyPaintBase
{
    public override string Name => "krea2-anypaint-inpaint";
    public override WorkflowKind Kind => WorkflowKind.Inpaint;

    public override IReadOnlyList<ParamSpec> Schema => AnyPaintSchema;

    protected override void ResolveRegion(ComfyWorkflowGraph g, Krea2AnyPaintParams p, WorkflowInputs inputs,
        out Output<Slot.Mask>? generatedMask, out int left, out int top, out int right, out int bottom)
    {
        left = top = right = bottom = 0;   // interior edit only — the canvas keeps the source's size
        if (!string.IsNullOrEmpty(inputs.MaskImageName))
        {
            g[Nodes.Mask] = new LoadImageMask { Image = inputs.MaskImageName, Channel = ComfyWidgets.MaskChannel.Red };
            generatedMask = LoadImageMask.Out(Nodes.Mask);
        }
        else
        {
            generatedMask = LoadImage.MaskOut(EditNodes.Source);   // source alpha
        }
    }
}
