namespace ImageGen.Comfy;

/// <summary>The shared seed control for every stochastic workflow.</summary>
internal static class SeedParam
{
    internal static readonly IReadOnlyList<ParamSpec> Schema =
    [
        new()
        {
            Key = WorkflowParamKeys.Seed,
            Type = ParamType.Int,
            Min = 0,
            Step = 1,
            Label = "Seed",
            Help = "Leave blank for a fresh random seed; enter a number (including 0) to reproduce a result.",
        },
    ];
}
