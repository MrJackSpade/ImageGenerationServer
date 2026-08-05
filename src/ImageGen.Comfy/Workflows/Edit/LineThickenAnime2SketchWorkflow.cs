using ImageGen.Application.Rendering;

namespace ImageGen.Comfy;

/// <summary>
/// Neural line extractor → thicken → composite. The <c>AnimeLineArtPreprocessor</c>
/// (comfyui_controlnet_aux) runs an anime line-art network over the source; the white-on-black
/// result is inverted to dark-lines-on-white, scaled back to the source size, boldened with
/// <c>LineThicken</c>, and multiplied over the source so only the extracted lines darken. The
/// preprocessor fetches its own weights from HuggingFace on first run. No diffusion checkpoint.
/// </summary>
public sealed class LineThickenAnime2SketchWorkflow : EditWorkflowBase
{
    public override string Name => "line-thicken-anime2sketch";
    public override bool PreservesComposition => true;
    public override bool RequiresModel => false;
    public override IReadOnlyList<ParamSpec> Schema => AnimeSchema;

    private static readonly IReadOnlyList<ParamSpec> AnimeSchema = new ParamSpec[]
    {
        new() { Key = WorkflowParamKeys.Thickness,  Type = ParamType.Int, Min = 0,   Max = 32,   Label = "Line thickness (px)" },
        new() { Key = WorkflowParamKeys.Resolution, Type = ParamType.Int, Min = 256, Max = 2048, Label = "Detector resolution" },
    };

    /// <summary>This workflow's own node ids.</summary>
    private const string Lineart = "20";
    private const string Invert = "21";
    private const string Size = "15";
    private const string Scale = "22";
    private const string Thicken = "23";
    private const string Blend = "24";
    private const string Save = "9";

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>
        {
            [Nodes.Source] = ComfyGraph.Node(ComfyNodeTypes.LoadImage, new { image = inputs.SourceImageName ?? throw new RenderValidationException("Line-thicken needs a source image, but none was provided.") }),
        };
        var src = PixelHarnessGraph.FlattenOnWhite(wf);   // flatten alpha onto white (nodes 11-14)
        // Extract anime line art (white-on-black), invert to dark-lines-on-white, force back to the source size.
        wf[Lineart] = ComfyGraph.Node(ComfyNodeTypes.AnimeLineArtPreprocessor, new { image = src, resolution = p.IntReq(WorkflowParamKeys.Resolution) });
        wf[Invert] = ComfyGraph.Node(ComfyNodeTypes.ImageInvert, new { image = ComfyGraph.Ref(Lineart, 0) });
        wf[Size] = ComfyGraph.Node(ComfyNodeTypes.GetImageSize, new { image = src });
        wf[Scale] = ComfyGraph.Node(ComfyNodeTypes.ImageScale, new
        {
            image = ComfyGraph.Ref(Invert, 0),
            upscale_method = "lanczos",
            width = ComfyGraph.Ref(Size, 0),
            height = ComfyGraph.Ref(Size, 1),
            crop = "disabled",
        });
        // Bolden the extracted lines, then multiply over the source (flat regions = white = unchanged).
        wf[Thicken] = ComfyGraph.Node(ComfyNodeTypes.LineThicken, new { image = ComfyGraph.Ref(Scale, 0), thickness = p.IntReq(WorkflowParamKeys.Thickness) });
        wf[Blend] = ComfyGraph.Node(ComfyNodeTypes.ImageBlend, new
        {
            image1 = src,
            image2 = ComfyGraph.Ref(Thicken, 0),
            blend_factor = 1.0,
            blend_mode = "multiply",
        });
        wf[Save] = ComfyGraph.Node(ComfyNodeTypes.SaveImage, new { images = ComfyGraph.Ref(Blend, 0), filename_prefix = "forgemcp_edit" });
        return wf;
    }
}
