using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
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
public sealed class DeflickerAutoVideoWorkflow : Workflow<DeflickerAutoParams>
{
    public override string Name => "deflicker-auto";
    public override WorkflowKind Kind => WorkflowKind.Edit;
    public override WorkflowMedia Media => WorkflowMedia.Video;
    public override WorkflowMedia SourceMedia => WorkflowMedia.Video;
    public override bool PromptDirectsMotion => false;
    public override bool PreservesComposition => true;
    public override bool RequiresModel => false;
    public override IReadOnlyList<ParamSpec> Schema => DeflickerSchema;

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

    protected override ComfyWorkflowGraph Build(DeflickerAutoParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        string source = inputs.SourceVideoName
            ?? throw new RenderValidationException("The deflicker pass needs a source clip, but none was provided.");
        return new ComfyWorkflowGraph
        {
            [Nodes.Source] = new LoadVideo { File = source },
            [Nodes.Components] = new GetVideoComponents { Video = LoadVideo.VideoOut(Nodes.Source) },
            [Nodes.Deflicker] = new DeflickerAuto
            {
                Image = GetVideoComponents.ImagesOut(Nodes.Components),
                MadK = p.MadK,
                MinDev = p.MinDev,
                AlphaCut = p.AlphaCut,
                TimeSigma = p.TimeSigma,
            },
            // Keep the source clip's frame rate (GetVideoComponents output 2). lossless so downstream stages see the
            // corrected frames verbatim (this is a preprocessing pass, not the final sprite).
            [Nodes.Save] = new SaveAnimatedWEBP
            {
                Images = DeflickerAuto.ImageOut(Nodes.Deflicker),
                FilenamePrefix = OutputPrefixes.Edit,
                Fps = GetVideoComponents.FpsOut(Nodes.Components),
                Lossless = true,
                Quality = 100,
                Method = ComfyWidgets.WebpMethod.Default,
            },
        };
    }
}

/// <summary>The deflicker pass's parameters, deserialized from the merged bag before <c>Build</c> — all four the robust
/// flag/correct thresholds. <c>required</c> so an absent value throws at the deserializer (the declarative form of the
/// previous <c>DblReq</c> reads).</summary>
public sealed record DeflickerAutoParams
{
    [JsonPropertyName(WorkflowParamKeys.MadK)]
    [Range(0.5, 20.0)]                              public required double MadK { get; init; }
    [JsonPropertyName(WorkflowParamKeys.MinDev)]
    [Range(0.0, 16.0)]                              public required double MinDev { get; init; }
    [JsonPropertyName(WorkflowParamKeys.AlphaCut)]
    [Range(0.0, 1.0)]                               public required double AlphaCut { get; init; }
    [JsonPropertyName(WorkflowParamKeys.TimeSigma)]
    [Range(0.1, 32.0)]                              public required double TimeSigma { get; init; }
}
