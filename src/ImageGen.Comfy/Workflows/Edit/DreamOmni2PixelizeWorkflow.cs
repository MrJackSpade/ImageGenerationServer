using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;
using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Comfy;

/// <summary>
/// Pixelizer on DreamOmni2. DreamOmni2 runs its whole diffusion inside the self-contained
/// <c>RunningHub DreamOmni2 Editor</c> node (a quanto-int8 FLUX.1-Kontext pipeline + a VLM), so the
/// per-step projection is done INSIDE that node: it carries <c>pixel_art</c> options that
/// project the flow-matching x0 estimate onto the grid+palette every step (same math as
/// <c>PixelManifoldProjection</c>, via PixelHarness <c>quant</c>). A final <c>PixelQuantize</c> renders the
/// authoritative output. <see cref="RequiresModel"/> = false (the pipeline loads its own weights).
/// </summary>
public sealed class DreamOmni2PixelizeWorkflow : EditWorkflow<DreamOmni2PixelizeParams>
{
    public override string Name => "pixelize-dreamomni2";
    public override bool PreservesComposition => true;
    public override bool RequiresModel => false;
    /// <summary>The editor loads its own int8 weights (no linked checkpoint → no resolved resolution), so the render
    /// snap uses the FLUX.1-Kontext-class envelope (256–1440, /16) it's built on.</summary>
    public override ModelResolution? ResolutionEnvelope => new() { MinW = 256, MinH = 256, MaxW = 1440, MaxH = 1440, Step = 16 };
    public override IReadOnlyList<ParamSpec> Schema => PixelizeSchema.DreamOmniLike();

    protected override ComfyWorkflowGraph Build(DreamOmni2PixelizeParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new ComfyWorkflowGraph
        {
            [EditNodes.Source] = new LoadImage { Image = inputs.SourceImageName ?? throw new RenderValidationException("The pixel quantizer needs a source image, but none was provided.") },
        };
        Output<Slot.Image> refImg;
        IReadOnlyList<string> refNames = inputs.ReferenceImageNames;
        if (refNames.Count > 0) { g[Nodes.Reference] = new LoadImage { Image = refNames[0] }; refImg = LoadImage.ImageOut(Nodes.Reference); }
        else refImg = LoadImage.ImageOut(EditNodes.Source);   // Editor requires a reference; the source doubles as its own.

        string instruction = string.IsNullOrWhiteSpace(p.StylePrompt) ? inputs.Positive : p.StylePrompt;
        int gw = p.GridW;
        int gh = p.GridH;
        string palette = p.Palette;
        int vres = p.VirtualResolution;

        // The config links no checkpoint (the editor loads its own int8 weights), so there's no resolved Resolution.
        // DreamOmni2 is a FLUX.1-Kontext-class pipeline, so snap against the Kontext envelope (256-1440, /16). The
        // render size is fed to the editor as render_width/height, overriding its internal aspect-bucket resize.
        (int w, int h)? snap = PixelSnap.Target(new ModelResolution { MinW = 256, MinH = 256, MaxW = 1440, MaxH = 1440, Step = 16 }, vres, p.SnapResolution, p.Width, p.Height, inputs.SourceWidth, inputs.SourceHeight);

        g[Nodes.Pipeline] = new RunningHubDreamOmni2EditPipeline();
        g[Nodes.Editor] = new RunningHubDreamOmni2PixelizeEditor
        {
            Pipeline = RunningHubDreamOmni2EditPipeline.Out(Nodes.Pipeline),
            SrcImage = LoadImage.ImageOut(EditNodes.Source),
            RefImage = refImg,
            Prompt = instruction,
            NumInferenceSteps = p.Steps,
            GuidanceScale = p.Cfg,
            Seed = ComfyGraph.Seed(p.Seed),
            // per-step pixel-art projection inside the pipeline (the node modification)
            PixelArt = true,
            GridW = gw,
            GridH = gh,
            Palette = palette,
            ProjMethod = p.ProjMethod,
            VirtualResolution = vres,
            WStart = p.WStart,
            WEnd = p.WEnd,
            ProjStart = p.StartPercent,
            ProjEnd = p.EndPercent,
            ProjectEvery = p.ProjectEvery,
            // 0 when snapping is off / no width+height given -> the node keeps its own aspect-bucket size
            RenderWidth = snap?.w ?? 0,
            RenderHeight = snap?.h ?? 0,
            // reference% -> img2img strength inside the pipeline; 1.0 (reference 0, default) == full generation
            Strength = PixelSnap.Denoise(p.Reference, 0),
        };
        g[Nodes.FinalQuantize] = PixelizeSchema.FinalQuantize(RunningHubDreamOmni2PixelizeEditor.Out(Nodes.Editor), gw, gh, palette, vres, p.FinalMethod);
        g[Nodes.Save] = new SaveImage { Images = PixelQuantize.Out(Nodes.FinalQuantize), FilenamePrefix = OutputPrefixes.Edit };
        return g;
    }
}

/// <summary>DreamOmni2PixelizeWorkflow's node ids (the source LoadImage reuses <c>EditNodes.Source</c>).</summary>
file static class Nodes
{
    public const string Reference = "11";
    public const string Pipeline = "1";
    public const string Editor = "2";
    public const string FinalQuantize = "36";
    public const string Save = "9";
}

/// <summary>DreamOmni2 pixelizer parameters — the two diffusion knobs the self-contained editor consumes
/// (<c>steps</c>/<c>cfg</c>, both <c>required</c>), the grid/palette/virtual-resolution + the projection ramp it runs
/// internally, and the <c>reference</c> %% (read via the img2img-strength map, so a defaulted int). <c>style_prompt</c>
/// is a nullable string; <c>width</c>/<c>height</c> are defaulted ints, <c>snap_resolution</c> a defaulted bool;
/// <c>seed</c> is the app's single-sourced seed (there is no <c>LoadModel</c> head — the editor loads its own weights).</summary>
public sealed record DreamOmni2PixelizeParams
{
    [JsonPropertyName(WorkflowParamKeys.Steps)]
    [Range(ParamBounds.StepsMin, ParamBounds.StepsMax)]     public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Cfg)]
    [Range(ParamBounds.CfgMin, ParamBounds.CfgMax)]         public required double Cfg { get; init; }
    [JsonPropertyName(WorkflowParamKeys.StylePrompt)]       public string? StylePrompt { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Reference)]
    [AllowNullable("null = the config didn't set the reference %; read via the img2img-strength map only when present, distinct from a real 0% (full generation)")]
    [Range(0, 100)]                                         public int? Reference { get; init; }
    [JsonPropertyName(WorkflowParamKeys.VirtualResolution)]
    [Range(0, 4096)]                                        public required int VirtualResolution { get; init; }
    [JsonPropertyName(WorkflowParamKeys.GridW)]
    [Range(0, 4096)]                                        public required int GridW { get; init; }
    [JsonPropertyName(WorkflowParamKeys.GridH)]
    [Range(0, 4096)]                                        public required int GridH { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Width)]
    [Range(0, 4096)]                                        public int Width { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Height)]
    [Range(0, 4096)]                                        public int Height { get; init; }
    [JsonPropertyName(WorkflowParamKeys.SnapResolution)]    public bool SnapResolution { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Palette)]           public required string Palette { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ProjMethod)]        public required string ProjMethod { get; init; }
    [JsonPropertyName(WorkflowParamKeys.FinalMethod)]       public required string FinalMethod { get; init; }
    [JsonPropertyName(WorkflowParamKeys.WStart)]
    [Range(0.0, 1.0)]                                       public required double WStart { get; init; }
    [JsonPropertyName(WorkflowParamKeys.WEnd)]
    [Range(0.0, 1.0)]                                       public required double WEnd { get; init; }
    [JsonPropertyName(WorkflowParamKeys.StartPercent)]
    [Range(0.0, 1.0)]                                       public required double StartPercent { get; init; }
    [JsonPropertyName(WorkflowParamKeys.EndPercent)]
    [Range(0.0, 1.0)]                                       public required double EndPercent { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ProjectEvery)]
    [Range(1, 8)]                                           public required int ProjectEvery { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)]              public long Seed { get; init; }
}
