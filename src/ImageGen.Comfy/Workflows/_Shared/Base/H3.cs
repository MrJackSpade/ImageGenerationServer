using ImageGen.Application.Rendering;
using ImageGen.Domain;

namespace ImageGen.Comfy;

internal static class H3
{
    /// <summary>H3's default render budget (~1&#160;MP): the i2v/ref2v source is scaled to this megapixel count on a
    /// 32-px grid and the clip renders at that size. Since #186 the i2v/ref2v budget is a per-config <c>megapixels</c>
    /// control passed into <see cref="Build"/> (defaulting to this value in the configs); this const is the nominal
    /// value the T2V path (which sizes from its aspect map, not a source budget) passes through unused.</summary>
    public const double BudgetMp = 1.0;
    public const int BudgetSteps = 32;

    /// <summary>The audio VAE — a SECOND vae slot beyond the video VAE (<c>req.Vae</c>). A model-ref param resolved to
    /// this machine's bound file (linked in the config's <c>extra</c>), mirroring how the MoE/SR workflows carry a
    /// second model file.</summary>
    public static readonly ParamSpec[] ExtraSchema =
    [
        new() { Key = WorkflowParamKeys.AudioVae, Type = ParamType.String, IsModelRef = true, Label = "Audio VAE" },
    ];

    /// <summary>First id for the per-picker-reference LoadImage nodes in ref2v (the source is ref_image_0, in-place
    /// at <see cref="H3Nodes.Source"/>); each picker reference gets <c>RefImageBase + i</c>. Kept clear of every node id
    /// in <see cref="H3Nodes"/>. A const int (not a node-id string), so it stays out of the pure <see cref="H3Nodes"/>
    /// holder. The video/audio reference bases follow, each in its own decade so the ranges can't collide.</summary>
    private const int RefImageBase = 60;

    /// <summary>First id for the per-video-reference <c>LoadVideo</c> nodes; the matching <c>GetVideoComponents</c> nodes
    /// (which split each clip to IMAGE frames for the node's <c>ref_videos</c> input) start at <see cref="RefVideoCompBase"/>.</summary>
    private const int RefVideoLoadBase = 70;

    /// <summary>First id for the per-video-reference <c>GetVideoComponents</c> nodes.</summary>
    private const int RefVideoCompBase = 80;

    /// <summary>First id for the per-audio-reference <c>LoadAudio</c> nodes (the node's <c>ref_audios</c> input).</summary>
    private const int RefAudioBase = 90;

    /// <summary>The <see cref="MiniMaxH3ReferenceToVideo"/> node's structural autogrow caps: up to 3 driving videos and 3
    /// driving audios (image refs are capped per-config by <c>reference_max</c>). A last-resort graph-integrity guard —
    /// the accepted-per-kind policy is enforced upstream at enqueue against the workflow's declared reference types.</summary>
    private const int MaxVideoRefs = 3;
    private const int MaxAudioRefs = 3;

    /// <summary>The shared T2V/I2V graph (typed #93). <paramref name="i2v"/>: the source image is the first frame and
    /// the clip size derives from it (scaled to H3's ~1 MP budget); otherwise the size is the aspect map's
    /// <paramref name="t2vDims"/>. The graph is otherwise identical — one H3 node, one distilled sampler chain, dual
    /// (video+audio) decode, one mp4-with-audio. The scalar knobs are read TYPED off each workflow's params record and
    /// passed in; <paramref name="seed"/> is already resolved (<c>ComfyGraph.Seed</c>) and
    /// <paramref name="sampler"/>/<paramref name="scheduler"/> are the RAW Forge names (mapped here).</summary>
    public static ComfyWorkflowGraph Build(ResolvedRequirements req, WorkflowInputs inputs, H3Mode mode,
        string audioVae, int length, double fps, long seed, int steps, string sampler, string scheduler,
        string? lora, double loraStrength, double budgetMp, (int w, int h)? t2vDims, int refMax = 0)
    {
        ComfyWorkflowGraph g = new();

        // Loaders. Diffusion via DiffusionLoaderNode → plain UNETLoader (int8 ConvRot loads natively, weight_dtype
        // default keeps its INT8). Qwen3-VL text encoder through CLIPLoader type "minimax". TWO VAEs: video (frames)
        // and audio (the native stereo track); the audio VAE is the audio_vae model-ref slot. An optional model-only
        // LoRA (the Turbo configs' distilled low-step LoRA) sits between the loader and everything downstream.
        g[H3Nodes.Model] = ComfyGraph.DiffusionLoaderNode(req.RequiredCheckpoint());   // H3 sets no weight_dtype → AutoWeightDtype (native INT8 ConvRot)
        Output<Slot.Model> model = ComfyGraph.ApplyLora(g, UNETLoader.ModelOut(H3Nodes.Model), lora, loraStrength, H3Nodes.Lora);
        g[H3Nodes.Clip] = new CLIPLoader { ClipName = req.TextEncoder(0), Type = ComfyWidgets.ClipType.Minimax, Device = ComfyWidgets.Device.Default };
        Output<Slot.Clip> clip = CLIPLoader.ClipOut(H3Nodes.Clip);
        g[H3Nodes.VideoVae] = new VAELoader { VaeName = req.RequiredVae() };
        Output<Slot.Vae> videoVae = VAELoader.VaeOut(H3Nodes.VideoVae);
        g[H3Nodes.AudioVae] = new VAELoader { VaeName = audioVae };
        Output<Slot.Vae> audioVaeRef = VAELoader.VaeOut(H3Nodes.AudioVae);

        // The single H3 conditioning+latent node. It encodes the prompt itself and emits (positive CONDITIONING, LATENT).
        switch (mode)
        {
            case H3Mode.I2V:
                {
                    // Source = first frame. Scale to H3's ~1 MP budget (multiple of 32) and use those dims as the clip size, so
                    // the clip keeps the source's aspect inside H3's canvas. An optional END frame pins the last frame.
                    g[H3Nodes.Source] = new LoadImage { Image = inputs.SourceImageName ?? throw new RenderValidationException("MiniMax-H3 image→video needs a source image (the first frame), but none was provided.") };
                    g[H3Nodes.ScaledSource] = new ImageScaleToTotalPixels { Image = LoadImage.ImageOut(H3Nodes.Source), UpscaleMethod = ComfyWidgets.Upscale.Lanczos, Megapixels = budgetMp, ResolutionSteps = H3.BudgetSteps };
                    g[H3Nodes.SourceSize] = new GetImageSize { Image = ImageScaleToTotalPixels.Out(H3Nodes.ScaledSource) };
                    Output<Slot.Image>? lastFrame = null;
                    if (!string.IsNullOrEmpty(inputs.EndImageName))
                    {
                        // Scale the end frame through the SAME node as the first frame (:99), so both reach
                        // MiniMaxH3ImageToVideo at identical dims. The node resizes first_frame to width/height with
                        // crop="disabled" and last_frame with crop="center"; the first frame arrives already at those
                        // exact dims (a no-op), so a raw end frame gets a lone cover/center-crop and lands at a different
                        // framing. A same-image loop (#110) then stretches instead of holding still. Pre-scaling the end
                        // frame identically makes the node's last-frame resize a no-op too → a clean static loop.
                        g[H3Nodes.EndFrame] = new LoadImage { Image = inputs.EndImageName };
                        g[H3Nodes.ScaledEndFrame] = new ImageScaleToTotalPixels { Image = LoadImage.ImageOut(H3Nodes.EndFrame), UpscaleMethod = ComfyWidgets.Upscale.Lanczos, Megapixels = budgetMp, ResolutionSteps = H3.BudgetSteps };
                        lastFrame = ImageScaleToTotalPixels.Out(H3Nodes.ScaledEndFrame);
                    }
                    // First/last-frame loop vs plain i2v is a choice of NODE, not a nullable input: the end frame either pins
                    // the ending (its own record) or there is none.
                    g[H3Nodes.Encode] = lastFrame is { } endFrame
                        ? new MiniMaxH3FirstLastFrameToVideo
                        {
                            Clip = clip,
                            Vae = videoVae,
                            Prompt = inputs.Positive,
                            Length = length,
                            Width = GetImageSize.WidthOut(H3Nodes.SourceSize),
                            Height = GetImageSize.HeightOut(H3Nodes.SourceSize),
                            FirstFrame = ImageScaleToTotalPixels.Out(H3Nodes.ScaledSource),
                            LastFrame = endFrame,
                        }
                        : new MiniMaxH3ImageToVideoI2V
                        {
                            Clip = clip,
                            Vae = videoVae,
                            Prompt = inputs.Positive,
                            Length = length,
                            Width = GetImageSize.WidthOut(H3Nodes.SourceSize),
                            Height = GetImageSize.HeightOut(H3Nodes.SourceSize),
                            FirstFrame = ImageScaleToTotalPixels.Out(H3Nodes.ScaledSource),
                        };
                    break;
                }
            case H3Mode.Ref2V:
                {
                    // Reference→video: the open image is the PRIMARY subject reference (ref_image_0) and sets the output
                    // canvas (scaled to H3's ~1 MP budget, exactly like i2v); any picker references follow as
                    // ref_image_1…N. They condition the subject/identity — NOT a first frame — so they enter the ref node's
                    // autogrow ref_images input, which resizes each internally (down only). The audio VAE is a direct input.
                    g[H3Nodes.Source] = new LoadImage { Image = inputs.SourceImageName ?? throw new RenderValidationException("MiniMax-H3 reference→video needs a source image (the primary subject reference), but none was provided.") };
                    g[H3Nodes.ScaledSource] = new ImageScaleToTotalPixels { Image = LoadImage.ImageOut(H3Nodes.Source), UpscaleMethod = ComfyWidgets.Upscale.Lanczos, Megapixels = budgetMp, ResolutionSteps = H3.BudgetSteps };
                    g[H3Nodes.SourceSize] = new GetImageSize { Image = ImageScaleToTotalPixels.Out(H3Nodes.ScaledSource) };

                    // Partition the typed references by media kind. Each family enters its own autogrow input on the
                    // node: image stills → ref_images, driving videos → ref_videos (as decoded frame batches), driving
                    // audio → ref_audios. The '<Picture i>'/'<Video k>'/'<Audio j>' prompt tags reference them by index.
                    IReadOnlyList<string> imageRefNames = [.. inputs.References.Where(r => r.Kind == ReferenceKind.Image).Select(r => r.Name)];
                    IReadOnlyList<string> videoRefNames = [.. inputs.References.Where(r => r.Kind == ReferenceKind.Video).Select(r => r.Name)];
                    IReadOnlyList<string> audioRefNames = [.. inputs.References.Where(r => r.Kind == ReferenceKind.Audio).Select(r => r.Name)];
                    if (imageRefNames.Count > refMax)
                    {
                        throw new RenderValidationException($"This configuration accepts at most {refMax} reference image(s); got {imageRefNames.Count}.");
                    }

                    if (videoRefNames.Count > MaxVideoRefs)
                    {
                        throw new RenderValidationException($"MiniMax-H3 reference→video accepts at most {MaxVideoRefs} reference video(s); got {videoRefNames.Count}.");
                    }

                    if (audioRefNames.Count > MaxAudioRefs)
                    {
                        throw new RenderValidationException($"MiniMax-H3 reference→video accepts at most {MaxAudioRefs} reference audio clip(s); got {audioRefNames.Count}.");
                    }

                    // Image references: the source is ref_image_0 (already loaded), the picker stills follow.
                    List<Output<Slot.Image>> imageEdges = new(imageRefNames.Count + 1) { LoadImage.ImageOut(H3Nodes.Source) };
                    for (int i = 0; i < imageRefNames.Count; i++)
                    {
                        string id = (RefImageBase + i).ToString();
                        g[id] = new LoadImage { Image = imageRefNames[i] };
                        imageEdges.Add(LoadImage.ImageOut(id));
                    }

                    // Video references: the node's ref_videos input is IMAGE frames and the paired ref_video_audios is
                    // that same video's AUDIO, so each clip is decoded once (LoadVideo → GetVideoComponents) and its
                    // frames (output 0) AND soundtrack (output 1) are wired to the same-numbered slots — a video
                    // reference drives BOTH motion and its own sound.
                    List<Output<Slot.Image>> videoFrameEdges = new(videoRefNames.Count);
                    List<Output<AudioSlot>> videoAudioEdges = new(videoRefNames.Count);
                    for (int i = 0; i < videoRefNames.Count; i++)
                    {
                        string loadId = (RefVideoLoadBase + i).ToString();
                        string compId = (RefVideoCompBase + i).ToString();
                        g[loadId] = new LoadVideo { File = videoRefNames[i] };
                        g[compId] = new GetVideoComponents { Video = LoadVideo.VideoOut(loadId) };
                        videoFrameEdges.Add(GetVideoComponents.ImagesOut(compId));
                        videoAudioEdges.Add(GetVideoComponents.AudioOut(compId));
                    }

                    // Audio references: standalone driving clips → ref_audios (encoded through audio_vae inside the node).
                    List<Output<AudioSlot>> audioEdges = new(audioRefNames.Count);
                    for (int i = 0; i < audioRefNames.Count; i++)
                    {
                        string id = (RefAudioBase + i).ToString();
                        g[id] = new LoadAudio { Audio = audioRefNames[i] };
                        audioEdges.Add(LoadAudio.AudioOut(id));
                    }

                    g[H3Nodes.Encode] = new MiniMaxH3ReferenceToVideo
                    {
                        Clip = clip,
                        Vae = videoVae,
                        AudioVae = audioVaeRef,
                        Prompt = inputs.Positive,
                        Length = length,
                        Width = GetImageSize.WidthOut(H3Nodes.SourceSize),
                        Height = GetImageSize.HeightOut(H3Nodes.SourceSize),
                        RefImageSize = ComfyWidgets.RefImageSize.Match,
                        RefInputs = MiniMaxH3ReferenceToVideo.Refs(imageEdges, videoFrameEdges, videoAudioEdges, audioEdges),
                    };
                    break;
                }
            default:
                {
                    (int w, int h) = t2vDims ?? throw new RenderValidationException("MiniMax-H3 text→video needs a render size, but none was resolved.");
                    g[H3Nodes.Encode] = new MiniMaxH3ImageToVideoT2V { Clip = clip, Vae = videoVae, Prompt = inputs.Positive, Length = length, Width = w, Height = h };
                    break;
                }
        }

        Output<Slot.Conditioning> positive = new(H3Nodes.Encode, 0);
        Output<Slot.Latent> latent = new(H3Nodes.Encode, 1);

        // Distilled sampling: BasicGuider (no CFG, no negative) + a res_multistep SamplerCustomAdvanced chain.
        g[H3Nodes.Scheduler] = new BasicScheduler { Model = model, Scheduler = ComfyGraph.MapScheduler(scheduler), Steps = steps, Denoise = 1.0 };
        g[H3Nodes.SamplerSelect] = new KSamplerSelect { SamplerName = ComfyGraph.MapSampler(sampler) };
        g[H3Nodes.Noise] = new RandomNoise { NoiseSeed = seed };
        g[H3Nodes.Guider] = new BasicGuider { Model = model, Conditioning = positive };
        g[H3Nodes.Sampler] = new SamplerCustomAdvanced { Noise = RandomNoise.Out(H3Nodes.Noise), Guider = BasicGuider.Out(H3Nodes.Guider), Sampler = KSamplerSelect.Out(H3Nodes.SamplerSelect), Sigmas = BasicScheduler.Out(H3Nodes.Scheduler), LatentImage = latent };

        // Dual decode → one mp4 with audio. The SAME latent decodes to frames (video VAE) and to the native stereo
        // track (audio VAE); CreateVideo muxes them; SaveVideo writes a real mp4 (format/codec auto = h264/aac).
        g[H3Nodes.VideoDecode] = new VAEDecode { Samples = SamplerCustomAdvanced.Out(H3Nodes.Sampler), Vae = videoVae };
        g[H3Nodes.AudioDecode] = new VAEDecodeAudio { Samples = SamplerCustomAdvanced.Out(H3Nodes.Sampler), Vae = audioVaeRef };
        g[H3Nodes.CreateVideo] = new CreateVideo { Images = VAEDecode.Out(H3Nodes.VideoDecode), Fps = fps, Audio = VAEDecodeAudio.Out(H3Nodes.AudioDecode) };
        g[H3Nodes.Save] = new SaveVideo { Video = CreateVideo.Out(H3Nodes.CreateVideo), FilenamePrefix = mode == H3Mode.T2V ? OutputPrefixes.Generate : OutputPrefixes.Edit, Format = ComfyWidgets.SaveFormat.Auto, Codec = ComfyWidgets.VideoCodec.Auto };
        return g;
    }
}