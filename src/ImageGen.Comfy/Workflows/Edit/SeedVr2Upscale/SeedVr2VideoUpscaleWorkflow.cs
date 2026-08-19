using ImageGen.Application.Rendering;

namespace ImageGen.Comfy.Edit.SeedVr2Upscale;

/// <summary>
/// Temporal SeedVR2 restoration/upscale for a source clip. The source is decoded once to an IMAGE frame batch plus
/// its media components. SeedVR2 processes neighboring frames together, then CreateVideo rebuilds an MP4 from the
/// restored frames while preserving the source FPS, optional audio track, and bit depth.
/// </summary>
public sealed class SeedVr2VideoUpscaleWorkflow : Workflow<SeedVr2VideoParams>
{
    public override string Name => "seedvr2-upscale-video";
    public override WorkflowKind Kind => WorkflowKind.Edit;
    public override WorkflowMedia Media => WorkflowMedia.Video;
    public override WorkflowMedia SourceMedia => WorkflowMedia.Video;
    public override bool PromptDirectsMotion => false;
    public override bool HasAudio => true;
    public override bool PreservesComposition => true;
    public override bool RequiresModel => false;
    public override bool TakesPrompt => false;
    public override string OutputSizePolicy => OutputSizePolicies.ExplicitRequested;
    public override IReadOnlyList<ParamSpec> Schema => VideoSchema;

    private static readonly IReadOnlyList<ParamSpec> VideoSchema =
    [
        new() { Key = WorkflowParamKeys.DitModel, Type = ParamType.String, IsModelRef = true },
        new() { Key = WorkflowParamKeys.VaeModel, Type = ParamType.String, IsModelRef = true },
        new() { Key = WorkflowParamKeys.Resolution, Type = ParamType.Int, Min = 16, Max = 16384, Step = 2,
                Label = "Target short edge", Help = "SeedVR2 output size in pixels on the shorter edge; aspect ratio is preserved" },
        new() { Key = WorkflowParamKeys.MaxResolution, Type = ParamType.Int, Min = 0, Max = 16384 },
        new() { Key = WorkflowParamKeys.BatchSize, Type = ParamType.Int, Min = 1, Max = 16384, Step = 4,
                Label = "Temporal batch", Help = "Frames processed together; must be 1, 5, 9, 13, ..." },
        new() { Key = WorkflowParamKeys.UniformBatchSize, Type = ParamType.Bool },
        new() { Key = WorkflowParamKeys.TemporalOverlap, Type = ParamType.Int, Min = 0, Max = 16,
                Label = "Temporal overlap", Help = "Frames blended between consecutive batches" },
        new() { Key = WorkflowParamKeys.PrependFrames, Type = ParamType.Int, Min = 0, Max = 32 },
        new() { Key = WorkflowParamKeys.ColorCorrection, Type = ParamType.Enum,
                Choices = [ ComfyWidgets.ColorMatch.Lab, ComfyWidgets.ColorMatch.Wavelet, ComfyWidgets.ColorMatch.WaveletAdaptive,
                            ComfyWidgets.ColorMatch.Hsv, ComfyWidgets.ColorMatch.Adain, ComfyWidgets.ColorMatch.None ], Label = "Colour match" },
        new() { Key = WorkflowParamKeys.InputNoiseScale, Type = ParamType.Double, Min = 0, Max = 1 },
        new() { Key = WorkflowParamKeys.LatentNoiseScale, Type = ParamType.Double, Min = 0, Max = 1 },
        new() { Key = WorkflowParamKeys.Device, Type = ParamType.String },
        new() { Key = WorkflowParamKeys.OffloadDevice, Type = ParamType.String },
        new() { Key = WorkflowParamKeys.AttentionMode, Type = ParamType.String },
        new() { Key = WorkflowParamKeys.BlocksToSwap, Type = ParamType.Int, Min = 0, Max = 36 },
        new() { Key = WorkflowParamKeys.SwapIoComponents, Type = ParamType.Bool },
        new() { Key = WorkflowParamKeys.CacheModel, Type = ParamType.Bool },
        new() { Key = WorkflowParamKeys.VaeTiled, Type = ParamType.Bool },
        new() { Key = WorkflowParamKeys.VaeTileSize, Type = ParamType.Int },
        new() { Key = WorkflowParamKeys.VaeTileOverlap, Type = ParamType.Int },
        new() { Key = WorkflowParamKeys.EnableDebug, Type = ParamType.Bool },
        .. SeedParam.Schema,
    ];

    private static class VideoNodes
    {
        public const string Source = "10";
        public const string Components = "11";
        public const string Dit = "30";
        public const string Vae = "31";
        public const string Upscale = "32";
        public const string CreateVideo = "40";
        public const string Save = "9";
    }

    protected override ComfyWorkflowGraph Build(SeedVr2VideoParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        if ((p.Resolution & 1) != 0)
        {
            throw new RenderValidationException($"SeedVR2 target short edge must be even; received {p.Resolution}.");
        }

        if ((p.BatchSize - 1) % 4 != 0)
        {
            throw new RenderValidationException($"SeedVR2 temporal batch must follow 4n+1 (1, 5, 9, ...); received {p.BatchSize}.");
        }

        if (p.TemporalOverlap >= p.BatchSize)
        {
            throw new RenderValidationException($"SeedVR2 temporal overlap ({p.TemporalOverlap}) must be smaller than its batch ({p.BatchSize}).");
        }

        string source = inputs.SourceVideoName
            ?? throw new RenderValidationException("SeedVR2 video upscale needs a source clip, but none was provided.");
        string device = p.Device;
        string offload = p.OffloadDevice;
        bool tiled = p.VaeTiled;
        int tile = p.VaeTileSize;
        int tileOverlap = p.VaeTileOverlap;
        long seed = (long)(unchecked((ulong)ComfyGraph.Seed(p.Seed)) % (SeedVr2UpscaleWorkflow.SeedVr2SeedMax + 1UL));

        return new ComfyWorkflowGraph
        {
            [VideoNodes.Source] = new LoadVideo { File = source },
            [VideoNodes.Components] = new GetVideoComponents { Video = LoadVideo.VideoOut(VideoNodes.Source) },
            [VideoNodes.Dit] = new SeedVR2LoadDiTModel
            {
                Model = p.DitModel,
                Device = device,
                BlocksToSwap = p.BlocksToSwap,
                SwapIoComponents = p.SwapIoComponents,
                OffloadDevice = offload,
                CacheModel = p.CacheModel,
                AttentionMode = p.AttentionMode,
            },
            [VideoNodes.Vae] = new SeedVR2LoadVAEModel
            {
                Model = p.VaeModel,
                Device = device,
                EncodeTiled = tiled,
                EncodeTileSize = tile,
                EncodeTileOverlap = tileOverlap,
                DecodeTiled = tiled,
                DecodeTileSize = tile,
                DecodeTileOverlap = tileOverlap,
                OffloadDevice = offload,
                CacheModel = p.CacheModel,
            },
            [VideoNodes.Upscale] = new SeedVR2TemporalVideoUpscaler
            {
                Image = GetVideoComponents.ImagesOut(VideoNodes.Components),
                Dit = SeedVR2LoadDiTModel.Out(VideoNodes.Dit),
                Vae = SeedVR2LoadVAEModel.Out(VideoNodes.Vae),
                Seed = seed,
                Resolution = p.Resolution,
                MaxResolution = p.MaxResolution,
                BatchSize = p.BatchSize,
                UniformBatchSize = p.UniformBatchSize,
                TemporalOverlap = p.TemporalOverlap,
                PrependFrames = p.PrependFrames,
                ColorCorrection = p.ColorCorrection,
                InputNoiseScale = p.InputNoiseScale,
                LatentNoiseScale = p.LatentNoiseScale,
                OffloadDevice = offload,
                EnableDebug = p.EnableDebug,
            },
            [VideoNodes.CreateVideo] = new CreateVideoFromComponents
            {
                Images = SeedVR2TemporalVideoUpscaler.Out(VideoNodes.Upscale),
                Fps = GetVideoComponents.FpsOut(VideoNodes.Components),
                Audio = GetVideoComponents.AudioOut(VideoNodes.Components),
                BitDepth = GetVideoComponents.BitDepthOut(VideoNodes.Components),
            },
            [VideoNodes.Save] = new SaveVideo
            {
                Video = CreateVideoFromComponents.Out(VideoNodes.CreateVideo),
                FilenamePrefix = OutputPrefixes.Edit,
                Format = ComfyWidgets.SaveFormat.Auto,
                Codec = ComfyWidgets.VideoCodec.Auto,
            },
        };
    }
}
