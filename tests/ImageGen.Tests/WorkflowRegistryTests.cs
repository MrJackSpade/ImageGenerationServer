using ImageGen.Comfy;

namespace ImageGen.Tests;

public sealed class WorkflowRegistryTests
{
    [Fact]
    public void Duplicate_workflow_names_fail_fast_case_insensitively()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new WorkflowRegistry([new StubWorkflow("same-name"), new StubWorkflow("SAME-NAME")]));

        Assert.Contains("Duplicate workflow name 'SAME-NAME'", error.Message, StringComparison.Ordinal);
    }

    private sealed class StubWorkflow(string name) : IWorkflow
    {
        public string Name => name;
        public WorkflowKind Kind => WorkflowKind.Generate;
        public WorkflowMedia Media => WorkflowMedia.Image;
        public bool PromptDirectsMotion => false;
        public IReadOnlyList<ParamSpec> Schema => [];

        public ComfyWorkflowGraph Build(
            IReadOnlyDictionary<string, object?> p, ResolvedRequirements req, WorkflowInputs inputs) => new();
    }
}
