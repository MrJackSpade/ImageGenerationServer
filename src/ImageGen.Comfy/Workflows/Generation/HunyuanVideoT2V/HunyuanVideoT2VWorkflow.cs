using ImageGen.Comfy;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;
using ImageGen.Domain;
using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Comfy.Generation.HunyuanVideoT2V;

/// <summary>Original HunyuanVideo 13B text→video. The diffusion loader follows the bound file; the LLaVA-Llama3/CLIP-L DualCLIPLoader
/// (type "hunyuan_video") + ModelSamplingSD3 + embedded FluxGuidance + EmptyHunyuanLatentVideo + a
/// BasicGuider/SamplerCustomAdvanced chain (guidance-distilled: cfg 1, no negative). The t2v sibling of the i2v
/// GGUF editor already in the catalog.</summary>
public sealed class HunyuanVideoT2VWorkflow : Txt2ImgWorkflow<HunyuanVideoT2VParams>
{
    public override string Name => "hunyuanvideo-t2v";
    public override WorkflowMedia Media => WorkflowMedia.Video;
    public override bool PromptDirectsMotion => true;

    protected override ComfyWorkflowGraph Build(HunyuanVideoT2VParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new ComfyWorkflowGraph();
        g[EditNodes.Model] = ComfyGraph.DiffusionLoaderNode(req.RequiredCheckpoint());
        g[HunyuanVideoT2VWorkflowNodes.ModelSampling] = new ModelSamplingSD3 { Model = UNETLoader.ModelOut(EditNodes.Model), Shift = p.Shift };
        Output<Slot.Model> model = ModelSamplingSD3.Out(HunyuanVideoT2VWorkflowNodes.ModelSampling);
        g[EditNodes.Clip] = new DualCLIPLoader { ClipName1 = req.TextEncoder(0), ClipName2 = req.TextEncoder(1), Type = ComfyWidgets.ClipType.HunyuanVideo, Device = ComfyWidgets.Device.Default };
        Output<Slot.Clip> clip = DualCLIPLoader.ClipOut(EditNodes.Clip);
        g[EditNodes.Vae] = new VAELoader { VaeName = req.RequiredVae() };
        Output<Slot.Vae> vae = VAELoader.VaeOut(EditNodes.Vae);

        (int w, int h) = p.Dims(ComfyGraph.NormalizeAspect(inputs.Aspect));
        int len = p.Length;
        double fps = p.Fps;
        g[Nodes.Positive] = new CLIPTextEncode { Text = inputs.Positive, Clip = clip };
        g[Nodes.Guidance] = new FluxGuidance { Conditioning = CLIPTextEncode.Out(Nodes.Positive), Guidance = p.RequiredGuidance() };
        g[HunyuanVideoT2VWorkflowNodes.VideoLatent] = new EmptyHunyuanLatentVideo { Width = w, Height = h, Length = len, BatchSize = 1 };
        g[HunyuanVideoT2VWorkflowNodes.Scheduler] = new BasicScheduler { Model = model, Scheduler = ComfyGraph.MapScheduler(p.Scheduler), Steps = p.Steps, Denoise = 1.0 };
        g[HunyuanVideoT2VWorkflowNodes.SamplerSelect] = new KSamplerSelect { SamplerName = ComfyGraph.MapSampler(p.Sampler) };
        g[HunyuanVideoT2VWorkflowNodes.Noise] = new RandomNoise { NoiseSeed = ComfyGraph.Seed(p.Seed) };
        g[HunyuanVideoT2VWorkflowNodes.Guider] = new BasicGuider { Model = model, Conditioning = FluxGuidance.Out(Nodes.Guidance) };
        g[Nodes.Sampler] = new SamplerCustomAdvanced { Noise = RandomNoise.Out(HunyuanVideoT2VWorkflowNodes.Noise), Guider = BasicGuider.Out(HunyuanVideoT2VWorkflowNodes.Guider), Sampler = KSamplerSelect.Out(HunyuanVideoT2VWorkflowNodes.SamplerSelect), Sigmas = BasicScheduler.Out(HunyuanVideoT2VWorkflowNodes.Scheduler), LatentImage = EmptyHunyuanLatentVideo.Out(HunyuanVideoT2VWorkflowNodes.VideoLatent) };
        g[Nodes.Decode] = new VAEDecodeTiled { Samples = SamplerCustomAdvanced.Out(Nodes.Sampler), Vae = vae, TileSize = 256, Overlap = 64, TemporalSize = 64, TemporalOverlap = 8 };
        g[Nodes.Save] = new SaveAnimatedWEBPLiteralFps { Images = VAEDecodeTiled.Out(Nodes.Decode), FilenamePrefix = OutputPrefixes.Generate, Fps = fps, Lossless = false, Quality = 80, Method = ComfyWidgets.WebpMethod.Default };
        return g;
    }
}
