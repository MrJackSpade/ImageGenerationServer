using ImageGen.Application.Rendering;

namespace ImageGen.Comfy.Edit.VaeRoundTrip;

/// <summary>
/// Isolated VAE reconstruction diagnostic: source pixels go directly through encode/decode with no resize,
/// checkpoint, text encoder, conditioning, noise, or sampler. A configuration supplies the VAE requirement, making
/// this graph reusable for any separately-loadable VAE while keeping each machine's file binding in the catalog.
/// </summary>
public sealed class VaeRoundTripWorkflow : Workflow<VaeRoundTripParams>
{
    public override string Name => "vae-roundtrip";
    public override WorkflowKind Kind => WorkflowKind.Edit;
    public override WorkflowMedia Media => WorkflowMedia.Image;
    public override bool PromptDirectsMotion => false;
    public override bool PreservesComposition => true;
    public override bool RequiresModel => false;
    public override bool TakesPrompt => false;
    public override IReadOnlyList<ParamSpec> Schema => [];

    protected override ComfyWorkflowGraph Build(VaeRoundTripParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        string source = inputs.SourceImageName
            ?? throw new RenderValidationException("The VAE round-trip diagnostic needs a source image.");
        return new ComfyWorkflowGraph
        {
            [Nodes.Source] = new LoadImage { Image = source },
            [Nodes.Vae] = new VAELoader { VaeName = req.RequiredVae() },
            [Nodes.Encode] = new VAEEncode
            {
                Pixels = LoadImage.ImageOut(Nodes.Source),
                Vae = VAELoader.VaeOut(Nodes.Vae),
            },
            [Nodes.Decode] = new VAEDecode
            {
                Samples = VAEEncode.Out(Nodes.Encode),
                Vae = VAELoader.VaeOut(Nodes.Vae),
            },
            [Nodes.Save] = new SaveImage
            {
                Images = VAEDecode.Out(Nodes.Decode),
                FilenamePrefix = OutputPrefixes.Edit,
            },
        };
    }

    private static class Nodes
    {
        public const string Source = "10";
        public const string Vae = "20";
        public const string Encode = "21";
        public const string Decode = "22";
        public const string Save = "9";
    }
}

/// <summary>No knobs by design: changing anything between source and encode would invalidate the control.</summary>
public sealed record VaeRoundTripParams;
