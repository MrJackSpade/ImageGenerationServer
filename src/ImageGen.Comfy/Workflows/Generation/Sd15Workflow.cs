namespace ImageGen.Comfy;

/// <summary>
/// One workflow class per generation model. Each owns its identity + VRAM band; the topology comes from the base
/// (the single txt2img graph all of them use). VRAM floors are conservative so nothing currently working drops.
/// </summary>
public sealed class Sd15Workflow : Txt2ImgWorkflow<Txt2ImgParams> { public override string Name => "sd15"; }
