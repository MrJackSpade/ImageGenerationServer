namespace ImageGen.Comfy.Edit.Krea2AnyPaint;

/// <summary>
/// OUTPAINT on Krea 2 Turbo (AnyPaint). The canvas grows by the per-side pads and the added border is the region
/// generated; no interior mask is supplied (<see cref="Krea2AnyPaintPrepare"/> then treats the whole source rectangle
/// as preserved). Inpaint and outpaint can be combined in one request, but this config exposes only the pads — the
/// inpaint sibling exposes the mask.
/// </summary>
public sealed class Krea2AnyPaintOutpaintWorkflow : Krea2AnyPaintBase
{
    public override string OutputSizePolicy => OutputSizePolicies.ExpandedCanvas;
    public override string Name => "krea2-anypaint-outpaint";
    public override WorkflowKind Kind => WorkflowKind.Outpaint;

    public override IReadOnlyList<ParamSpec> Schema => OutpaintSchema;
    private static readonly IReadOnlyList<ParamSpec> OutpaintSchema =
    [
        .. AnyPaintSchema,
        new() { Key = WorkflowParamKeys.PadLeft,   Type = ParamType.Int, Min = 0, Max = 4096, Label = "Extend left (px)" },
        new() { Key = WorkflowParamKeys.PadTop,    Type = ParamType.Int, Min = 0, Max = 4096, Label = "Extend top (px)" },
        new() { Key = WorkflowParamKeys.PadRight,  Type = ParamType.Int, Min = 0, Max = 4096, Label = "Extend right (px)" },
        new() { Key = WorkflowParamKeys.PadBottom, Type = ParamType.Int, Min = 0, Max = 4096, Label = "Extend bottom (px)" },
    ];

    protected override void ResolveRegion(ComfyWorkflowGraph g, Krea2AnyPaintParams p, WorkflowInputs inputs,
        out Output<Slot.Mask>? generatedMask, out int left, out int top, out int right, out int bottom)
    {
        generatedMask = null;   // pure outpaint: only the padding is generated
        left = p.PadLeft;
        top = p.PadTop;
        right = p.PadRight;
        bottom = p.PadBottom;
    }
}
