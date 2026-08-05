using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageGen.Application.Rendering;
using ImageGen.Domain;

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
public sealed class SeedVr2UpscaleWorkflow : EditWorkflow<SeedVr2Params>
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
        new() { Key = WorkflowParamKeys.DitModel,  Type = ParamType.String, IsModelRef = true },
        new() { Key = WorkflowParamKeys.VaeModel,  Type = ParamType.String, IsModelRef = true },
        // Sizing, expressed the same way as the feed-forward upscalers: a plain multiple of the SOURCE. The node
        // itself only understands a target short edge, so Build converts (short_edge * scale) -- see there.
        new() { Key = WorkflowParamKeys.Scale, Type = ParamType.Int, Min = 1, Max = 4, Step = 1,
                Label = "Scale (×)", Help = "Output size relative to the source. The aspect ratio is preserved." },
        // The node's "no limit" sentinel. Locked: an upscaler must never silently shrink what it was asked for.
        new() { Key = WorkflowParamKeys.MaxResolution, Type = ParamType.Int },
        // How the output's colour is re-matched to the source. Diffusion restorers drift; 'lab' is the pack's default.
        new() { Key = WorkflowParamKeys.ColorCorrection, Type = ParamType.Enum,
                Choices = new[] { ComfyWidgets.ColorMatch.Lab, ComfyWidgets.ColorMatch.Wavelet, ComfyWidgets.ColorMatch.WaveletAdaptive,
                                  ComfyWidgets.ColorMatch.Hsv, ComfyWidgets.ColorMatch.Adain, ComfyWidgets.ColorMatch.None }, Label = "Colour match" },
        // Compute + memory placement. cuda:0 / cpu on this single-GPU box.
        new() { Key = WorkflowParamKeys.Device,         Type = ParamType.String },
        new() { Key = WorkflowParamKeys.OffloadDevice, Type = ParamType.String },
        new() { Key = WorkflowParamKeys.AttentionMode, Type = ParamType.String },
        // BlockSwap: how many of the 3B model's transformer blocks live on the offload device (max 32 for 3B).
        new() { Key = WorkflowParamKeys.BlocksToSwap,     Type = ParamType.Int,  Min = 0, Max = 36 },
        new() { Key = WorkflowParamKeys.SwapIoComponents, Type = ParamType.Bool },
        new() { Key = WorkflowParamKeys.CacheModel,        Type = ParamType.Bool },
        // Tiled VAE — without it the encode/decode of a large frame spikes past the card on its own.
        new() { Key = WorkflowParamKeys.VaeTiled,        Type = ParamType.Bool },
        new() { Key = WorkflowParamKeys.VaeTileSize,    Type = ParamType.Int },
        new() { Key = WorkflowParamKeys.VaeTileOverlap, Type = ParamType.Int },
        // A still is a one-frame clip. 4n+1 => 1. Never raise this for an image editor.
        new() { Key = WorkflowParamKeys.BatchSize, Type = ParamType.Int },
    };

    protected override ComfyWorkflowGraph Build(SeedVr2Params p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        string device = p.Device;
        string offload = p.OffloadDevice;
        bool tiled = p.VaeTiled;
        int tile = p.VaeTileSize;
        int overlap = p.VaeTileOverlap;

        string source = inputs.SourceImageName ?? throw new RenderValidationException("SeedVR2 upscale needs a source image, but none was provided.");
        ComfyWorkflowGraph g = new ComfyWorkflowGraph
        {
            [EditNodes.Source] = new LoadImage { Image = source },

            // The DiT, with BlockSwap parked on the offload device so the 3B weights don't have to sit in VRAM whole.
            [Nodes.Dit] = new SeedVR2LoadDiTModel
            {
                Model = p.DitModel,
                Device = device,
                BlocksToSwap = p.BlocksToSwap,
                SwapIoComponents = p.SwapIoComponents,
                OffloadDevice = offload,
                CacheModel = p.CacheModel,
                AttentionMode = p.AttentionMode,
            },

            // The VAE, tiled on both ends: a full-frame encode/decode is its own VRAM spike, independent of the DiT.
            [Nodes.Vae] = new SeedVR2LoadVAEModel
            {
                Model = p.VaeModel,
                Device = device,
                EncodeTiled = tiled,
                EncodeTileSize = tile,
                EncodeTileOverlap = overlap,
                DecodeTiled = tiled,
                DecodeTileSize = tile,
                DecodeTileOverlap = overlap,
                OffloadDevice = offload,
                CacheModel = p.CacheModel,
            },
        };

        // SeedVR2's seed input is a UINT32 (max 4294967295); the app's single-sourced seed is 64-bit, so passing it
        // straight through makes ComfyUI reject the whole prompt with value_bigger_than_max. Fold it into range
        // deterministically -- the same image seed always yields the same SeedVR2 seed, so a re-run reproduces.
        long seed = (long)(unchecked((ulong)ComfyGraph.Seed(p.Seed)) % (SeedVr2SeedMax + 1UL));

        // The node sizes by TARGET SHORT EDGE, not by a multiplier, so turn the scale the UI offers into one:
        // short_edge(source) * scale, aspect preserved by the node, snapped to even (the node's required step).
        const int NodeResMin = 16, NodeResMax = 16384;
        int scale = p.Scale;   // range enforced by the DTO's [Range(1,4)] at the ParamsCodec boundary
        // The source is a still, so its dimensions are ALWAYS measured — a zero is a broken source to refuse, not a
        // state to substitute a fixed short edge for.
        int sw = Ensure.GreaterThanZero(inputs.SourceWidth), sh = Ensure.GreaterThanZero(inputs.SourceHeight);
        // A computed target outside the node's declared [16, 16384] is REFUSED, not clamped: silently returning 16384
        // hands back a smaller upscale than the scale asked for, and the caller can lower the scale (or feed a smaller
        // source) to make a valid request. The even snap above is the node's step, not an adjustment of intent.
        int resolution = (Math.Min(sw, sh) * scale + 1) / 2 * 2;
        Ensure.Between(resolution, NodeResMin, NodeResMax);

        // One frame in, one frame out. uniform_batch_size is meaningless at batch_size 1 and stays off.
        g[Nodes.Upscale] = new SeedVR2VideoUpscaler
        {
            Image = LoadImage.ImageOut(EditNodes.Source),
            Dit = SeedVR2LoadDiTModel.Out(Nodes.Dit),
            Vae = SeedVR2LoadVAEModel.Out(Nodes.Vae),
            Seed = seed,
            Resolution = resolution,
            MaxResolution = p.MaxResolution,
            BatchSize = p.BatchSize,
            UniformBatchSize = false,
            ColorCorrection = p.ColorCorrection,
            OffloadDevice = offload,
        };

        g[Nodes.Save] = new SaveImage { Images = SeedVR2VideoUpscaler.Out(Nodes.Upscale), FilenamePrefix = OutputPrefixes.Edit };
        return g;
    }
}

/// <summary>SeedVR2's own loader/upscale node ids (source LoadImage reuses the inherited <c>EditNodes.Source</c>).</summary>
file static class Nodes
{
    public const string Dit = "30";
    public const string Vae = "31";
    public const string Upscale = "32";
    public const string Save = "9";
}

/// <summary>SeedVR2 restore/upscale parameters — the DiT/VAE model refs, sizing, colour match, and memory-fit knobs.
/// The <c>*Req</c>-read values are <c>required</c>; the three boolean toggles keep their false default; <c>seed</c> is
/// the app's single-sourced generation seed (folded into the node's uint32 range in Build).</summary>
public sealed record SeedVr2Params
{
    [JsonPropertyName(WorkflowParamKeys.DitModel)]        public required string DitModel { get; init; }
    [JsonPropertyName(WorkflowParamKeys.VaeModel)]        public required string VaeModel { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Scale)]
    [Range(1, 4)]                                         public required int Scale { get; init; }
    [JsonPropertyName(WorkflowParamKeys.MaxResolution)]   public required int MaxResolution { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ColorCorrection)] public required string ColorCorrection { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Device)]          public required string Device { get; init; }
    [JsonPropertyName(WorkflowParamKeys.OffloadDevice)]   public required string OffloadDevice { get; init; }
    [JsonPropertyName(WorkflowParamKeys.AttentionMode)]   public required string AttentionMode { get; init; }
    [JsonPropertyName(WorkflowParamKeys.BlocksToSwap)]
    [Range(0, 36)]                                        public required int BlocksToSwap { get; init; }
    [JsonPropertyName(WorkflowParamKeys.SwapIoComponents)] public bool SwapIoComponents { get; init; }
    [JsonPropertyName(WorkflowParamKeys.CacheModel)]      public bool CacheModel { get; init; }
    [JsonPropertyName(WorkflowParamKeys.VaeTiled)]        public bool VaeTiled { get; init; }
    [JsonPropertyName(WorkflowParamKeys.VaeTileSize)]     public required int VaeTileSize { get; init; }
    [JsonPropertyName(WorkflowParamKeys.VaeTileOverlap)]  public required int VaeTileOverlap { get; init; }
    [JsonPropertyName(WorkflowParamKeys.BatchSize)]       public required int BatchSize { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)]            public long Seed { get; init; }
}
