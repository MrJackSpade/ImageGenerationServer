//TODO: CHECK FOR FALLBACKS
using ImageGen.Application.Rendering;

namespace ImageGen.Comfy;

/// <summary>
/// Step1X-Edit (original i1258) instruction editor, via the raykindle ComfyUI nodes. A self-contained loader
/// (<c>Step1XEditModelLoader</c>: DiT fp8 + Flux AE + a full Qwen2.5-VL-7B HF folder, int8-quantized + offloaded)
/// feeds <c>Step1XEditGenerate</c>. The node's flash-attn requirement is patched to fall back to PyTorch SDPA
/// (flash-attn won't build on this box), so no flash-attn is needed. Files resolve from the E category dirs
/// (diffusion_models → Stable-diffusion, vae → VAE, text_encoders → text_encoder). No ComfyUI model loaders here,
/// hence <see cref="RequiresModel"/> = false. NOTE: superseded by Step1X-Edit v1p2; this is the older release.
/// </summary>
public sealed class Step1XEditWorkflow : EditWorkflowBase
{
    public override string Name => "step1x-edit-i1258";
    /// <summary>Self-contained loader node (manages its own VRAM: int8 + offload) — no ComfyUI loaders to presence-gate.</summary>
    public override bool RequiresModel => false;

    /// <summary>The DiT and AE are slot ids on the configuration, resolved to this machine's bound files — they were
    /// const filenames here, which is one person's disk written into the application and unreachable from the models
    /// page. The text encoder stays a literal: it is not a file but the name of a Hugging Face folder the node
    /// loads from its own directory, so there is nothing to bind.</summary>
    private const string TextEncoder = "Qwen2.5-VL-7B-Instruct";

    public override IReadOnlyList<ParamSpec> Schema => _schema;
    private static readonly IReadOnlyList<ParamSpec> _schema = SharedSchema.Concat(new ParamSpec[]
    {
        new() { Key = "diffusion_model", Type = ParamType.String, IsModelRef = true },
        new() { Key = "step1x_vae",      Type = ParamType.String, IsModelRef = true },
    }).ToArray();

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        var wf = new Dictionary<string, object>
        {
            ["10"] = ComfyGraph.Node("LoadImage", new { image = inputs.SourceImageName ?? throw new RenderValidationException("Step1X-Edit needs a source image, but none was provided.") }),
            ["1"] = ComfyGraph.Node("Step1XEditModelLoader", new
            {
                diffusion_model = p.Model("diffusion_model"),
                vae = p.Model("step1x_vae"),
                text_encoder = TextEncoder,
                dtype = "bfloat16",
                quantized = true,
                offload = true,
            }),
        };
        wf["2"] = ComfyGraph.Node("Step1XEditGenerate", new
        {
            model = ComfyGraph.Ref("1", 0),
            input_image = ComfyGraph.Ref("10", 0),
            prompt = inputs.Positive,
            negative_prompt = "",
            num_steps = p.IntReq("steps"),
            cfg_guidance = p.DblReq("cfg"),
            seed = ComfyGraph.Seed(p),
            size_level = p.Int("width") > 0 ? p.Int("width") : 1024,
        });
        wf["9"] = ComfyGraph.Node("SaveImage", new { images = ComfyGraph.Ref("2", 0), filename_prefix = "forgemcp_edit" });
        return wf;
    }
}
