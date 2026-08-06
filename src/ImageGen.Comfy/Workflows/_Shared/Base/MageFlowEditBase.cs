using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;
using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Comfy;

/// <summary>
/// Mage-Flow-Edit instruction-based image editing (Mage-VAE + Native-Resolution DiT + Qwen3-VL). One unified node —
/// <c>TextEncodeMageFlowEdit</c> — takes the CLIP, the instruction (+ optional negative), the VAE and the reference
/// image(s) and emits (positive, negative) conditioning plus a zero latent sized to the output. The primary edited
/// image is <c>image_1</c>; extra references are <c>image_2..N</c>. Width/height are left 0 so the node follows the
/// (pre-scaled) reference size — Mage's RoPE aligns reference and target by position, so all references are resized
/// to the output resolution before encoding. The source is pre-scaled to the ~1MP native range (aligned to /16),
/// mirroring the official <c>image_mage_flow_edit_int8</c> template. Flow shift (6.0) is baked in at load.
/// </summary>
public abstract class MageFlowEditBase : EditWorkflow<MageFlowEditParams>
{
    public override ModelResolution? ResolutionEnvelope => new() { MinW = 512, MinH = 512, MaxW = 2048, MaxH = 2048, Step = 16 };

    protected override ComfyWorkflowGraph Build(MageFlowEditParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new ComfyWorkflowGraph();
        LoadModel(g, p.Loader, p.WeightDtype, p.ClipType, req, inputs, out Output<Slot.Model> model0, out Output<Slot.Clip> clip0, out Output<Slot.Vae> vae0);   // UNETLoader / CLIPLoader(type=mage) / VAELoader + LoadImage at "10"

        // Pre-scale the source into Mage's native ~1MP range, aligned to a /16 grid (matches the template's
        // ImageScaleToTotalPixels: lanczos, 1.0 MP, 16-px steps). Keeps a large upload inside the training
        // distribution instead of asking the model to render at, e.g., 3000px.
        g[MageFlowEditNodes.ScaledSource] = new ImageScaleToTotalPixels { Image = LoadImage.ImageOut(EditNodes.Source), UpscaleMethod = ComfyWidgets.Upscale.Lanczos, Megapixels = 1.0, ResolutionSteps = 16 };

        // Extra reference images -> image_2, image_3, ... (scaled the same way).
        Dictionary<string, object> refs = new Dictionary<string, object>();
        IReadOnlyList<string> refNames = inputs.ReferenceImageNames;
        // No reference_max declared → no extra refs (capacity 0). Supplying more references than the capacity is
        // REFUSED, not silently truncated.
        int rm = p.ReferenceMax ?? 0;
        if (refNames.Count > rm)
            throw new RenderValidationException($"This configuration accepts at most {rm} reference image(s); got {refNames.Count}.");
        int rn = refNames.Count;
        for (int i = 0; i < rn; i++)
        {
            string load = $"{40 + i * 2}", scale = $"{41 + i * 2}";
            g[load] = new LoadImage { Image = refNames[i] };
            g[scale] = new ImageScaleToTotalPixels { Image = LoadImage.ImageOut(load), UpscaleMethod = ComfyWidgets.Upscale.Lanczos, Megapixels = 1.0, ResolutionSteps = 16 };
            refs[$"image_{i + 2}"] = ImageScaleToTotalPixels.Out(scale);
        }

        g[MageFlowEditNodes.Encode] = new TextEncodeMageFlowEdit
        {
            Clip = clip0,
            Prompt = inputs.Positive,
            NegativePrompt = inputs.Negative ?? "",
            Vae = vae0,
            Width = 0,      // 0 -> follow the (scaled) reference's own size
            Height = 0,
            BatchSize = 1,
            Image1 = ImageScaleToTotalPixels.Out(MageFlowEditNodes.ScaledSource),
            Extra = refs.Count > 0 ? refs : null,
        };
        g[MageFlowEditNodes.Sampler] = new KSampler
        {
            Seed = ComfyGraph.Seed(p.Seed),
            Steps = p.Steps,
            Cfg = p.Cfg,
            SamplerName = ComfyGraph.MapSampler(p.Sampler),
            Scheduler = ComfyGraph.MapScheduler(p.Scheduler),
            Denoise = 1.0,
            Model = model0,
            Positive = TextEncodeMageFlowEdit.PositiveOut(MageFlowEditNodes.Encode),
            Negative = TextEncodeMageFlowEdit.NegativeOut(MageFlowEditNodes.Encode),
            LatentImage = TextEncodeMageFlowEdit.LatentOut(MageFlowEditNodes.Encode),
        };
        g[MageFlowEditNodes.Decode] = new VAEDecode { Samples = KSampler.Out(MageFlowEditNodes.Sampler), Vae = vae0 };
        g[MageFlowEditNodes.Save] = new SaveImage { Images = VAEDecode.Out(MageFlowEditNodes.Decode), FilenamePrefix = OutputPrefixes.Generate };
        return g;
    }
}
