namespace ImageGen.Comfy.Edit.SdxlAnimateDiff;

/// <summary>SDXL AnimateDiff i2v via img2img motion. Uses BASE SDXL — the <c>mm_sdxl_v10_beta</c> motion module
/// learned its temporal priors against base SDXL's feature space, so heavily-finetuned SDXL derivatives
/// (Pony/AutismMix lineage) run but produce color-noise instead of motion.</summary>
public sealed class SdxlAnimateDiffWorkflow : EditWorkflow<SdxlAnimateDiffParams>
{
    public override bool NormalizesSourceResolution => true;
    public override string Name => "sdxl-i2v";
    public override WorkflowMedia Media => WorkflowMedia.Video;
    /// <summary>AnimateDiff: prompt sets the scene, motion is generic.</summary>
    public override bool PromptDirectsMotion => false;

    /// <summary>The shared edit menu plus the per-config i2v <c>megapixels</c> budget control (#186).</summary>
    public override IReadOnlyList<ParamSpec> Schema => [.. EditWorkflowBase.SharedSchema, VideoSizeSchema.Megapixels];

    /// <summary>SDXL AnimateDiff's i2v snap grid (64-px). The megapixel BUDGET is the per-config <c>megapixels</c>
    /// control (#186), read off the params record.</summary>
    private const int BudgetSteps = 64;

    protected override (double Megapixels, int ResolutionSteps)? EtaBudget(SdxlAnimateDiffParams p) => (p.Megapixels, BudgetSteps);

    protected override ComfyWorkflowGraph Build(SdxlAnimateDiffParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new();
        LoadModel(g, p.Loader, p.WeightDtype, p.ClipType, req, inputs, out Output<Slot.Model> model0, out Output<Slot.Clip> clip0, out Output<Slot.Vae> vae0);
        long seed = ComfyGraph.Seed(p.Seed);
        int frames = p.Length;
        double fps = p.Fps;
        double denoise = p.Denoise;
        double budgetMp = p.Megapixels;   // the per-config i2v megapixel budget (the source is scaled to it)
        string mm = p.MotionModel;
        string beta = p.BetaSchedule;
        g[Nodes.ScaleSource] = new ImageScaleToTotalPixels { Image = LoadImage.ImageOut(EditNodes.Source), UpscaleMethod = ComfyWidgets.Upscale.Lanczos, Megapixels = budgetMp, ResolutionSteps = BudgetSteps };
        g[Nodes.MotionLoad] = new ADE_LoadAnimateDiffModel { ModelName = mm };
        g[Nodes.ApplyMotion] = new ADE_ApplyAnimateDiffModelSimple { MotionModel = ADE_LoadAnimateDiffModel.Out(Nodes.MotionLoad) };
        g[Nodes.EvolvedSampling] = new ADE_UseEvolvedSampling { Model = model0, BetaSchedule = beta, MModels = ADE_ApplyAnimateDiffModelSimple.Out(Nodes.ApplyMotion) };
        g[Nodes.Positive] = new CLIPTextEncode { Text = inputs.Positive, Clip = clip0 };
        g[Nodes.Negative] = new CLIPTextEncode { Text = inputs.Negative ?? "", Clip = clip0 };
        g[Nodes.Encode] = new VAEEncode { Pixels = ImageScaleToTotalPixels.Out(Nodes.ScaleSource), Vae = vae0 };
        g[Nodes.RepeatLatent] = new RepeatLatentBatch { Samples = VAEEncode.Out(Nodes.Encode), Amount = frames };
        g[Nodes.Sampler] = new KSampler { Seed = seed, Steps = p.Steps, Cfg = p.Cfg, SamplerName = ComfyGraph.MapSampler(p.Sampler), Scheduler = ComfyGraph.MapScheduler(p.Scheduler), Denoise = denoise, Model = ADE_UseEvolvedSampling.Out(Nodes.EvolvedSampling), Positive = CLIPTextEncode.Out(Nodes.Positive), Negative = CLIPTextEncode.Out(Nodes.Negative), LatentImage = RepeatLatentBatch.Out(Nodes.RepeatLatent) };
        g[Nodes.Decode] = new VAEDecode { Samples = KSampler.Out(Nodes.Sampler), Vae = vae0 };
        g[Nodes.Save] = new SaveAnimatedWEBPLiteralFps { Images = VAEDecode.Out(Nodes.Decode), FilenamePrefix = OutputPrefixes.Edit, Fps = fps, Lossless = false, Quality = 80, Method = ComfyWidgets.WebpMethod.Default };
        return g;
    }
}
