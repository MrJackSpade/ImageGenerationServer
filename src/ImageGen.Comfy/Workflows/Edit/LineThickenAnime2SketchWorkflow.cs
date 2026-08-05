using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;

namespace ImageGen.Comfy;

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

    /// <summary>This workflow's own node ids.</summary>
    private const string Lineart = "20";
    private const string Invert = "21";
    private const string Size = "15";
    private const string Scale = "22";
    private const string Thicken = "23";
    private const string Blend = "24";
    private const string Save = "9";

    protected override ComfyWorkflowGraph Build(LineThickenAnime2SketchParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        string source = inputs.SourceImageName ?? throw new RenderValidationException("Line-thicken needs a source image, but none was provided.");
        ComfyWorkflowGraph g = new ComfyWorkflowGraph
        {
            [Nodes.Source] = new LoadImage { Image = source },
        };
        Output<Slot.Image> src = PixelHarnessGraph.FlattenOnWhite(g);   // flatten alpha onto white (nodes 11-14)
        // Extract anime line art (white-on-black), invert to dark-lines-on-white, force back to the source size.
        g[Lineart] = new AnimeLineArtPreprocessor { Image = src, Resolution = p.Resolution };
        g[Invert] = new ImageInvert { Image = AnimeLineArtPreprocessor.Out(Lineart) };
        g[Size] = new GetImageSize { Image = src };
        g[Scale] = new ImageScaleToImageSize
        {
            Image = ImageInvert.Out(Invert),
            UpscaleMethod = "lanczos",
            Width = GetImageSize.WidthOut(Size),
            Height = GetImageSize.HeightOut(Size),
            Crop = "disabled",
        };
        // Bolden the extracted lines, then multiply over the source (flat regions = white = unchanged).
        g[Thicken] = new LineThicken { Image = ImageScaleToImageSize.Out(Scale), Thickness = p.Thickness };
        g[Blend] = new ImageBlend
        {
            Image1 = src,
            Image2 = LineThicken.Out(Thicken),
            BlendFactor = 1.0,
            BlendMode = "multiply",
        };
        g[Save] = new SaveImage { Images = ImageBlend.Out(Blend), FilenamePrefix = "forgemcp_edit" };
        return g;
    }
}

/// <summary>The anime2sketch thickener's parameters. <c>required</c> so an absent value throws at the deserializer
/// (the declarative form of the previous <c>IntReq</c> reads).</summary>
public sealed record LineThickenAnime2SketchParams
{
    [JsonPropertyName(WorkflowParamKeys.Thickness)]
    [Range(0, 32)]                                   public required int Thickness { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Resolution)]
    [Range(256, 2048)]                               public required int Resolution { get; init; }
}
