using ImageGen.Application.Rendering;

namespace ImageGen.Comfy;

/// <summary>
/// Shared graph primitives for MiniMax-H3-Image — ONE checkpoint serving both text-to-image and editing (one target
/// plus references, up to 16 pictures). A lift of the official Comfy-Org templates
/// (<c>image_minimax_h3_image_t2i.json</c> / <c>image_minimax_h3_image_edit.json</c>): <c>UNETLoader</c> (the pruned
/// int8-ConvRot DiT loads natively), <c>CLIPLoader</c> type <c>minimax</c> over the SAME Qwen3-VL 32B encoder the H3
/// video model uses, <c>VAELoader</c>, then <c>MiniMaxH3ImageConditioning</c> → (positive, latent).
///
/// <para>The checkpoint is distilled to 6 steps on a fixed PDD sigma grid with no classifier-free guidance: ComfyUI's
/// <c>MiniMaxH3ImageModelSampling</c> maps the stock scheduler at 6 steps onto that grid, so sampling is a plain
/// <c>KSampler</c> at CFG 1 with a zeroed-out negative. No negative prompt exists.</para>
/// </summary>
internal static class MiniMaxH3ImageGraph
{
    /// <summary>ComfyUI's H3-Image canvas grid (<c>CANVAS_MULTIPLE</c>): 16× VAE downsample × 2×2 patchify.</summary>
    public const int CanvasStep = 32;

    /// <summary>First node id for the edit's per-reference <c>LoadImage</c> nodes; reference i gets this + i.</summary>
    public const int ReferenceNodeBase = 21;

    public static (Output<Slot.Model> Model, Output<Slot.Clip> Clip, Output<Slot.Vae> Vae) Load(
        ComfyWorkflowGraph g, ResolvedRequirements req)
    {
        g[MiniMaxH3ImageNodes.Model] = ComfyGraph.DiffusionLoaderNode(req.RequiredCheckpoint());
        g[MiniMaxH3ImageNodes.Clip] = new CLIPLoader
        {
            ClipName = req.TextEncoder(0),
            Type = ComfyWidgets.ClipType.Minimax,
            Device = ComfyWidgets.Device.Default,
        };
        g[MiniMaxH3ImageNodes.Vae] = new VAELoader { VaeName = req.RequiredVae() };
        return (UNETLoader.ModelOut(MiniMaxH3ImageNodes.Model), CLIPLoader.ClipOut(MiniMaxH3ImageNodes.Clip), VAELoader.VaeOut(MiniMaxH3ImageNodes.Vae));
    }

    /// <summary>The distilled sampler tail: zeroed negative (CFG 1 never reads it, but KSampler requires one), KSampler
    /// at denoise 1 off the conditioning node's own latent, decode, save.</summary>
    public static void SampleAndSave(ComfyWorkflowGraph g, MiniMaxH3ImageSampling s, Output<Slot.Model> model,
        Output<Slot.Vae> vae, Output<Slot.Conditioning> positive, Output<Slot.Latent> latent, string filenamePrefix)
    {
        g[MiniMaxH3ImageNodes.Negative] = new ConditioningZeroOut { Conditioning = positive };
        g[MiniMaxH3ImageNodes.Sampler] = new KSampler
        {
            Seed = ComfyGraph.Seed(s.Seed),
            Steps = s.Steps,
            Cfg = s.Cfg,
            SamplerName = ComfyGraph.MapSampler(s.Sampler),
            Scheduler = ComfyGraph.MapScheduler(s.Scheduler),
            Denoise = 1.0,
            Model = model,
            Positive = positive,
            Negative = ConditioningZeroOut.Out(MiniMaxH3ImageNodes.Negative),
            LatentImage = latent,
        };
        g[MiniMaxH3ImageNodes.Decode] = new VAEDecode { Samples = KSampler.Out(MiniMaxH3ImageNodes.Sampler), Vae = vae };
        g[MiniMaxH3ImageNodes.Save] = new SaveImage { Images = VAEDecode.Out(MiniMaxH3ImageNodes.Decode), FilenamePrefix = filenamePrefix };
    }
}

/// <summary>The sampler knobs both H3-Image workflows read off their params and hand to
/// <see cref="MiniMaxH3ImageGraph.SampleAndSave"/>.</summary>
internal readonly record struct MiniMaxH3ImageSampling(int Steps, double Cfg, string Sampler, string Scheduler, long Seed);

/// <summary>MiniMax-H3-Image node ids. Model/Clip/Vae/Source match <see cref="EditNodes"/>; picture references take
/// <see cref="MiniMaxH3ImageGraph.ReferenceNodeBase"/> + i, clear of every fixed id here and of the LoRA stack (91+).</summary>
internal static class MiniMaxH3ImageNodes
{
    public const string Model = "4";
    public const string Clip = "5";
    public const string Vae = "6";
    public const string Encode = "7";
    public const string Negative = "8";
    public const string Sampler = "9";
    public const string Source = "10";
    public const string Decode = "11";
    public const string Save = "12";
    public const string ScaledSource = "13";
    public const string SourceSize = "14";
    public const string CkAttention = "37";
}
