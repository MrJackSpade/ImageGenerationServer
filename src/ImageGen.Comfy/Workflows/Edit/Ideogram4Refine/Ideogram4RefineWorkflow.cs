using ImageGen.Comfy.Generation.Ideogram4;
using ImageGen.Application.Rendering;
using ImageGen.Domain;

namespace ImageGen.Comfy.Edit.Ideogram4Refine;

/// <summary>
/// Whole-image Ideogram 4 refine: encode an existing image into the Flux2 latent space, then sample only the chosen
/// low-sigma tail of Ideogram's native schedule. This preserves the source composition at low strength while using
/// Ideogram's typography and prompt adherence to rework the frame. The guidance path intentionally matches the
/// text-to-image workflow: correction on the conditional model only, late CFG override, then dual-model guidance
/// against the dedicated unconditional UNet.
/// </summary>
public sealed class Ideogram4RefineWorkflow : EditWorkflow<Ideogram4RefineParams>
{
    public override bool NormalizesSourceResolution => true;
    public override bool SupportsEditQuality => true;
    public override string Name => "ideogram4-refine";
    public override bool PreservesComposition => true;
    public override PromptSemantics PromptSemantics => PromptSemantics.WholeImage;

    public override IReadOnlyList<ParamSpec> Schema => RefineSchema;
    private static readonly IReadOnlyList<ParamSpec> RefineSchema =
    [
        new() { Key = WorkflowParamKeys.Steps, Type = ParamType.Int, Min = ParamBounds.StepsMin, Max = ParamBounds.StepsMax, Label = "Steps", EtaVariable = true },
        new() { Key = WorkflowParamKeys.Cfg, Type = ParamType.Double, Min = ParamBounds.CfgMin, Max = ParamBounds.CfgMax, Label = "CFG scale" },
        new() { Key = WorkflowParamKeys.CfgOverride, Type = ParamType.Double, Min = 1, Max = 30, Label = "Late-step CFG" },
        new() { Key = WorkflowParamKeys.Mu, Type = ParamType.Double, Min = -10, Max = 10, Label = "Schedule shift (mu)" },
        new() { Key = WorkflowParamKeys.Std, Type = ParamType.Double, Min = 0.1, Max = 5, Label = "Schedule spread (std)" },
        new() { Key = WorkflowParamKeys.Sampler, Type = ParamType.String, Label = "Sampler" },
        new() { Key = WorkflowParamKeys.Denoise, Type = ParamType.Double, Min = ParamBounds.DenoiseMin, Max = ParamBounds.DenoiseMax, Step = 0.01,
                Label = "Refine strength", Help = "How far Ideogram may redraw the source. Lower values preserve more; higher values follow the prompt more strongly." },
        new() { Key = WorkflowParamKeys.DebannerStrength, Type = ParamType.Double, Min = 0, Max = 2, Step = 0.01, Label = "Debanner Strength" },
        .. SeedParam.Schema,
        PromptTemplates.Schema,
        new() { Key = WorkflowParamKeys.NegativeSupported, Type = ParamType.Bool },
        new() { Key = WorkflowParamKeys.NativePixels, Type = ParamType.Int, Min = 1, Label = "Native pixel budget" },
        new() { Key = WorkflowParamKeys.MaxDimension, Type = ParamType.Int, Min = 0, Max = 4096, Label = "Max long edge (px)" },
    ];

    private static double NativeMegapixels(Ideogram4RefineParams p) => p.NativePixels / (1024.0 * 1024.0);

    protected override (int Width, int Height) EtaRenderSize(
        Ideogram4RefineParams p,
        ResolvedRequirements req,
        int sourceWidth,
        int sourceHeight,
        double? editMegapixels) =>
        EditWorkingResolution.Resolve(
            sourceWidth,
            sourceHeight,
            editMegapixels ?? NativeMegapixels(p),
            maxDimension: p.MaxDimension);

    protected override ComfyWorkflowGraph Build(Ideogram4RefineParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        (int Width, int Height) current = (
            Ensure.GreaterThanZero(inputs.SourceWidth),
            Ensure.GreaterThanZero(inputs.SourceHeight));
        (int Width, int Height) target = EditWorkingResolution.Resolve(
            current.Width,
            current.Height,
            inputs.EditMegapixels ?? NativeMegapixels(p),
            maxDimension: p.MaxDimension);
        ComfyWorkflowGraph g = new();

        g[EditNodes.Model] = ComfyGraph.DiffusionLoaderNode(req.RequiredCheckpoint());
        g[Nodes.UncondModel] = ComfyGraph.DiffusionLoaderNode(req.RequiredMotionModel());
        g[Nodes.Debanner] = new Ideogram4CorrectionPatch
        {
            Model = UNETLoader.ModelOut(EditNodes.Model),
            Enabled = p.DebannerStrength != 0,
            Strength = p.DebannerStrength,
        };
        g[EditNodes.Clip] = new CLIPLoader { ClipName = req.TextEncoder(0), Type = ComfyWidgets.ClipType.Ideogram4, Device = ComfyWidgets.Device.Default };
        g[EditNodes.Vae] = new VAELoader { VaeName = req.RequiredVae() };
        g[EditNodes.Source] = new LoadImage { Image = inputs.SourceImageName ?? throw new RenderValidationException("This refine needs a source image, but none was provided.") };

        g[Nodes.Positive] = new CLIPTextEncode { Text = inputs.Positive, Clip = CLIPLoader.ClipOut(EditNodes.Clip) };
        g[Nodes.NegativeZeroOut] = new ConditioningZeroOut { Conditioning = CLIPTextEncode.Out(Nodes.Positive) };
        g[Nodes.CfgOverride] = new CFGOverride
        {
            Model = Ideogram4CorrectionPatch.Out(Nodes.Debanner),
            Cfg = p.CfgOverride,
            StartPercent = 0.7,
            EndPercent = 1.0,
        };
        g[Nodes.Guider] = new DualModelGuider
        {
            Model = CFGOverride.Out(Nodes.CfgOverride),
            Positive = CLIPTextEncode.Out(Nodes.Positive),
            ModelNegative = UNETLoader.ModelOut(Nodes.UncondModel),
            Negative = ConditioningZeroOut.Out(Nodes.NegativeZeroOut),
            Cfg = p.Cfg,
        };

        Output<Slot.Image> encPixels = EditWorkingResolution.ScaleImage(
            g,
            Nodes.SourceScale,
            LoadImage.ImageOut(EditNodes.Source),
            current,
            target);
        g[Nodes.Encode] = new VAEEncode { Pixels = encPixels, Vae = VAELoader.VaeOut(EditNodes.Vae) };
        // Read the normalized image itself so the resolution-aware scheduler cannot drift from the VAE input.
        g[Nodes.SourceSize] = new GetImageSize { Image = encPixels };
        g[Nodes.Sigmas] = new Ideogram4SchedulerFromSize
        {
            Steps = p.Steps,
            Width = GetImageSize.WidthOut(Nodes.SourceSize),
            Height = GetImageSize.HeightOut(Nodes.SourceSize),
            Mu = p.Mu,
            Std = p.Std,
        };
        g[Nodes.SplitSigmas] = new SplitSigmasDenoise { Sigmas = Ideogram4Scheduler.Out(Nodes.Sigmas), Denoise = p.Denoise };
        g[Nodes.SamplerSelect] = new KSamplerSelect { SamplerName = ComfyGraph.MapSampler(p.Sampler) };
        g[Nodes.Noise] = new RandomNoise { NoiseSeed = ComfyGraph.Seed(p.Seed) };
        g[Nodes.Sampler] = new SamplerCustomAdvanced
        {
            Noise = RandomNoise.Out(Nodes.Noise),
            Guider = DualModelGuider.Out(Nodes.Guider),
            Sampler = KSamplerSelect.Out(Nodes.SamplerSelect),
            Sigmas = SplitSigmasDenoise.LowOut(Nodes.SplitSigmas),
            LatentImage = VAEEncode.Out(Nodes.Encode),
        };

        g[Nodes.Decode] = new VAEDecode { Samples = SamplerCustomAdvanced.Out(Nodes.Sampler), Vae = VAELoader.VaeOut(EditNodes.Vae) };
        g[Nodes.Save] = new SaveImage { Images = VAEDecode.Out(Nodes.Decode), FilenamePrefix = OutputPrefixes.Edit };
        return g;
    }
}
