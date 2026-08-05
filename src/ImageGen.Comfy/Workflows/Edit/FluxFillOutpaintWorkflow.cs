namespace ImageGen.Comfy;

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
    protected override (int W, int H) CanvasSize(ParamValues p, WorkflowInputs inputs)
    {
        if (inputs.SourceWidth <= 0 || inputs.SourceHeight <= 0) return (0, 0);
        return (inputs.SourceWidth + Math.Max(0, p.Int(WorkflowParamKeys.PadLeft)) + Math.Max(0, p.Int(WorkflowParamKeys.PadRight)),
                inputs.SourceHeight + Math.Max(0, p.Int(WorkflowParamKeys.PadTop)) + Math.Max(0, p.Int(WorkflowParamKeys.PadBottom)));
    }

    public override IReadOnlyList<ParamSpec> Schema => OutpaintSchema;
    private static readonly IReadOnlyList<ParamSpec> OutpaintSchema = FillSchema.Concat(new ParamSpec[]
    {
        new() { Key = WorkflowParamKeys.PadLeft,   Type = ParamType.Int, Min = 0, Max = 4096, Label = "Extend left (px)" },
        new() { Key = WorkflowParamKeys.PadTop,    Type = ParamType.Int, Min = 0, Max = 4096, Label = "Extend top (px)" },
        new() { Key = WorkflowParamKeys.PadRight,  Type = ParamType.Int, Min = 0, Max = 4096, Label = "Extend right (px)" },
        new() { Key = WorkflowParamKeys.PadBottom, Type = ParamType.Int, Min = 0, Max = 4096, Label = "Extend bottom (px)" },
        new() { Key = WorkflowParamKeys.MaskGrow,  Type = ParamType.Int, Min = 0, Max = 64, Label = "Mask grow (px)" },
    }).ToArray();

    private const string Pad = "20";

    protected override void ResolveCanvas(Dictionary<string, object> wf, ParamValues p, WorkflowInputs inputs,
        out object image, out object rawMask)
    {
        wf[Pad] = ComfyGraph.Node(ComfyNodeTypes.ImagePadForOutpaint, new
        {
            image = ComfyGraph.Ref(Nodes.Source, 0),
            left = Math.Max(0, p.Int(WorkflowParamKeys.PadLeft)),
            top = Math.Max(0, p.Int(WorkflowParamKeys.PadTop)),
            right = Math.Max(0, p.Int(WorkflowParamKeys.PadRight)),
            bottom = Math.Max(0, p.Int(WorkflowParamKeys.PadBottom)),
            // Softening happens once, in SoftenMask — the node's own feathering would stack with it.
            feathering = 0,
        });
        image = ComfyGraph.Ref(Pad, 0);
        rawMask = ComfyGraph.Ref(Pad, 1);
    }
}
