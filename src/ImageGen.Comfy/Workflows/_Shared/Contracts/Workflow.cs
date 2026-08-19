namespace ImageGen.Comfy;

/// <summary>
/// Base for a workflow whose parameters are a strongly-typed DTO. The renderer dispatches to the non-generic
/// <see cref="Build(IReadOnlyDictionary{string, object}, ResolvedRequirements, WorkflowInputs)"/> seam, which deserializes the merged
/// parameter bag into this workflow's own <typeparamref name="TParams"/> in ONE System.Text.Json pass and hands the
/// typed DTO to <see cref="Build(TParams, ResolvedRequirements, WorkflowInputs)"/>. A concrete workflow reads typed
/// properties off its params record and never touches a string key, an accessor, or a loosely-typed bag.
/// <para>The <see cref="IWorkflow"/> metadata is mirrored here as class members (abstract for the required ones,
/// virtual with the same defaults for the rest) so a workflow overrides them with ordinary <c>override</c>.</para>
/// </summary>
public abstract class Workflow<TParams> : IWorkflow
{
    public abstract string Name { get; }
    public abstract WorkflowKind Kind { get; }
    public abstract WorkflowMedia Media { get; }
    public abstract bool PromptDirectsMotion { get; }
    public abstract IReadOnlyList<ParamSpec> Schema { get; }

    public virtual WorkflowMedia SourceMedia => WorkflowMedia.Image;
    public virtual PromptSemantics PromptSemantics => PromptSemantics.Instruction;
    public virtual bool SupportsEndFrame => false;
    public virtual bool HasAudio => false;
    public virtual bool PreservesComposition => false;
    public virtual bool RequiresModel => true;
    public virtual bool TakesPrompt => true;
    public virtual FrameRule? FrameRule => null;
    public virtual ModelResolution? ResolutionEnvelope => null;
    public virtual bool NormalizesSourceResolution => false;

    /// <summary>Pre-build parameter normalization — the seconds→frames conversion and frame-count snap (enqueue +
    /// submit). Bag-based because it runs BEFORE the DTO is deserialized (it mutates the values that then feed the
    /// DTO). Mirrors the <see cref="IWorkflow.Normalize"/> default via the shared <see cref="FrameNormalization"/>.</summary>
    public virtual IReadOnlyList<string> Normalize(IDictionary<string, object?> p, NormalizeContext ctx)
        => FrameNormalization.Apply(FrameRule, p);

    /// <summary>The non-generic build entry the renderer dispatches to: deserialize the merged bag into this workflow's
    /// <typeparamref name="TParams"/> at the <see cref="ParamsCodec"/> boundary, then build the TYPED graph. This is the
    /// single seam where the loosely-typed bag becomes a DTO — a workflow itself sees neither a string key on the way in
    /// nor a <c>Dictionary&lt;string, object&gt;</c> on the way out.</summary>
    public ComfyWorkflowGraph Build(IReadOnlyDictionary<string, object?> p, ResolvedRequirements req, WorkflowInputs inputs)
        => Build(ParamsCodec.Deserialize<TParams>(p), req, inputs);

    /// <summary>Build the ComfyUI graph — typed params in, a typed graph of typed nodes out.</summary>
    protected abstract ComfyWorkflowGraph Build(TParams p, ResolvedRequirements req, WorkflowInputs inputs);

    /// <summary>The bag-based <see cref="IWorkflow.EtaRenderSize"/> seam: deserialize the merged bag into
    /// <typeparamref name="TParams"/> (same <see cref="ParamsCodec"/> pass as <see cref="Build"/>) and hand off to the
    /// typed overload, so a workflow never touches a string key here either.</summary>
    (int Width, int Height) IWorkflow.EtaRenderSize(IReadOnlyDictionary<string, object?> p, ResolvedRequirements req, int sourceWidth, int sourceHeight)
        => EtaRenderSize(ParamsCodec.Deserialize<TParams>(p), req, sourceWidth, sourceHeight);

    /// <summary>The resolution this workflow actually renders at for a source of the given dims — typed params in.
    /// Default: the source dims unchanged. A budget-scaling workflow overrides this to return
    /// <see cref="BudgetScale.Snap"/> with its own megapixel budget and step grid. Mirrors
    /// <see cref="IWorkflow.EtaRenderSize"/>.</summary>
    protected virtual (int Width, int Height) EtaRenderSize(TParams p, ResolvedRequirements req, int sourceWidth, int sourceHeight)
        => (sourceWidth, sourceHeight);
}
