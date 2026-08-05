using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;

namespace ImageGen.Comfy;

/// <summary>
/// Model-free image UPSCALER — a single feed-forward pass through an ESRGAN-family super-resolution network
/// (<c>UpscaleModelLoader</c> → <c>ImageUpscaleWithModel</c>, both ComfyUI core). No diffusion, no checkpoint, no
/// prompt, no seed: the network is deterministic, so the same source always yields the same output. It resolves
/// existing detail rather than inventing it — nothing is hallucinated, which is exactly why it is the safe default
/// for finishing an image whose content is already correct.
///
/// Model-agnostic: the network arrives as the <c>upscale_model</c> configuration parameter (the on-disk filename in
/// <c>models/upscale_models</c>, presence-gated through the config's <c>extra</c> requirement link), the same way
/// <see cref="HunyuanSr"/> takes its <c>sr_upsampler</c>. So a new upscaler is a config, not graph code — the anime
/// (PLKSR 2x) and photo (DAT2 4x) editors both bind here and differ only in weight and scale.
///
/// The network has ONE fixed factor (<c>model_scale</c>): 2x nets emit 2x, 4x nets emit 4x, always. To offer any
/// other factor we run the net and then resample its output by <c>scale / model_scale</c> — the standard
/// upscale-then-fit. That ratio is ≤ 1 for every scale below the native factor, so the resample is a DOWNSCALE of a
/// super-resolved image (sharp), never a plain stretch of the source (soft). At <c>scale == model_scale</c> the
/// resample node is omitted entirely rather than emitted as a no-op 1.0.
///
/// The edit path carries an instruction and an optional negative; both are IGNORED here (there is no text encoder to
/// feed them to). Exempt from the no-change gate via <see cref="PreservesComposition"/>: an upscale is a
/// resolution change, and every pixel of the composition is meant to survive it.
/// </summary>
public sealed class UpscaleWorkflow : EditWorkflow<UpscaleParams>
{
    public override string Name => "upscale-model";


    /// <summary>No checkpoint — the upscale network is loaded by its own node from its own folder.</summary>
    public override bool RequiresModel => false;

    /// <summary>No text encoder in the graph — the editor hides its instruction box.</summary>
    public override bool TakesPrompt => false;

    /// <summary>An upscale preserves the composition exactly — exempt from the no-change gate.</summary>
    public override bool PreservesComposition => true;

    /// <summary>The shared schema is all diffusion knobs (steps/cfg/sampler/denoise); none of them apply to a
    /// feed-forward SR net. Declare the two that do.</summary>
    public override IReadOnlyList<ParamSpec> Schema => UpscaleSchema;
    private static readonly IReadOnlyList<ParamSpec> UpscaleSchema = new ParamSpec[]
    {
        // The on-disk filename in models/upscale_models. Locked per config — the choice of network IS the editor.
        new() { Key = WorkflowParamKeys.UpscaleModel, Type = ParamType.String, IsModelRef = true },
        // The network's own fixed output factor, from its model card. Locked per config; used only as the divisor
        // that turns the requested scale into a resample ratio. Wrong value = a silently mis-sized result.
        new() { Key = WorkflowParamKeys.ModelScale,   Type = ParamType.Double, Min = 1.0, Max = 8.0 },
        // The factor the user actually wants, relative to the SOURCE. Each config narrows Max to its own
        // model_scale, so the slider never asks for more magnification than the network can produce.
        new() { Key = WorkflowParamKeys.Scale,         Type = ParamType.Int, Min = 1, Max = 4, Step = 1,
                Label = "Scale (×)", Help = "Output size relative to the source. Above the model's native factor the result is stretched, not resolved." },
        // Resampler for the fit-to-scale step. lanczos keeps the SR pass's sharpness on the way down.
        new() { Key = WorkflowParamKeys.Resample,      Type = ParamType.Enum,
                Choices = new[] { "lanczos", "bicubic", "bilinear", "area", "nearest-exact" } },
    };

    /// <summary>This workflow's own node ids (source LoadImage reuses the inherited <c>Nodes.Source</c>).</summary>
    private const string UpscaleModel = "20";
    private const string Upscale = "21";
    private const string Resample = "22";
    private const string Save = "9";

    protected override ComfyWorkflowGraph Build(UpscaleParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        // Node ids stay clear of the shared edit head (3/4/5/6/8/9/10/13/14) — only LoadImage "10" and SaveImage "9"
        // are reused, since the edit save path keys off the "forgemcp_edit" prefix.
        string source = inputs.SourceImageName ?? throw new RenderValidationException("The upscaler needs a source image, but none was provided.");
        ComfyWorkflowGraph g = new ComfyWorkflowGraph
        {
            [Nodes.Source] = new LoadImage { Image = source },
            [UpscaleModel] = new UpscaleModelLoader { ModelName = p.UpscaleModel },
        };
        g[Upscale] = new ImageUpscaleWithModel
        {
            UpscaleModel = UpscaleModelLoader.Out(UpscaleModel),
            Image = LoadImage.ImageOut(Nodes.Source),
        };
        Output<Slot.Image> outImage = ImageUpscaleWithModel.Out(Upscale);

        // Fit the net's fixed-factor output to the requested scale. A model_scale of 0 (config typo) would divide by
        // zero, so fall back to "the net's output is already what was asked for" and emit no resample.
        double modelScale = p.ModelScale;
        double scale = p.Scale;
        if (modelScale > 0 && scale > 0)
        {
            double ratio = scale / modelScale;
            if (Math.Abs(ratio - 1.0) > 0.001)   // exactly native → the SR output IS the answer, no resample node
            {
                g[Resample] = new ImageScaleBy { Image = outImage, UpscaleMethod = p.Resample, ScaleBy = ratio };
                outImage = ImageScaleBy.Out(Resample);
            }
        }

        g[Save] = new SaveImage { Images = outImage, FilenamePrefix = "forgemcp_edit" };
        return g;
    }
}

/// <summary>Upscaler parameters. <c>model_scale</c>/<c>scale</c> were read as doubles (the ratio math); the model-ref
/// filename and the resampler are <c>required</c> (the old Model()/StrReq reads throw on absent).</summary>
public sealed record UpscaleParams
{
    [JsonPropertyName(WorkflowParamKeys.UpscaleModel)] public required string UpscaleModel { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ModelScale)]   public required double ModelScale { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Scale)]        public required double Scale { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Resample)]     public required string Resample { get; init; }
}
