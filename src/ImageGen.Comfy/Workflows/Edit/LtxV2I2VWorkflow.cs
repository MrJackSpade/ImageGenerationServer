using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>LTX-2 (19B) image-to-video. Same LTXV sampler chain as the 0.9.8 editor, but the model is a GGUF unet
/// (UnetLoaderGGUF) and the text encoder is the Gemma + LTX-connectors pair (DualCLIPLoader, type "ltxv") — both
/// supplied by the shared <see cref="EditWorkflow{TParams}.LoadModel"/> head when the config sets loader=unet_gguf,
/// dual=true. The 11GB distilled Q4 GGUF + 8.8GB Gemma encoder total ~20GB; no offload flags. Distilled: ~8 steps,
/// cfg 1. Animates anime natively without a LoRA.</summary>
public sealed class LtxV2I2VWorkflow : EditWorkflow<LtxV2I2VParams>
{
    public override string Name => "ltx2-i2v";
    public override WorkflowMedia Media => WorkflowMedia.Video;
    /// <summary>LTX VAE: 8× temporal compression → valid clip lengths are 8n+1 (mirrors the node's length step=8).</summary>
    public override FrameRule? FrameRule => new(1, 8);

    /// <summary>This workflow's own node ids.</summary>
    private const string Scale = "51";
    private const string Size = "52";
    private const string Positive = "13";
    private const string Negative = "12";
    private const string ImgToVideo = "53";
    private const string Conditioning = "54";
    private const string Scheduler = "55";
    private const string SamplerSelect = "56";
    private const string Sampler = "3";
    private const string Decode = "8";
    private const string Save = "9";

    protected override ComfyWorkflowGraph Build(LtxV2I2VParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new ComfyWorkflowGraph();
        LoadModel(g, p.Loader, p.WeightDtype, p.ClipType, req, inputs, out var model0, out var clip0, out var vae0);
        model0 = ComfyGraph.ApplyLora(g, model0, p.Lora, p.LoraStrength);   // optional anime-style LoRA on the LTX-2 model
        long seed = ComfyGraph.Seed(p.Seed);
        int frames = p.Length;
        double fps = p.Fps;
        double budgetMp = 0.4;   // LTX-2's native i2v megapixel budget — always applied (the source is scaled to it)
        g[Scale] = new ImageScaleToTotalPixels { Image = LoadImage.ImageOut(Nodes.Source), UpscaleMethod = "lanczos", Megapixels = budgetMp, ResolutionSteps = 32 };
        g[Size] = new GetImageSize { Image = ImageScaleToTotalPixels.Out(Scale) };
        g[Positive] = new CLIPTextEncode { Text = inputs.Positive, Clip = clip0 };
        g[Negative] = new CLIPTextEncode { Text = inputs.Negative ?? "", Clip = clip0 };
        g[ImgToVideo] = new LTXVImgToVideo { Positive = CLIPTextEncode.Out(Positive), Negative = CLIPTextEncode.Out(Negative), Vae = vae0, Image = ImageScaleToTotalPixels.Out(Scale), Width = GetImageSize.WidthOut(Size), Height = GetImageSize.HeightOut(Size), Length = frames, BatchSize = 1, Strength = 1.0 };
        g[Conditioning] = new LTXVConditioning { Positive = LTXVImgToVideo.PositiveOut(ImgToVideo), Negative = LTXVImgToVideo.NegativeOut(ImgToVideo), FrameRate = fps };
        g[Scheduler] = new LTXVScheduler { Steps = p.Steps, MaxShift = 2.05, BaseShift = 0.95, Stretch = true, Terminal = 0.1, Latent = LTXVImgToVideo.LatentOut(ImgToVideo) };
        g[SamplerSelect] = new KSamplerSelect { SamplerName = ComfyGraph.MapSampler(p.Sampler) };
        g[Sampler] = new SamplerCustom { Model = model0, AddNoise = true, NoiseSeed = seed, Cfg = p.Cfg, Positive = LTXVConditioning.PositiveOut(Conditioning), Negative = LTXVConditioning.NegativeOut(Conditioning), Sampler = KSamplerSelect.Out(SamplerSelect), Sigmas = LTXVScheduler.Out(Scheduler), LatentImage = LTXVImgToVideo.LatentOut(ImgToVideo) };
        g[Decode] = new VAEDecode { Samples = SamplerCustom.Out(Sampler), Vae = vae0 };
        g[Save] = new SaveAnimatedWEBPLiteralFps { Images = VAEDecode.Out(Decode), FilenamePrefix = "forgemcp_edit", Fps = fps, Lossless = false, Quality = 80, Method = "default" };
        return g;
    }
}

/// <summary>LTX-2 i2v parameters — the shared loader head knobs (<c>loader</c>/<c>weight_dtype</c>/<c>clip_type</c> for
/// the typed <c>LoadModel</c>, driven by loader=unet_gguf + dual=true), the <c>SamplerCustom</c> settings, the clip
/// length + playback fps, and the optional preset LoRA. LTX runs its own LTXVScheduler, so no <c>scheduler</c> param is
/// read. The <c>*Req</c> reads are <c>required</c>; <c>weight_dtype</c>/<c>clip_type</c> are nullable strings;
/// <c>lora</c> is a nullable model-ref and <c>lora_strength</c> a defaulted double (only read when a LoRA is set);
/// <c>seed</c> is the app's single-sourced seed (defaulted).</summary>
public sealed record LtxV2I2VParams
{
    [JsonPropertyName(WorkflowParamKeys.Loader)]       public required string Loader { get; init; }
    [JsonPropertyName(WorkflowParamKeys.WeightDtype)]  public string? WeightDtype { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ClipType)]     public string? ClipType { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Steps)]        public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Cfg)]          public required double Cfg { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Sampler)]      public required string Sampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Length)]       public required int Length { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Fps)]          public required double Fps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Lora)]         public string? Lora { get; init; }
    [JsonPropertyName(WorkflowParamKeys.LoraStrength)] public double LoraStrength { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)]         public long Seed { get; init; }
}
