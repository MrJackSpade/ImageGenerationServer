using ImageGen.Application.Rendering;

namespace ImageGen.Comfy;

/// <summary>
/// Pixelizer on DreamOmni2. DreamOmni2 runs its whole diffusion inside the self-contained
/// <c>RunningHub DreamOmni2 Editor</c> node (a quanto-int8 FLUX.1-Kontext pipeline + a VLM), so the
/// per-step projection is done INSIDE that node: it carries <c>pixel_art</c> options that
/// project the flow-matching x0 estimate onto the grid+palette every step (same math as
/// <c>PixelManifoldProjection</c>, via PixelHarness <c>quant</c>). A final <c>PixelQuantize</c> renders the
/// authoritative output. <see cref="RequiresModel"/> = false (the pipeline loads its own weights).
/// </summary>
public sealed class DreamOmni2PixelizeWorkflow : EditWorkflowBase
{
    public override string Name => "pixelize-dreamomni2";
    public override bool PreservesComposition => true;
    public override bool RequiresModel => false;
    /// <summary>The editor loads its own int8 weights (no linked checkpoint → no resolved resolution), so the render
    /// snap uses the FLUX.1-Kontext-class envelope (256–1440, /16) it's built on.</summary>
    public override ModelResolution? ResolutionEnvelope => new() { MinW = 256, MinH = 256, MaxW = 1440, MaxH = 1440, Step = 16 };
    public override IReadOnlyList<ParamSpec> Schema => PixelizeSchema.DreamOmniLike(PixelizeSchema.DefaultPixelPrompt);

    /// <summary>This subclass's own node ids (the source LoadImage reuses EditWorkflowBase.Nodes.Source).</summary>
    private const string Reference = "11";
    private const string Pipeline = "1";
    private const string Editor = "2";
    private const string FinalQuantize = "36";
    private const string Save = "9";

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>
        {
            [Nodes.Source] = ComfyGraph.Node(ComfyNodeTypes.LoadImage, new { image = inputs.SourceImageName ?? throw new RenderValidationException("The pixel quantizer needs a source image, but none was provided.") }),
        };
        object refImg;
        var refNames = inputs.ReferenceImageNames;
        if (refNames.Count > 0) { wf[Reference] = ComfyGraph.Node(ComfyNodeTypes.LoadImage, new { image = refNames[0] }); refImg = ComfyGraph.Ref(Reference, 0); }
        else refImg = ComfyGraph.Ref(Nodes.Source, 0);   // Editor requires a reference; the source doubles as its own.

        var instruction = p.Str(WorkflowParamKeys.StylePrompt);
        if (string.IsNullOrWhiteSpace(instruction)) instruction = inputs.Positive;
        int gw = p.IntReq(WorkflowParamKeys.GridW);
        int gh = p.IntReq(WorkflowParamKeys.GridH);
        var palette = p.StrReq(WorkflowParamKeys.Palette);
        int vres = p.IntReq(WorkflowParamKeys.VirtualResolution);

        // The config links no checkpoint (the editor loads its own int8 weights), so there's no resolved Resolution.
        // DreamOmni2 is a FLUX.1-Kontext-class pipeline, so snap against the Kontext envelope (256-1440, /16). The
        // render size is fed to the editor as render_width/height, overriding its internal aspect-bucket resize.
        var snap = PixelSnap.Target(p, new ModelResolution { MinW = 256, MinH = 256, MaxW = 1440, MaxH = 1440, Step = 16 }, vres, inputs.SourceWidth, inputs.SourceHeight);

        wf[Pipeline] = ComfyGraph.Node(ComfyNodeTypes.RunningHubDreamOmni2EditPipeline, new { });
        wf[Editor] = ComfyGraph.Node(ComfyNodeTypes.RunningHubDreamOmni2Editor, new
        {
            pipeline = ComfyGraph.Ref(Pipeline, 0),
            src_image = ComfyGraph.Ref(Nodes.Source, 0),
            ref_image = refImg,
            prompt = instruction,
            num_inference_steps = p.IntReq(WorkflowParamKeys.Steps),
            guidance_scale = p.DblReq(WorkflowParamKeys.Cfg),
            seed = ComfyGraph.Seed(p),
            // per-step pixel-art projection inside the pipeline (the node modification)
            pixel_art = true,
            grid_w = gw,
            grid_h = gh,
            palette,
            proj_method = p.StrReq(WorkflowParamKeys.ProjMethod),
            virtual_resolution = vres,
            w_start = p.DblReq(WorkflowParamKeys.WStart),
            w_end = p.DblReq(WorkflowParamKeys.WEnd),
            proj_start = p.DblReq(WorkflowParamKeys.StartPercent),
            proj_end = p.DblReq(WorkflowParamKeys.EndPercent),
            project_every = p.IntReq(WorkflowParamKeys.ProjectEvery),
            // 0 when snapping is off / no width+height given -> the node keeps its own aspect-bucket size
            render_width = snap?.w ?? 0,
            render_height = snap?.h ?? 0,
            // reference% -> img2img strength inside the pipeline; 1.0 (reference 0, default) == full generation
            strength = PixelSnap.Denoise(p, 0),
        });
        wf[FinalQuantize] = PixelizeSchema.FinalQuantize(ComfyGraph.Ref(Editor, 0), gw, gh, palette, vres, p);
        wf[Save] = ComfyGraph.Node(ComfyNodeTypes.SaveImage, new { images = ComfyGraph.Ref(FinalQuantize, 0), filename_prefix = "forgemcp_edit" });
        return wf;
    }
}
