namespace ImageGen.Application.Snapshots;

/// <summary>
/// This machine's per-model MATCHED average render timings (ms), keyed by config id (#200) — the decoration behind the
/// <c>/forge/workflows</c> ETAs and the <c>/forge/queue</c> header ETA. Each config's value averages only recent renders
/// whose parameter signature (resolution/steps/frames) is identical to that config's most recent render; there is no
/// blending or scaling across signatures. Flushed exactly on job finalization (the only event that changes the
/// averages), with a 5-minute backstop.
///
/// <para>These are a DECORATION on already-decided rows: a faulted snapshot must degrade at the consumer (list without
/// ETAs, log a warning) rather than 502 the picker/queue. The snapshot itself stays honest — the degrade decision lives
/// in the consumer's existing catch, not here.</para>
/// </summary>
public sealed class GenTimingAverages(IReadOnlyDictionary<string, double> byModel)
{
    /// <summary>configId → recent average render duration in milliseconds.</summary>
    public IReadOnlyDictionary<string, double> ByModel { get; } = byModel;

    /// <summary>The average ms for a model, or null when this machine has no recent sample for it.</summary>
    public double? SecondsFor(string configId) =>
        ByModel.TryGetValue(configId, out double ms) ? ms / 1000.0 : null;
}
