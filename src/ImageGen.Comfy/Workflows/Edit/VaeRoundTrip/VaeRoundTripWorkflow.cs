using ImageGen.Application.Rendering;
using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Comfy.Edit.VaeRoundTrip;

/// <summary>
/// Isolated VAE reconstruction diagnostic: source pixels go directly through encode/decode with no resize,
/// checkpoint, text encoder, conditioning, noise, or sampler. A configuration supplies the VAE requirement, making
/// this graph reusable for any separately-loadable VAE while keeping each machine's file binding in the catalog.
/// </summary>
public sealed class VaeRoundTripWorkflow : Workflow<VaeRoundTripParams>
{
    private const int ExactGridStep = 32;

    public override string Name => "vae-roundtrip";
    public override WorkflowKind Kind => WorkflowKind.Edit;
    public override WorkflowMedia Media => WorkflowMedia.Image;
    public override bool PromptDirectsMotion => false;
    public override bool PreservesComposition => true;
    public override bool RequiresModel => false;
    public override bool TakesPrompt => false;
    public override IReadOnlyList<ParamSpec> Schema => [];

    /// <summary>The exact-source grid. This graph is VAE-agnostic, so the pad must land on every bound VAE's
    /// compression grid at once: 32 is a multiple of the 8× and 16× spatial compression the catalogue's VAEs use.
    /// Without the request option the source goes to <c>VAEEncode</c> untouched, which centre-crops an off-grid
    /// image and returns a smaller one.</summary>
    [AllowNullable("the exact-source grid step; null on the base means the workflow has no exact-source path")]
    public override int? ExactSourceStep => ExactGridStep;

    protected override ComfyWorkflowGraph Build(VaeRoundTripParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        string source = inputs.SourceImageName
            ?? throw new RenderValidationException("The VAE round-trip diagnostic needs a source image.");
        ComfyWorkflowGraph graph = new()
        {
            [Nodes.Source] = new LoadImage { Image = source },
            [Nodes.Vae] = new VAELoader { VaeName = req.RequiredVae() },
        };

        (int Width, int Height) sourceSize = (inputs.SourceWidth, inputs.SourceHeight);
        (int Width, int Height) paddedSize = (inputs.TargetWidth, inputs.TargetHeight);
        Output<Slot.Image> pixels = inputs.ExactSourceResolution
            ? ExactSourceCanvas.Pad(graph, Nodes.ExactPad, LoadImage.ImageOut(Nodes.Source), sourceSize, paddedSize)
            : LoadImage.ImageOut(Nodes.Source);
        graph[Nodes.Encode] = new VAEEncode
        {
            Pixels = pixels,
            Vae = VAELoader.VaeOut(Nodes.Vae),
        };
        graph[Nodes.Decode] = new VAEDecode
        {
            Samples = VAEEncode.Out(Nodes.Encode),
            Vae = VAELoader.VaeOut(Nodes.Vae),
        };
        Output<Slot.Image> output = inputs.ExactSourceResolution
            ? ExactSourceCanvas.Crop(graph, Nodes.ExactCrop, VAEDecode.Out(Nodes.Decode), sourceSize, paddedSize)
            : VAEDecode.Out(Nodes.Decode);
        graph[Nodes.Save] = new SaveImage
        {
            Images = output,
            FilenamePrefix = OutputPrefixes.Edit,
        };
        return graph;
    }

    private static class Nodes
    {
        public const string Source = "10";
        public const string Vae = "20";
        public const string Encode = "21";
        public const string Decode = "22";
        public const string Save = "9";
        public const string ExactPad = "23";
        public const string ExactCrop = "24";
    }
}

/// <summary>No knobs by design: changing anything between source and encode would invalidate the control.</summary>
public sealed record VaeRoundTripParams;
