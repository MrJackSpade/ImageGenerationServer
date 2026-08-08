namespace ImageGen.Comfy;

/// <summary>
/// The shared image-EDIT parameter menu. Each edit MODEL has its own subclass deriving from
/// <see cref="EditWorkflow{TParams}"/> with its own self-contained graph; the one thing shared here is the
/// <see cref="SharedSchema"/> parameter set they draw their exposed knobs from.
/// </summary>
internal static class EditWorkflowBase
{
    internal static readonly IReadOnlyList<ParamSpec> SharedSchema =
    [
        new() { Key = LoaderKinds.ParamKey, Type = ParamType.Enum, Choices = LoaderKindWire.Choices },
        // UNETLoader cast-at-load. "default" keeps the file's own dtype; fp8_e4m3fn halves a bf16's VRAM so a 12B
        // model fits a 24GB card alongside its text encoder instead of swapping against it.
        new() { Key = WorkflowParamKeys.WeightDtype, Type = ParamType.String },
        new() { Key = WorkflowParamKeys.ClipType, Type = ParamType.String },
        new() { Key = WorkflowParamKeys.Dual,      Type = ParamType.Bool },
        new() { Key = WorkflowParamKeys.Steps,     Type = ParamType.Int,    Min = ParamBounds.StepsMin, Max = ParamBounds.StepsMax, Label = "Steps", EtaVariable = true },
        new() { Key = WorkflowParamKeys.Cfg,       Type = ParamType.Double, Min = ParamBounds.CfgMin,   Max = ParamBounds.CfgMax,  Label = "CFG scale" },
        new() { Key = WorkflowParamKeys.Guidance,  Type = ParamType.Double },
        new() { Key = WorkflowParamKeys.Sampler,   Type = ParamType.String },
        new() { Key = WorkflowParamKeys.Scheduler, Type = ParamType.String },
        // Video shapes (wan/animatediff/ltxv): frame-size budget, clip length (frames), playback fps. 0 = builder default.
        new() { Key = WorkflowParamKeys.Width,     Type = ParamType.Int },
        new() { Key = WorkflowParamKeys.Height,    Type = ParamType.Int },
        new() { Key = WorkflowParamKeys.Length,    Type = ParamType.Int,    Label = "Frames", EtaVariable = true },
        new() { Key = WorkflowParamKeys.Fps,       Type = ParamType.Double },
        new() { Key = WorkflowParamKeys.MotionModel, Type = ParamType.String, IsModelRef = true },
        // SD1.5 AnimateDiff's SparseCtrl-RGB adapter — a slot id resolved to a bound file, exactly like
        // motion_model. Without IsModelRef the raw slot id reaches ACN_SparseCtrlLoaderAdvanced and ComfyUI
        // rejects it (value_not_in_list), so animatediff-sd15 cannot render.
        new() { Key = WorkflowParamKeys.SparsectrlName, Type = ParamType.String, IsModelRef = true },
        // The i2v vision encoder (CLIP-ViT-H for Wan/ChronoEdit, SigCLIP for HunyuanVideo 1.5). A slot id like every
        // other model reference, not a private const filename — a hardcoded filename would be one machine's disk
        // written into the application and unreachable from the models page.
        new() { Key = WorkflowParamKeys.ClipVision, Type = ParamType.String, IsModelRef = true },
        // SDXL AnimateDiff img2img: how far frames drift from the source. Low = stays put (little motion); high =
        // more motion but loses the source. Exposed for tuning the motion/fidelity tradeoff.
        new() { Key = WorkflowParamKeys.Denoise,   Type = ParamType.Double, Min = ParamBounds.DenoiseMin, Max = ParamBounds.DenoiseMax, Step = 0.01, Label = "Denoise (source ↔ motion)" },
        // AnimateDiff only (ADE_UseEvolvedSampling). The WRONG schedule is what turns these into color-smear/no-motion
        // garbage, so it's a per-module setting, not an artistic one — exposed for iterative testing, to be locked
        // down once dialed in. No schema default: each AnimateDiff workflow falls back to its module's correct value.
        new() { Key = WorkflowParamKeys.BetaSchedule, Type = ParamType.Enum, Label = "AnimateDiff schedule",
                Choices = [ ComfyWidgets.BetaSchedule.Autoselect, ComfyWidgets.BetaSchedule.UseExisting,
                                  ComfyWidgets.BetaSchedule.SqrtLinearAnimateDiff, ComfyWidgets.BetaSchedule.LinearAnimateDiffSdxl,
                                  ComfyWidgets.BetaSchedule.LinearHotshotXlDefault, ComfyWidgets.BetaSchedule.AvgSqrtLinearLinear,
                                  ComfyWidgets.BetaSchedule.LcmAvgSqrtLinearLinear, ComfyWidgets.BetaSchedule.Lcm,
                                  ComfyWidgets.BetaSchedule.Lcm100Ots, ComfyWidgets.BetaSchedule.LcmThenSqrtLinear,
                                  ComfyWidgets.BetaSchedule.Sqrt, ComfyWidgets.BetaSchedule.Cosine,
                                  ComfyWidgets.BetaSchedule.SquaredcosCapV2 ] },
        // Reference images: how many extra images this editor accepts, and (Qwen) the encode-node slot names.
        new() { Key = WorkflowParamKeys.ReferenceMax,    Type = ParamType.Int },
        new() { Key = WorkflowParamKeys.ReferenceInputs, Type = ParamType.String },   // ["image2","image3"]
        // How the sampler stitches reference latents into the conditioning — a per-MODEL contract, not a preference:
        // Qwen-Image handles index_timestep_zero, plain-Flux LongCat crashes on it and takes index (see
        // ComfyWidgets.ReferenceLatents), so each config declares its model's method.
        new() { Key = WorkflowParamKeys.ReferenceLatentsMethod, Type = ParamType.Enum,
                Choices = [ ComfyWidgets.ReferenceLatents.Offset, ComfyWidgets.ReferenceLatents.Index,
                                  ComfyWidgets.ReferenceLatents.UxoUno, ComfyWidgets.ReferenceLatents.IndexTimestepZero ] },
        // Optional style/quality LoRA applied on top of the base model — lets a config be a "base + anime LoRA"
        // variant (e.g. WAN i2v + Flat Color) with no new graph code.
        new() { Key = WorkflowParamKeys.Lora,          Type = ParamType.String, IsModelRef = true },
        new() { Key = WorkflowParamKeys.LoraStrength, Type = ParamType.Double, Min = ParamBounds.EditLoraStrengthMin, Max = ParamBounds.EditLoraStrengthMax, Step = 0.01, Label = "LoRA strength" },
    ];
}