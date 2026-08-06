using ImageGen.Comfy;
using ImageGen.Application.Rendering;

namespace ImageGen.Comfy.Edit.BiRefNetMatteVideo;

/// <summary>
/// BiRefNet background-removal matte as a video-to-video edit — the sprite-pipeline bake-off winner. Each frame is
/// matted by the <c>BiRefNetMatte</c> node (PixelHarness) and the alpha kept, producing a TRANSPARENT-background
/// animated WEBP (<c>lossless=true</c> so the alpha channel survives). No checkpoint: the node loads its own model,
/// so this must not be hidden by the catalog's no-model guard. Composition-preserving (exempt from the no-change gate).
/// </summary>
public sealed class BiRefNetMatteVideoWorkflow : Workflow<MatteParams>
{
    public override string Name => "birefnet-matte-video";
    public override WorkflowKind Kind => WorkflowKind.Edit;
    public override WorkflowMedia Media => WorkflowMedia.Video;
    public override WorkflowMedia SourceMedia => WorkflowMedia.Video;
    public override bool PromptDirectsMotion => false;
    public override bool PreservesComposition => true;
    public override bool RequiresModel => false;
    public override IReadOnlyList<ParamSpec> Schema => MatteSchema;

    private static readonly IReadOnlyList<ParamSpec> MatteSchema = new ParamSpec[]
    {
        new() { Key = WorkflowParamKeys.Threshold, Type = ParamType.Double, Min = 0, Max = 1, Label = "Alpha cutoff", Help = "0 = soft matte (caller thresholds); >0 = hard cutoff at this matte value" },
    };

    /// <summary>This standalone graph's node ids, named by role.</summary>
    private static class Nodes
    {
        public const string Source = "10";
        public const string Components = "11";
        public const string Matte = "20";
        public const string Save = "9";
    }

    protected override ComfyWorkflowGraph Build(MatteParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        return new ComfyWorkflowGraph
        {
            [Nodes.Source] = new LoadVideo
            {
                File = inputs.SourceVideoName ?? throw new RenderValidationException("The video matte needs a source clip, but none was provided."),
            },
            [Nodes.Components] = new GetVideoComponents { Video = LoadVideo.VideoOut(Nodes.Source) },
            // BiRefNetMatte output 0 = RGBA (frame + matte as alpha).
            [Nodes.Matte] = new global::ImageGen.Comfy.BiRefNetMatte
            {
                Image = GetVideoComponents.ImagesOut(Nodes.Components),
                Threshold = p.Threshold,
            },
            // Keep the source clip's frame rate (GetVideoComponents output 2). lossless=true so the alpha survives.
            [Nodes.Save] = new SaveAnimatedWEBP
            {
                Images = global::ImageGen.Comfy.BiRefNetMatte.Out(Nodes.Matte),
                FilenamePrefix = OutputPrefixes.Edit,
                Fps = GetVideoComponents.FpsOut(Nodes.Components),
                Lossless = true,
                Quality = 100,
                Method = ComfyWidgets.WebpMethod.Default,
            },
        };
    }
}
