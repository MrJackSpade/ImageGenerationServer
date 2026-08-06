using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;

namespace ImageGen.Comfy.Edit.LineThickenAnime2Sketch;

/// <summary>
/// Neural line extractor → thicken → composite. The <c>AnimeLineArtPreprocessor</c>
/// (comfyui_controlnet_aux) runs an anime line-art network over the source; the white-on-black
/// result is inverted to dark-lines-on-white, scaled back to the source size, boldened with
/// <c>LineThicken</c>, and multiplied over the source so only the extracted lines darken. The
/// preprocessor fetches its own weights from HuggingFace on first run. No diffusion checkpoint.
/// </summary>
public sealed class LineThickenAnime2SketchWorkflow : EditWorkflow<LineThickenAnime2SketchParams>
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

    protected override ComfyWorkflowGraph Build(LineThickenAnime2SketchParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        string source = inputs.SourceImageName ?? throw new RenderValidationException("Line-thicken needs a source image, but none was provided.");
        ComfyWorkflowGraph g = new ComfyWorkflowGraph
        {
            [EditNodes.Source] = new LoadImage { Image = source },
        };
        Output<Slot.Image> src = PixelHarnessGraph.FlattenOnWhite(g);   // flatten alpha onto white (nodes 11-14)
        // Extract anime line art (white-on-black), invert to dark-lines-on-white, force back to the source size.
        g[Nodes.Lineart] = new AnimeLineArtPreprocessor { Image = src, Resolution = p.Resolution };
        g[Nodes.Invert] = new ImageInvert { Image = AnimeLineArtPreprocessor.Out(Nodes.Lineart) };
        g[Nodes.Size] = new GetImageSize { Image = src };
        g[Nodes.Scale] = new ImageScaleToImageSize
        {
            Image = ImageInvert.Out(Nodes.Invert),
            UpscaleMethod = ComfyWidgets.Upscale.Lanczos,
            Width = GetImageSize.WidthOut(Nodes.Size),
            Height = GetImageSize.HeightOut(Nodes.Size),
            Crop = ComfyWidgets.Crop.Disabled,
        };
        // Bolden the extracted lines, then multiply over the source (flat regions = white = unchanged).
        g[Nodes.Thicken] = new LineThicken { Image = ImageScaleToImageSize.Out(Nodes.Scale), Thickness = p.Thickness };
        g[Nodes.Blend] = new ImageBlend
        {
            Image1 = src,
            Image2 = LineThicken.Out(Nodes.Thicken),
            BlendFactor = 1.0,
            BlendMode = ComfyWidgets.Blend.Multiply,
        };
        g[Nodes.Save] = new SaveImage { Images = ImageBlend.Out(Nodes.Blend), FilenamePrefix = OutputPrefixes.Edit };
        return g;
    }
}
