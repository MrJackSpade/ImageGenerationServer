using ImageGen.Application.Rendering;

namespace ImageGen.Comfy;

/// <summary>Shared graph primitives for the unified Qwen-Image 2.1 generation and editing workflows.</summary>
internal static class QwenImage21Graph
{
    public static (Output<Slot.Model> Model, Output<Slot.Clip> Clip, Output<Slot.Vae> Vae) Load(
        ComfyWorkflowGraph g, ResolvedRequirements req)
    {
        g[QwenImage21Nodes.Model] = ComfyGraph.DiffusionLoaderNode(req.RequiredCheckpoint());
        g[QwenImage21Nodes.Clip] = new CLIPLoader
        {
            ClipName = req.TextEncoder(0),
            Type = ComfyWidgets.ClipType.QwenImage,
            Device = ComfyWidgets.Device.Default,
        };
        g[QwenImage21Nodes.Vae] = new VAELoader { VaeName = req.RequiredVae() };
        g[QwenImage21Nodes.Cache] = new QwenImage21Cache
        {
            Model = UNETLoader.ModelOut(QwenImage21Nodes.Model),
            Device = ComfyWidgets.QwenImageCache.Auto,
            Dtype = ComfyWidgets.QwenImageCache.Default,
        };
        return (QwenImage21Cache.Out(QwenImage21Nodes.Cache), CLIPLoader.ClipOut(QwenImage21Nodes.Clip), VAELoader.VaeOut(QwenImage21Nodes.Vae));
    }

    public static void SampleAndSave(ComfyWorkflowGraph g, Txt2ImgParams p, Output<Slot.Model> model,
        Output<Slot.Vae> vae, Output<Slot.Conditioning> positive, Output<Slot.Conditioning> negative,
        Output<Slot.Latent> latent)
    {
        g[QwenImage21Nodes.Sampler] = new KSampler
        {
            Seed = ComfyGraph.Seed(p.Seed),
            Steps = p.Steps,
            Cfg = p.RequiredCfg(),
            SamplerName = ComfyGraph.MapSampler(p.Sampler),
            Scheduler = ComfyGraph.MapScheduler(p.Scheduler),
            Denoise = 1.0,
            Model = model,
            Positive = positive,
            Negative = negative,
            LatentImage = latent,
        };
        g[QwenImage21Nodes.Decode] = new VAEDecode { Samples = KSampler.Out(QwenImage21Nodes.Sampler), Vae = vae };
        g[QwenImage21Nodes.Save] = new SaveImage { Images = VAEDecode.Out(QwenImage21Nodes.Decode), FilenamePrefix = OutputPrefixes.Generate };
    }
}

internal static class QwenImage21Nodes
{
    public const string Model = "4";
    public const string Clip = "5";
    public const string Vae = "6";
    public const string Encode = "7";
    public const string Latent = "8";
    public const string Sampler = "9";
    public const string Decode = "11";
    public const string Save = "12";
    public const string Cache = "13";
    public const string Source = "20";
}
