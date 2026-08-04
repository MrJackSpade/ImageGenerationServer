//TODO: CHECK FOR FALLBACKS
using ImageGen.Application.Rendering;

namespace ImageGen.Comfy;

/// <summary>
/// BiRefNet background-removal matte as a video-to-video edit — the sprite-pipeline bake-off winner. Each frame is
/// matted by the <c>BiRefNetMatte</c> node (PixelHarness) and the alpha kept, producing a TRANSPARENT-background
/// animated WEBP (<c>lossless=true</c> so the alpha channel survives). No checkpoint: the node loads its own model,
/// so this must not be hidden by the catalog's no-model guard. Composition-preserving (exempt from the no-change gate).
/// </summary>
public sealed class BiRefNetMatteVideoWorkflow : IWorkflow
{
    public string Name => "birefnet-matte-video";
    public WorkflowKind Kind => WorkflowKind.Edit;
    public WorkflowMedia Media => WorkflowMedia.Video;
    public WorkflowMedia SourceMedia => WorkflowMedia.Video;
    public bool PromptDirectsMotion => false;
    public bool PreservesComposition => true;
    public bool RequiresModel => false;
    public IReadOnlyList<ParamSpec> Schema => MatteSchema;

    private static readonly IReadOnlyList<ParamSpec> MatteSchema = new ParamSpec[]
    {
        new() { Key = "threshold", Type = ParamType.Double, Default = 0, Min = 0, Max = 1, Label = "Alpha cutoff", Help = "0 = soft matte (caller thresholds); >0 = hard cutoff at this matte value" },
    };

    public Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>
        {
            ["10"] = ComfyGraph.Node("LoadVideo", new { file = inputs.SourceVideoName ?? "" }),
            ["11"] = ComfyGraph.Node("GetVideoComponents", new { video = ComfyGraph.Ref("10", 0) }),
            // BiRefNetMatte output 0 = RGBA (frame + matte as alpha).
            ["20"] = ComfyGraph.Node("BiRefNetMatte", new { image = ComfyGraph.Ref("11", 0), threshold = p.Dbl("threshold", 0) }),
        };
        // Keep the source clip's frame rate (GetVideoComponents output 2). lossless=true so the alpha survives.
        wf["9"] = ComfyGraph.Node("SaveAnimatedWEBP", new { images = ComfyGraph.Ref("20", 0), filename_prefix = "forgemcp_edit", fps = ComfyGraph.Ref("11", 2), lossless = true, quality = 100, method = "default" });
        return wf;
    }
}
