namespace ImageGen.Comfy;

/// <summary>
/// The comfy-kitchen attention toggle — an int8 attention kernel ComfyUI ships as an alternative sampling backend
/// (SageAttention's niche: faster and, per early reports, sharper than sage, with the biggest gains on Ampere).
/// Implemented by splicing ComfyUI's built-in <see cref="ModelAttentionBackend"/> node (a MODEL patch) between the
/// loader/LoRA head and the sampler; the wired graphs are the shared txt2img topology and the MiniMax-H3 family.
///
/// Off (the default) emits no node, leaving the graph byte-identical to the plain topology — the model then samples
/// on ComfyUI's default pytorch attention.
/// </summary>
public static class CkAttention
{
    /// <summary>The toggle, concatenated into each wired workflow's schema.</summary>
    public static readonly IReadOnlyList<ParamSpec> Schema =
    [
        new() { Key = WorkflowParamKeys.CkAttention, Type = ParamType.Bool, Label = "CK attention",
                Help = "Sample with the comfy-kitchen int8 attention kernel instead of the default pytorch attention. "
                     + "Experimental upstream: usually faster (largest gains on Ampere), can misbehave on some models." },
    ];

    /// <summary>Splice the backend-selector node at <paramref name="nodeId"/> onto the model chain when the toggle is
    /// on; otherwise return <paramref name="model"/> untouched (no node emitted). The caller owns
    /// <paramref name="nodeId"/> because the surrounding topologies differ.</summary>
    public static Output<Slot.Model> Apply(ComfyWorkflowGraph g, Output<Slot.Model> model, bool on, string nodeId)
    {
        if (!on)
        {
            return model;
        }

        g[nodeId] = new ModelAttentionBackend { Model = model, Attention = ComfyWidgets.Attention.ComfyKitchen };
        return ModelAttentionBackend.Out(nodeId);
    }
}
