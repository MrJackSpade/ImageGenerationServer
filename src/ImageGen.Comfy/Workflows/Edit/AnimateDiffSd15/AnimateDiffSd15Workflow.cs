using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy.Edit.AnimateDiffSd15;

/// <summary>SD1.5 AnimateDiff + SparseCtrl-RGB: the source conditions frame 0 (faithful anime i2v).</summary>
public sealed class AnimateDiffSd15Workflow : EditWorkflow<AnimateDiffSd15Params>
{
    public override string Name => "animatediff-sd15";
    public override WorkflowMedia Media => WorkflowMedia.Video;
    /// <summary>AnimateDiff: prompt sets the scene, motion is generic.</summary>
    public override bool PromptDirectsMotion => false;

    protected override ComfyWorkflowGraph Build(AnimateDiffSd15Params p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new ComfyWorkflowGraph();
        LoadModel(g, p.Loader, p.WeightDtype, p.ClipType, req, inputs, out Output<Slot.Model> model0, out Output<Slot.Clip> clip0, out Output<Slot.Vae> vae0);
        long seed = ComfyGraph.Seed(p.Seed);
        int frames = p.Length;
        double fps = p.Fps;
        double budgetMp = 0.26;   // SD1.5 AnimateDiff's native i2v megapixel budget — always applied (the source is scaled to it)
        string mm = p.MotionModel;
        string beta = p.BetaSchedule;
        g[Nodes.ScaledSource] = new ImageScaleToTotalPixels { Image = LoadImage.ImageOut(EditNodes.Source), UpscaleMethod = ComfyWidgets.Upscale.Lanczos, Megapixels = budgetMp, ResolutionSteps = 64 };
        g[Nodes.SourceSize] = new GetImageSize { Image = ImageScaleToTotalPixels.Out(Nodes.ScaledSource) };
        g[Nodes.MotionLoad] = new ADE_LoadAnimateDiffModel { ModelName = mm };
        g[Nodes.MotionApply] = new ADE_ApplyAnimateDiffModelSimple { MotionModel = ADE_LoadAnimateDiffModel.Out(Nodes.MotionLoad) };
        g[Nodes.EvolvedSampling] = new ADE_UseEvolvedSampling { Model = model0, BetaSchedule = beta, MModels = ADE_ApplyAnimateDiffModelSimple.Out(Nodes.MotionApply) };
        g[Nodes.Positive] = new CLIPTextEncode { Text = inputs.Positive, Clip = clip0 };
        g[Nodes.Negative] = new CLIPTextEncode { Text = inputs.Negative ?? "", Clip = clip0 };
        g[Nodes.Latent] = new EmptyLatentImageSized { Width = GetImageSize.WidthOut(Nodes.SourceSize), Height = GetImageSize.HeightOut(Nodes.SourceSize), BatchSize = frames };
        g[Nodes.SparseCtrlLoader] = new ACN_SparseCtrlLoaderAdvanced { SparsectrlName = p.SparsectrlName, UseMotion = true, MotionStrength = 1.0, MotionScale = 1.0 };
        g[Nodes.SparseCtrlPreprocess] = new ACN_SparseCtrlRGBPreprocessor { Image = ImageScaleToTotalPixels.Out(Nodes.ScaledSource), Vae = vae0, LatentSize = EmptyLatentImageSized.Out(Nodes.Latent) };
        g[Nodes.ControlNetApply] = new ControlNetApplyAdvanced { Positive = CLIPTextEncode.Out(Nodes.Positive), Negative = CLIPTextEncode.Out(Nodes.Negative), ControlNet = ACN_SparseCtrlLoaderAdvanced.Out(Nodes.SparseCtrlLoader), Image = ACN_SparseCtrlRGBPreprocessor.Out(Nodes.SparseCtrlPreprocess), Strength = 1.0, StartPercent = 0.0, EndPercent = 1.0, Vae = vae0 };
        g[Nodes.Sampler] = new KSampler { Seed = seed, Steps = p.Steps, Cfg = p.Cfg, SamplerName = ComfyGraph.MapSampler(p.Sampler), Scheduler = ComfyGraph.MapScheduler(p.Scheduler), Denoise = 1.0, Model = ADE_UseEvolvedSampling.Out(Nodes.EvolvedSampling), Positive = ControlNetApplyAdvanced.PositiveOut(Nodes.ControlNetApply), Negative = ControlNetApplyAdvanced.NegativeOut(Nodes.ControlNetApply), LatentImage = EmptyLatentImageSized.Out(Nodes.Latent) };
        g[Nodes.Decode] = new VAEDecode { Samples = KSampler.Out(Nodes.Sampler), Vae = vae0 };
        g[Nodes.Save] = new SaveAnimatedWEBPLiteralFps { Images = VAEDecode.Out(Nodes.Decode), FilenamePrefix = OutputPrefixes.Edit, Fps = fps, Lossless = false, Quality = 80, Method = ComfyWidgets.WebpMethod.Default };
        return g;
    }
}
