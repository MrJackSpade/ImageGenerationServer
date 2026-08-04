namespace ImageGen.Comfy;

/// <summary>
/// Resolves a workflow by name. Built from the DI-registered set of <see cref="IWorkflow"/> singletons (explicit
/// registration, no reflection). The <see cref="WorkflowConfiguration.WorkflowName"/> of every configuration must
/// resolve here, or that configuration is unusable.
/// </summary>
public sealed class WorkflowRegistry
{
    private readonly Dictionary<string, IWorkflow> _byName;

    public WorkflowRegistry(IEnumerable<IWorkflow> workflows)
    {
        _byName = new Dictionary<string, IWorkflow>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in workflows) _byName[w.Name] = w;   // last registration wins on a duplicate name
    }

    public IWorkflow? Find(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : _byName.GetValueOrDefault(name);

    public IReadOnlyCollection<IWorkflow> All => _byName.Values;
}
