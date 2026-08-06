namespace ImageGen.Comfy;

/// <summary>
/// 24GB-tier generation models whose graph is NOT the plain single-CLIPLoader txt2img topology, so each gets its own
/// Build (over the shared Txt2Img parameter menu + typed nodes). All three gate to 24GB via their config's
/// min_vram_mb. Node ids follow the txt2img convention (4=model, 20=clip, 21=vae, 11=model-sampling, 6/7=encode,
/// 5=latent, 3=sampler, 8=decode, 9=save). Wired from the official ComfyUI example workflows; smoke-test on the box.
/// </summary>
internal static class HighVram
{
    /// <summary>The model loader block (UNETLoader / UnetLoaderGGUF / CheckpointLoaderSimple), returning typed
    /// model+vae refs.</summary>
    public static (Output<Slot.Model> model, Output<Slot.Vae> vae) LoadDiffusion(ComfyWorkflowGraph g, Txt2ImgParams p, ResolvedRequirements req)
    {
        LoaderKind loader = LoaderKindWire.Parse(p.RequiredLoader());
        if (loader == LoaderKind.Checkpoint)
        {
            g[HighVramNodes.Model] = new CheckpointLoaderSimple { CkptName = req.RequiredCheckpoint() };
            return (CheckpointLoaderSimple.ModelOut(HighVramNodes.Model), CheckpointLoaderSimple.VaeOut(HighVramNodes.Model));   // model, (clip unused), vae
        }

        g[HighVramNodes.Model] = ComfyGraph.DiffusionLoaderNode(req.RequiredCheckpoint());
        g[HighVramNodes.Vae] = new VAELoader { VaeName = req.RequiredVae() };
        return (UNETLoader.ModelOut(HighVramNodes.Model), VAELoader.VaeOut(HighVramNodes.Vae));
    }
}
