using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>LTX-Video image-to-video: fast distilled model; source conditions frame 0. LTX has no CLIP in the
/// checkpoint — it loads an external T5.</summary>
public sealed class LtxvI2VWorkflow : EditWorkflow<LtxvI2VParams>
{
    public override string Name => "ltxv-i2v";
    public override WorkflowMedia Media => WorkflowMedia.Video;
    /// <summary>LTX VAE: 8× temporal compression → valid clip lengths are 8n+1 (mirrors the node's length step=8).</summary>
    public override FrameRule? FrameRule => new(1, 8);

    /// <summary>Own node ids (source LoadImage is the inherited <c>Nodes.Source</c>).</summary>
    private const string T5Loader = "50";
    private const string ScaledSource = "51";
    private const string SourceSize = "52";
    private const string Positive = "13";
    private const string Negative = "12";
    private const string ImgToVideo = "53";
    private const string Conditioning = "54";
    private const string Scheduler = "55";
    private const string SamplerSelect = "56";
    private const string Sampler = "3";
    private const string Decode = "8";
    private const string Save = "9";

    protected override ComfyWorkflowGraph Build(LtxvI2VParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new ComfyWorkflowGraph();
        LoadModel(g, p.Loader, p.WeightDtype, p.ClipType, req, inputs, out Output<Slot.Model> model0, out _, out Output<Slot.Vae> vae0);
        model0 = ComfyGraph.ApplyLora(g, model0, p.Lora, p.LoraStrength);   // optional anime-style LoRA on the LTX model
        long seed = ComfyGraph.Seed(p.Seed);
        int frames = p.Length;
        double fps = p.Fps;
        // LTX loads its own external T5 (clip_type "ltxv").
        g[T5Loader] = new CLIPLoader { ClipName = req.TextEncoder(0), Type = ComfyWidgets.ClipType.Ltxv, Device = ComfyWidgets.Device.Default };
        Output<Slot.Clip> ltxClip = CLIPLoader.ClipOut(T5Loader);
        double budgetMp = 0.39;   // LTX's native i2v megapixel budget — always applied (the source is scaled to it)
        g[ScaledSource] = new ImageScaleToTotalPixels { Image = LoadImage.ImageOut(Nodes.Source), UpscaleMethod = ComfyWidgets.Upscale.Lanczos, Megapixels = budgetMp, ResolutionSteps = 32 };
        g[SourceSize] = new GetImageSize { Image = ImageScaleToTotalPixels.Out(ScaledSource) };
        g[Positive] = new CLIPTextEncode { Text = inputs.Positive, Clip = ltxClip };
        g[Negative] = new CLIPTextEncode { Text = inputs.Negative ?? "", Clip = ltxClip };
        g[ImgToVideo] = new LTXVImgToVideo { Positive = CLIPTextEncode.Out(Positive), Negative = CLIPTextEncode.Out(Negative), Vae = vae0, Image = ImageScaleToTotalPixels.Out(ScaledSource), Width = GetImageSize.WidthOut(SourceSize), Height = GetImageSize.HeightOut(SourceSize), Length = frames, BatchSize = 1, Strength = 1.0 };
        g[Conditioning] = new LTXVConditioning { Positive = LTXVImgToVideo.PositiveOut(ImgToVideo), Negative = LTXVImgToVideo.NegativeOut(ImgToVideo), FrameRate = fps };
        g[Scheduler] = new LTXVScheduler { Steps = p.Steps, MaxShift = 2.05, BaseShift = 0.95, Stretch = true, Terminal = 0.1, Latent = LTXVImgToVideo.LatentOut(ImgToVideo) };
        g[SamplerSelect] = new KSamplerSelect { SamplerName = ComfyGraph.MapSampler(p.Sampler) };
        g[Sampler] = new SamplerCustom { Model = model0, AddNoise = true, NoiseSeed = seed, Cfg = p.Cfg, Positive = LTXVConditioning.PositiveOut(Conditioning), Negative = LTXVConditioning.NegativeOut(Conditioning), Sampler = KSamplerSelect.Out(SamplerSelect), Sigmas = LTXVScheduler.Out(Scheduler), LatentImage = LTXVImgToVideo.LatentOut(ImgToVideo) };
        g[Decode] = new VAEDecode { Samples = SamplerCustom.Out(Sampler), Vae = vae0 };
        g[Save] = new SaveAnimatedWEBPLiteralFps { Images = VAEDecode.Out(Decode), FilenamePrefix = OutputPrefixes.Edit, Fps = fps, Lossless = false, Quality = 80, Method = ComfyWidgets.WebpMethod.Default };
        return g;
    }
}

/// <summary>LTX-Video i2v parameters — the shared loader head knobs (<c>loader</c>/<c>weight_dtype</c>/<c>clip_type</c>
/// for the typed <c>LoadModel</c>), the <c>SamplerCustom</c> settings, the clip length + playback fps, and the optional
/// preset LoRA. LTX runs its own LTXVScheduler, so no <c>scheduler</c> param is read. The <c>*Req</c> reads are
/// <c>required</c>; <c>weight_dtype</c>/<c>clip_type</c> are nullable strings; <c>lora</c> is a nullable model-ref and
/// <c>lora_strength</c> a defaulted double (only read when a LoRA is set); <c>seed</c> is the app's single-sourced
/// seed (defaulted).</summary>
public sealed record LtxvI2VParams
{
    [JsonPropertyName(WorkflowParamKeys.Loader)]       public required string Loader { get; init; }
    [JsonPropertyName(WorkflowParamKeys.WeightDtype)]  public string? WeightDtype { get; init; }
    [JsonPropertyName(WorkflowParamKeys.ClipType)]     public string? ClipType { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Steps)]
    [Range(ParamBounds.StepsMin, ParamBounds.StepsMax)] public required int Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Cfg)]
    [Range(ParamBounds.CfgMin, ParamBounds.CfgMax)]    public required double Cfg { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Sampler)]      public required string Sampler { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Length)]       public required int Length { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Fps)]          public required double Fps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Lora)]         public string? Lora { get; init; }
    [JsonPropertyName(WorkflowParamKeys.LoraStrength)]
    [Range(ParamBounds.EditLoraStrengthMin, ParamBounds.EditLoraStrengthMax)] public double LoraStrength { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Seed)]         public long Seed { get; init; }
}
