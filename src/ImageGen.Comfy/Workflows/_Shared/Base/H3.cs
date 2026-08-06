using ImageGen.Application.Rendering;

namespace ImageGen.Comfy;

internal static class H3
{
    /// <summary>The audio VAE — a SECOND vae slot beyond the video VAE (<c>req.Vae</c>). A model-ref param resolved to
    /// this machine's bound file (linked in the config's <c>extra</c>), mirroring how the MoE/SR workflows carry a
    /// second model file.</summary>
    public static readonly ParamSpec[] ExtraSchema = new ParamSpec[]
    {
        new() { Key = WorkflowParamKeys.AudioVae, Type = ParamType.String, IsModelRef = true, Label = "Audio VAE" },
    };

    /// <summary>First id for the per-picker-reference LoadImage nodes in ref2v (the source is ref_image_0, in-place
    /// at <see cref="H3Nodes.Source"/>); each picker reference gets <c>RefImageBase + i</c>. Kept clear of every node id
    /// in <see cref="H3Nodes"/>. A const int (not a node-id string), so it stays out of the pure <see cref="H3Nodes"/>
    /// holder.</summary>
    private const int RefImageBase = 60;

    /// <summary>The shared T2V/I2V graph (typed #93). <paramref name="i2v"/>: the source image is the first frame and
    /// the clip size derives from it (scaled to H3's ~1 MP budget); otherwise the size is the aspect map's
    /// <paramref name="t2vDims"/>. The graph is otherwise identical — one H3 node, one distilled sampler chain, dual
    /// (video+audio) decode, one mp4-with-audio. The scalar knobs are read TYPED off each workflow's params record and
    /// passed in; <paramref name="seed"/> is already resolved (<c>ComfyGraph.Seed</c>) and
    /// <paramref name="sampler"/>/<paramref name="scheduler"/> are the RAW Forge names (mapped here).</summary>
    public static ComfyWorkflowGraph Build(ResolvedRequirements req, WorkflowInputs inputs, H3Mode mode,
        string audioVae, int length, double fps, long seed, int steps, string sampler, string scheduler, (int w, int h)? t2vDims, int refMax = 0)
    {
        ComfyWorkflowGraph g = new();

        // Loaders. Diffusion via DiffusionLoaderNode → plain UNETLoader (int8 ConvRot loads natively, weight_dtype
        // default keeps its INT8). Qwen3-VL text encoder through CLIPLoader type "minimax". TWO VAEs: video (frames)
        // and audio (the native stereo track); the audio VAE is the audio_vae model-ref slot.
        g[H3Nodes.Model] = ComfyGraph.DiffusionLoaderNode(req.RequiredCheckpoint());   // H3 sets no weight_dtype → AutoWeightDtype (native INT8 ConvRot)
        Output<Slot.Model> model = UNETLoader.ModelOut(H3Nodes.Model);
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
                    g[H3Nodes.ScaledSource] = new ImageScaleToTotalPixels { Image = LoadImage.ImageOut(H3Nodes.Source), UpscaleMethod = ComfyWidgets.Upscale.Lanczos, Megapixels = 1.0, ResolutionSteps = 32 };
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
                        g[H3Nodes.ScaledEndFrame] = new ImageScaleToTotalPixels { Image = LoadImage.ImageOut(H3Nodes.EndFrame), UpscaleMethod = ComfyWidgets.Upscale.Lanczos, Megapixels = 1.0, ResolutionSteps = 32 };
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
                    g[H3Nodes.ScaledSource] = new ImageScaleToTotalPixels { Image = LoadImage.ImageOut(H3Nodes.Source), UpscaleMethod = ComfyWidgets.Upscale.Lanczos, Megapixels = 1.0, ResolutionSteps = 32 };
                    g[H3Nodes.SourceSize] = new GetImageSize { Image = ImageScaleToTotalPixels.Out(H3Nodes.ScaledSource) };

                    IReadOnlyList<string> refNames = inputs.ReferenceImageNames;
                    if (refNames.Count > refMax)
                    {
                        throw new RenderValidationException($"This configuration accepts at most {refMax} reference image(s); got {refNames.Count}.");
                    }

                    List<Output<Slot.Image>> refs = new(refNames.Count + 1) { LoadImage.ImageOut(H3Nodes.Source) };
                    for (int i = 0; i < refNames.Count; i++)
                    {
                        string id = (RefImageBase + i).ToString();
                        g[id] = new LoadImage { Image = refNames[i] };
                        refs.Add(LoadImage.ImageOut(id));
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
                        RefImages = MiniMaxH3ReferenceToVideo.Refs(refs),
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
