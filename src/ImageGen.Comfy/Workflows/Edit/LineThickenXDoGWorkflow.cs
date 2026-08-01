namespace ImageGen.Comfy;

/// <summary>
/// Model-free OUTLINE-ONLY thickener — XDoG line extraction → thicken the extracted lines → multiply
/// them back over the original. Unlike the plain erode (which darkens every dark pixel), this touches
/// only the edges: <c>XDoGLines</c> (ComfyUI-PixelHarness) pulls the existing outlines out as
/// dark-lines-on-white, <c>LineThicken</c> boldens that line layer, and a multiply <c>ImageBlend</c>
/// composites it over the source so flat-colour interiors stay clean. No model, no VRAM. API-only.
/// </summary>
public sealed class LineThickenXDoGWorkflow : EditWorkflowBase
{
    public override string Name => "line-thicken-xdog";
    public override bool PreservesComposition => true;
    public override bool RequiresModel => false;
    public override IReadOnlyList<ParamSpec> Schema => XDoGSchema;

    private static readonly IReadOnlyList<ParamSpec> XDoGSchema = new ParamSpec[]
    {
        new() { Key = "thickness", Type = ParamType.Int,    Default = 2,    Min = 0,    Max = 32,  Label = "Line thickness (px)" },
        new() { Key = "sigma",     Type = ParamType.Double, Default = 1.0,  Min = 0.3,  Max = 8.0, Label = "Line scale (sigma)" },
        new() { Key = "k",         Type = ParamType.Double, Default = 1.6,  Min = 1.0,  Max = 4.0 },
        new() { Key = "tau",       Type = ParamType.Double, Default = 0.98, Min = 0.5,  Max = 1.0 },
        new() { Key = "epsilon",   Type = ParamType.Double, Default = 0.0,  Min = -1.0, Max = 1.0, Label = "Edge threshold (0=flats stay clean)" },
        new() { Key = "phi",       Type = ParamType.Double, Default = 10.0, Min = 0.1,  Max = 50.0 },
    };

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>
        {
            ["10"] = ComfyGraph.Node("LoadImage", new { image = inputs.SourceImageName ?? "" }),
        };
        var src = PixelHarnessGraph.FlattenOnWhite(wf);   // flatten alpha onto white (nodes 11-14)
        // Extract the existing outlines as dark-lines-on-white...
        wf["20"] = ComfyGraph.Node("XDoGLines", new
        {
            image = src,
            sigma = p.Dbl("sigma", 1.0),
            k = p.Dbl("k", 1.6),
            tau = p.Dbl("tau", 0.98),
            epsilon = p.Dbl("epsilon", 0.0),
            phi = p.Dbl("phi", 10.0),
        });
        // ...bolden that line layer...
        wf["21"] = ComfyGraph.Node("LineThicken", new { image = ComfyGraph.Ref("20", 0), thickness = p.Int("thickness", 2) });
        // ...and multiply it back over the source so only the outlines darken (flat regions = white = unchanged).
        wf["22"] = ComfyGraph.Node("ImageBlend", new
        {
            image1 = src,
            image2 = ComfyGraph.Ref("21", 0),
            blend_factor = 1.0,
            blend_mode = "multiply",
        });
        wf["9"] = ComfyGraph.Node("SaveImage", new { images = ComfyGraph.Ref("22", 0), filename_prefix = "forgemcp_edit" });
        return wf;
    }
}
