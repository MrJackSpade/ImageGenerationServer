using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;

namespace ImageGen.Comfy;

/// <summary>
/// BiRefNet background-removal matte for a SINGLE image — the still sibling of
/// <see cref="BiRefNetMatteVideoWorkflow"/>, for callers working per-frame (e.g. the sprite pipeline keying an
/// individually re-pixelized frame, where there is no clip to matte). Same <c>BiRefNetMatte</c> node, image IO:
/// <c>LoadImage → BiRefNetMatte → SaveImage</c>; the RGBA output saves as PNG so the alpha survives. No checkpoint
/// (the node loads its own model), so it must not be hidden by the catalog's no-model guard. Composition-preserving.
/// </summary>
public sealed class BiRefNetMatteWorkflow : Workflow<MatteParams>
{
    public override string Name => "birefnet-matte";
    public override WorkflowKind Kind => WorkflowKind.Edit;
    public override WorkflowMedia Media => WorkflowMedia.Image;
    public override bool PromptDirectsMotion => true;
    public override bool PreservesComposition => true;
    public override bool RequiresModel => false;
    public override IReadOnlyList<ParamSpec> Schema => MatteSchema;

    private static readonly IReadOnlyList<ParamSpec> MatteSchema = new ParamSpec[]
    {
        new() { Key = WorkflowParamKeys.Threshold, Type = ParamType.Double, Min = 0, Max = 1, Label = "Alpha cutoff", Help = "0 = soft matte (caller thresholds); >0 = hard cutoff at this matte value" },
    };

    /// <summary>This graph's node ids, named by role. Values are the graph-local keys, preserved exactly so the
    /// emitted graph stays byte-identical.</summary>
    private static class Nodes
    {
        public const string Source = "10";
        public const string Matte = "20";
        public const string Save = "9";
    }

    protected override ComfyWorkflowGraph Build(MatteParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        // No flatten-on-white: the matte wants the source verbatim (mirrors the video matte, which feeds frames as-is).
        return new ComfyWorkflowGraph
        {
            [Nodes.Source] = new LoadImage
            {
                Image = inputs.SourceImageName ?? throw new RenderValidationException("The matte needs a source image, but none was provided."),
            },
            // BiRefNetMatte output 0 = RGBA (frame + matte as alpha); SaveImage writes PNG, which keeps the alpha.
            [Nodes.Matte] = new BiRefNetMatte
            {
                Image = LoadImage.ImageOut(Nodes.Source),
                Threshold = p.Threshold,
            },
            [Nodes.Save] = new SaveImage
            {
                Images = BiRefNetMatte.Out(Nodes.Matte),
                FilenamePrefix = "forgemcp_edit",
            },
        };
    }
}

/// <summary>The BiRefNet matte's parameters, deserialized from the merged bag before <c>Build</c> — just the alpha
/// cutoff. Shared by the still (<see cref="BiRefNetMatteWorkflow"/>) and video (<see cref="BiRefNetMatteVideoWorkflow"/>)
/// mattes, which take the same one input. <c>required</c> so an absent value throws at the deserializer (the
/// declarative form of the previous <c>DblReq</c> read).</summary>
public sealed record MatteParams
{
    [JsonPropertyName(WorkflowParamKeys.Threshold)] public required double Threshold { get; init; }
}
