using ImageGen.Comfy;

namespace ImageGen.Comfy.Edit.AnimateLcmI2V;

/// <summary>AnimateLCM (LCM LoRA + lcm sampler, ~8-step, CFG ~1.5) i2v.</summary>
public sealed class AnimateLcmI2VWorkflow : AnimateDiffI2VWorkflowBase
{
    public override string Name => "animatelcm-i2v";
}
