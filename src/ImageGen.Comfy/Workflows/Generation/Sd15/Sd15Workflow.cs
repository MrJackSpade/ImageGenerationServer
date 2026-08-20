namespace ImageGen.Comfy.Generation.Sd15;

/// <summary>
/// One workflow class per generation model. Each owns its identity; the topology comes from the base (the single
/// txt2img graph all of them use). Catalogue eligibility is determined by requirement presence, not VRAM metadata.
/// </summary>
public sealed class Sd15Workflow : Txt2ImgWorkflow<Txt2ImgParams> { public override string Name => "sd15"; }
