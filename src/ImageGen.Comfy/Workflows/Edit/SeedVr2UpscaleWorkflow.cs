using ImageGen.Application.Rendering;

namespace ImageGen.Comfy;

/// <summary>
/// Diffusion-based UPSCALE/RESTORE through SeedVR2 (ByteDance), via the ComfyUI-SeedVR2_VideoUpscaler node pack.
/// Unlike <see cref="UpscaleWorkflow"/>'s feed-forward SR nets, this is a one-step diffusion transformer: it
/// INVENTS plausible detail rather than only resolving what is present. That is the whole point (it can rebuild
/// texture an ESRGAN pass can only smooth) and also the whole risk (it can invent detail that was never there).
///
/// SeedVR2 is natively a VIDEO restorer — the upstream node is named for that — but it treats a single image as a
/// one-frame clip. <c>batch_size</c> follows a 4n+1 rule, so a still is exactly <c>batch_size = 1</c>; the temporal
/// machinery (overlap, prepend, uniform batching) is inert at one frame and left at its defaults.
///
/// The UI offers the same integer <c>scale</c> multiplier as the feed-forward upscalers. The node itself sizes by
/// TARGET SHORT EDGE, so <see cref="Build"/> converts: <c>resolution = short_edge(source) * scale</c>, aspect
/// preserved by the node. <c>max_resolution</c> is locked at 0 (the node's "no limit") so nothing is silently
/// clamped below what was asked for.
///
/// 8 GB fit: the 3B model at Q8_0 is ~3.4 GB of weights, which alone leaves too little room for activations at any
/// useful resolution. So the config ships BlockSwap on (<c>blocks_to_swap</c>, <c>swap_io_components</c>) with the
/// DiT offloading to system RAM, and tiled VAE encode/decode. Those are memory-fit settings, not quality caps —
/// every one of them is a locked config param, not a hard-coded constant, and none of them bound the output size.
/// </summary>
public sealed class SeedVr2UpscaleWorkflow : EditWorkflowBase
{
    public override string Name => "seedvr2-upscale";

    /// <summary>The upstream node declares <c>seed</c> as an INT capped at 2^32-1, unlike ComfyUI's samplers.</summary>
    internal const ulong SeedVr2SeedMax = 4294967295UL;


    /// <summary>No checkpoint — the DiT and its VAE load through the node pack's own loaders.</summary>
    public override bool RequiresModel => false;

    /// <summary>SeedVR2 is a restorer, not a text-to-image model: no text encoder, so no instruction box.</summary>
    public override bool TakesPrompt => false;

    /// <summary>A restore/upscale keeps the composition — exempt from the no-change gate.</summary>
    public override bool PreservesComposition => true;

    /// <summary>Diffusion, but not text-conditioned: SeedVR2 takes no prompt. Only sizing and memory knobs.</summary>
    public override IReadOnlyList<ParamSpec> Schema => SeedVr2Schema;
    private static readonly IReadOnlyList<ParamSpec> SeedVr2Schema = new ParamSpec[]
    {
        // Weight files in models/SEEDVR2. Locked per config; the node pack fetches them on first use if absent.
        new() { Key = "dit_model",  Type = ParamType.String, IsModelRef = true },
        new() { Key = "vae_model",  Type = ParamType.String, IsModelRef = true },
        // Sizing, expressed the same way as the feed-forward upscalers: a plain multiple of the SOURCE. The node
        // itself only understands a target short edge, so Build converts (short_edge * scale) -- see there.
        new() { Key = "scale", Type = ParamType.Int, Min = 1, Max = 4, Step = 1,
                Label = "Scale (×)", Help = "Output size relative to the source. The aspect ratio is preserved." },
        // The node's "no limit" sentinel. Locked: an upscaler must never silently shrink what it was asked for.
        new() { Key = "max_resolution", Type = ParamType.Int },
        // Fallback short edge for the rare case the edit path supplies no source dimensions (the node's own default).
        new() { Key = "fallback_short_edge", Type = ParamType.Int },
        // How the output's colour is re-matched to the source. Diffusion restorers drift; 'lab' is the pack's default.
        new() { Key = "color_correction", Type = ParamType.Enum,
                Choices = new[] { "lab", "wavelet", "wavelet_adaptive", "hsv", "adain", "none" }, Label = "Colour match" },
        // Compute + memory placement. cuda:0 / cpu on this single-GPU box.
        new() { Key = "device",         Type = ParamType.String },
        new() { Key = "offload_device", Type = ParamType.String },
        new() { Key = "attention_mode", Type = ParamType.String },
        // BlockSwap: how many of the 3B model's transformer blocks live on the offload device (max 32 for 3B).
        new() { Key = "blocks_to_swap",     Type = ParamType.Int,  Min = 0, Max = 36 },
        new() { Key = "swap_io_components", Type = ParamType.Bool },
        new() { Key = "cache_model",        Type = ParamType.Bool },
        // Tiled VAE — without it the encode/decode of a large frame spikes past the card on its own.
        new() { Key = "vae_tiled",        Type = ParamType.Bool },
        new() { Key = "vae_tile_size",    Type = ParamType.Int },
        new() { Key = "vae_tile_overlap", Type = ParamType.Int },
        // A still is a one-frame clip. 4n+1 => 1. Never raise this for an image editor.
        new() { Key = "batch_size", Type = ParamType.Int },
    };

    public override Dictionary<string, object> Build(ParamValues p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        string device = p.StrReq("device");
        string offload = p.StrReq("offload_device");
        bool tiled = p.Bool("vae_tiled");
        int tile = p.IntReq("vae_tile_size");
        int overlap = p.IntReq("vae_tile_overlap");

        var wf = new Dictionary<string, object>
        {
            ["10"] = ComfyGraph.Node("LoadImage", new { image = inputs.SourceImageName ?? throw new RenderValidationException("SeedVR2 upscale needs a source image, but none was provided.") }),

            // The DiT, with BlockSwap parked on the offload device so the 3B weights don't have to sit in VRAM whole.
            ["30"] = ComfyGraph.Node("SeedVR2LoadDiTModel", new
            {
                model = p.Model("dit_model"),
                device,
                blocks_to_swap = p.IntReq("blocks_to_swap"),
                swap_io_components = p.Bool("swap_io_components"),
                offload_device = offload,
                cache_model = p.Bool("cache_model"),
                attention_mode = p.StrReq("attention_mode"),
            }),

            // The VAE, tiled on both ends: a full-frame encode/decode is its own VRAM spike, independent of the DiT.
            ["31"] = ComfyGraph.Node("SeedVR2LoadVAEModel", new
            {
                model = p.Model("vae_model"),
                device,
                encode_tiled = tiled,
                encode_tile_size = tile,
                encode_tile_overlap = overlap,
                decode_tiled = tiled,
                decode_tile_size = tile,
                decode_tile_overlap = overlap,
                offload_device = offload,
                cache_model = p.Bool("cache_model"),
            }),
        };

        // SeedVR2's seed input is a UINT32 (max 4294967295); the app's single-sourced seed is 64-bit, so passing it
        // straight through makes ComfyUI reject the whole prompt with value_bigger_than_max. Fold it into range
        // deterministically -- the same image seed always yields the same SeedVR2 seed, so a re-run reproduces.
        long seed = (long)(unchecked((ulong)ComfyGraph.Seed(p)) % (SeedVr2SeedMax + 1UL));

        // The node sizes by TARGET SHORT EDGE, not by a multiplier, so turn the scale the UI offers into one:
        // short_edge(source) * scale, aspect preserved by the node. Snapped to even (the node's step). When the edit
        // path gives no source dimensions there is nothing to multiply, so fall back to the node's own default.
        // The node's own declared bounds for `resolution` (16..16384, even). Exceeding them is not a soft failure --
        // ComfyUI rejects the entire prompt at validation -- so clamp to the ceiling rather than emit a certain 400.
        // Unreachable in practice: it takes a >4096px short edge at 4x to get there.
        const int NodeResMin = 16, NodeResMax = 16384;
        int sw = inputs.SourceWidth, sh = inputs.SourceHeight;
        int scale = Math.Max(1, p.IntReq("scale"));
        int resolution = (sw > 0 && sh > 0)
            ? Math.Clamp((Math.Min(sw, sh) * scale + 1) / 2 * 2, NodeResMin, NodeResMax)
            : p.IntReq("fallback_short_edge");

        // One frame in, one frame out. uniform_batch_size is meaningless at batch_size 1 and stays off.
        wf["32"] = ComfyGraph.Node("SeedVR2VideoUpscaler", new
        {
            image = ComfyGraph.Ref("10", 0),
            dit = ComfyGraph.Ref("30", 0),
            vae = ComfyGraph.Ref("31", 0),
            seed,
            resolution,
            max_resolution = p.IntReq("max_resolution"),
            batch_size = p.IntReq("batch_size"),
            uniform_batch_size = false,
            color_correction = p.StrReq("color_correction"),
            offload_device = offload,
        });

        wf["9"] = ComfyGraph.Node("SaveImage", new { images = ComfyGraph.Ref("32", 0), filename_prefix = "forgemcp_edit" });
        return wf;
    }
}
