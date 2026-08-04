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
        new() { Key = "thickness",  Type = ParamType.Int, Min = 0,   Max = 32,   Label = "Line thickness (px)" },
        new() { Key = "resolution", Type = ParamType.Int, Min = 256, Max = 2048, Label = "Detector resolution" },
    };

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>
        {
            ["10"] = ComfyGraph.Node("LoadImage", new { image = inputs.SourceImageName ?? throw new RenderValidationException("Line-thicken needs a source image, but none was provided.") }),
        };
        var src = PixelHarnessGraph.FlattenOnWhite(wf);   // flatten alpha onto white (nodes 11-14)
        // Extract anime line art (white-on-black), invert to dark-lines-on-white, force back to the source size.
        wf["20"] = ComfyGraph.Node("AnimeLineArtPreprocessor", new { image = src, resolution = p.IntReq("resolution") });
        wf["21"] = ComfyGraph.Node("ImageInvert", new { image = ComfyGraph.Ref("20", 0) });
        wf["15"] = ComfyGraph.Node("GetImageSize", new { image = src });
        wf["22"] = ComfyGraph.Node("ImageScale", new
        {
            image = ComfyGraph.Ref("21", 0),
            upscale_method = "lanczos",
            width = ComfyGraph.Ref("15", 0),
            height = ComfyGraph.Ref("15", 1),
            crop = "disabled",
        });
        // Bolden the extracted lines, then multiply over the source (flat regions = white = unchanged).
        wf["23"] = ComfyGraph.Node("LineThicken", new { image = ComfyGraph.Ref("22", 0), thickness = p.IntReq("thickness") });
        wf["24"] = ComfyGraph.Node("ImageBlend", new
        {
            image1 = src,
            image2 = ComfyGraph.Ref("23", 0),
            blend_factor = 1.0,
            blend_mode = "multiply",
        });
        wf["9"] = ComfyGraph.Node("SaveImage", new { images = ComfyGraph.Ref("24", 0), filename_prefix = "forgemcp_edit" });
        return wf;
    }
}
