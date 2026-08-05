using ImageGen.Application.Rendering;

namespace ImageGen.Comfy;

/// <summary>
/// Automatic flicker/wash correction as a video-to-video edit — the <c>DeflickerAuto</c> node (PixelHarness) over the
/// whole clip. It mattes every frame (BiRefNet, the pipeline keyer, alpha renormalized by interior confidence so the
/// correction strength is decode/contrast-invariant), computes pose/drift-invariant character stats over the
/// character's own palette (luma P5/P15 dark tail, chroma P90 — a wash is a chroma collapse), flags frames that deviate from
/// the whole-clip median by more than <c>mad_k</c>·MAD (floor <c>min_dev</c> 8-bit levels), and corrects each flagged
/// frame by per-channel CDF matching of its character pixels to ALL clean frames pooled with temporal-proximity
/// weights (so the reference tracks legitimate drift). If nothing flags, the clip passes through untouched. No
/// checkpoint (the node loads its own BiRefNet), so it must not be hidden by the catalog's no-model guard.
/// Composition-preserving. Output is a lossless animated WEBP at the source frame rate so downstream stages see the
/// corrected clip verbatim.
/// </summary>
public sealed class DeflickerAutoVideoWorkflow : IWorkflow
{
    public string Name => "deflicker-auto";
    public WorkflowKind Kind => WorkflowKind.Edit;
    public WorkflowMedia Media => WorkflowMedia.Video;
    public WorkflowMedia SourceMedia => WorkflowMedia.Video;
    public bool PromptDirectsMotion => false;
    public bool PreservesComposition => true;
    public bool RequiresModel => false;
    public IReadOnlyList<ParamSpec> Schema => DeflickerSchema;

    /// <summary>This graph's node ids, named by role. Values are the graph-local keys, preserved exactly so the
    /// emitted graph stays byte-identical.</summary>
    private static class Nodes
    {
        public const string Source = "10";
        public const string Components = "11";
        public const string Deflicker = "20";
        public const string Save = "9";
    }

    private static readonly IReadOnlyList<ParamSpec> DeflickerSchema = new ParamSpec[]
    {
        new() { Key = WorkflowParamKeys.MadK, Type = ParamType.Double, Min = 0.5, Max = 20.0, Label = "MAD K", Help = "Robust threshold: flag a frame past K*MAD of the whole-clip series" },
        new() { Key = WorkflowParamKeys.MinDev, Type = ParamType.Double, Min = 0.0, Max = 16.0, Label = "Min deviation (levels)", Help = "Absolute floor in 8-bit levels — smaller deviations are invisible" },
        new() { Key = WorkflowParamKeys.AlphaCut, Type = ParamType.Double, Min = 0.0, Max = 1.0, Label = "Matte cutoff", Help = "BiRefNet matte threshold for the character pixel set" },
        new() { Key = WorkflowParamKeys.TimeSigma, Type = ParamType.Double, Min = 0.1, Max = 32.0, Label = "Reference sigma (frames)", Help = "How fast the clean-frame reference pool's temporal weights fall off" },
    };

    public Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>
        {
            [Nodes.Source] = ComfyGraph.Node(ComfyNodeTypes.LoadVideo, new { file = inputs.SourceVideoName ?? throw new RenderValidationException("The deflicker pass needs a source clip, but none was provided.") }),
            [Nodes.Components] = ComfyGraph.Node(ComfyNodeTypes.GetVideoComponents, new { video = ComfyGraph.Ref(Nodes.Source, 0) }),
            [Nodes.Deflicker] = ComfyGraph.Node(ComfyNodeTypes.DeflickerAuto, new
            {
                image = ComfyGraph.Ref(Nodes.Components, 0),
                mad_k = p.DblReq(WorkflowParamKeys.MadK),
                min_dev = p.DblReq(WorkflowParamKeys.MinDev),
                alpha_cut = p.DblReq(WorkflowParamKeys.AlphaCut),
                time_sigma = p.DblReq(WorkflowParamKeys.TimeSigma),
            }),
        };
        // Keep the source clip's frame rate (GetVideoComponents output 2). lossless so downstream stages see the
        // corrected frames verbatim (this is a preprocessing pass, not the final sprite).
        wf[Nodes.Save] = ComfyGraph.Node(ComfyNodeTypes.SaveAnimatedWEBP, new { images = ComfyGraph.Ref(Nodes.Deflicker, 0), filename_prefix = "forgemcp_edit", fps = ComfyGraph.Ref(Nodes.Components, 2), lossless = true, quality = 100, method = "default" });
        return wf;
    }
}
