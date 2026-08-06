using ImageGen.Application.Rendering;

namespace ImageGen.Comfy;

/// <summary>
/// Typed base for the image-EDIT workflows: a <see cref="Workflow{TParams}"/> that fills in the edit-kind metadata
/// (Kind = Edit, still image, prompt-directs-motion default), the shared edit-head node ids, and the typed
/// <see cref="LoadModel"/> head. Converted edit workflows derive from this and implement <c>Build(TParams, …)</c>;
/// the not-yet-converted ones stay on the older <see cref="EditWorkflowBase"/> until they move over.
/// </summary>
public abstract class EditWorkflow<TParams> : Workflow<TParams>
{
    public override WorkflowKind Kind => WorkflowKind.Edit;
    public override WorkflowMedia Media => WorkflowMedia.Image;
    public override bool PromptDirectsMotion => true;

    /// <summary>Default to the shared edit parameter menu (same default <see cref="EditWorkflowBase"/> gives). A
    /// workflow with its own menu overrides this; one that extends the shared set does
    /// <c>EditWorkflowBase.SharedSchema.Concat(…)</c>.</summary>
    public override IReadOnlyList<ParamSpec> Schema => EditWorkflowBase.SharedSchema;

    /// <summary>Emit the common edit head — the model/CLIP/VAE loaders (from the loader wire value + resolved
    /// requirements) and the source <c>LoadImage</c> at node "10" — as typed nodes, returning the model/clip/vae edges.
    /// Byte-identical to <see cref="EditWorkflowBase.LoadModel"/>.</summary>
    protected static void LoadModel(ComfyWorkflowGraph g, string loaderWire, string? weightDtype, string? clipType,
        ResolvedRequirements req, WorkflowInputs inputs,
        out Output<Slot.Model> model0, out Output<Slot.Clip> clip0, out Output<Slot.Vae> vae0)
    {
        string file = req.RequiredCheckpoint();
        LoaderKind loader = LoaderKindWire.Parse(loaderWire);
        if (loader == LoaderKind.Checkpoint)
        {
            g[EditNodes.Model] = new CheckpointLoaderSimple { CkptName = file };
            model0 = CheckpointLoaderSimple.ModelOut(EditNodes.Model);
            vae0 = CheckpointLoaderSimple.VaeOut(EditNodes.Model);
            clip0 = req.TextEncoders.Count > 0
                ? BuildClipLoader(g, EditNodes.Clip, req.TextEncoders, clipType)
                : CheckpointLoaderSimple.ClipOut(EditNodes.Model);
        }
        else
        {
            g[EditNodes.Model] = weightDtype is { Length: > 0 } wd
                ? ComfyGraph.DiffusionLoaderNode(file, wd)
                : ComfyGraph.DiffusionLoaderNode(file);
            g[EditNodes.Vae] = new VAELoader { VaeName = req.RequiredVae() };
            model0 = UNETLoader.ModelOut(EditNodes.Model);
            vae0 = VAELoader.VaeOut(EditNodes.Vae);
            clip0 = BuildClipLoader(g, EditNodes.Clip, req.TextEncoders, clipType);
        }

        g[EditNodes.Source] = new LoadImage { Image = inputs.SourceImageName ?? throw new RenderValidationException("This edit needs a source image, but none was provided.") };
    }

    /// <summary>The CLIP loader a model's encoders call for, chosen by HOW MANY it declares (1→CLIPLoader,
    /// 2→Dual, 3→Triple, 4→Quadruple). Byte-identical to <see cref="EditWorkflowBase"/>'s.</summary>
    protected static Output<Slot.Clip> BuildClipLoader(ComfyWorkflowGraph g, string nodeId, IReadOnlyList<string> encoders, string? clipType)
    {
        string At(int i)
        {
            return i < encoders.Count && !string.IsNullOrWhiteSpace(encoders[i])
            ? encoders[i]
            : throw new RenderValidationException($"This configuration needs text encoder #{i + 1} and none is bound to that slot on this machine.");
        }

        g[nodeId] = encoders.Count switch
        {
            >= 4 => new QuadrupleCLIPLoader { ClipName1 = At(0), ClipName2 = At(1), ClipName3 = At(2), ClipName4 = At(3) },
            3 => new TripleCLIPLoader { ClipName1 = At(0), ClipName2 = At(1), ClipName3 = At(2) },
            2 => new DualCLIPLoader { ClipName1 = At(0), ClipName2 = At(1), Type = clipType, Device = ComfyWidgets.Device.Default },
            _ => ComfyGraph.IsGguf(At(0))
                ? new CLIPLoaderGGUF { ClipName = At(0), Type = clipType }
                : new CLIPLoader { ClipName = At(0), Type = clipType, Device = ComfyWidgets.Device.Default },
        };
        return new Output<Slot.Clip>(nodeId, 0);
    }
}

/// <summary>The shared edit head's node ids, named by role (values preserved so the emitted graph is byte-identical).
/// Referenced by every edit workflow's <c>Build</c> and by <see cref="EditWorkflow{TParams}.LoadModel"/>.</summary>
internal static class EditNodes
{
    public const string Model = "4";
    public const string Clip = "5";
    public const string Vae = "6";
    public const string Source = "10";
}
