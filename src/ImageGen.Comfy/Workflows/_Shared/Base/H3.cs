using ImageGen.Application.Rendering;
using ImageGen.Domain;

namespace ImageGen.Comfy;

/// <summary>
/// MiniMax-H3 — an omni-modal video model with NATIVE stereo audio (voice/SFX/music generated in the same forward
/// pass, not layered on after). TWO task-specific diffusion checkpoints share one graph shape: the "fl2va" model
/// serves both text→video and image→video (the two differ only in whether a first frame is fed to the H3
/// conditioning node), and the separate "ref2va" model serves reference→video. The H3-specific conditioning nodes
/// encode the prompt themselves (no separate CLIPTextEncode) and emit (positive, latent). Distilled sampling
/// (BasicGuider, no CFG/negative) through a res_multistep SamplerCustomAdvanced chain, exactly like the official
/// ComfyUI templates (video_minimax_h3_{t2v,i2v}.json).
///
/// <para>UNLIKE every other video model here, H3 does NOT end in the silent <c>SaveAnimatedWEBP</c> — its whole
/// point is the audio, which WEBP cannot carry. The video latent decodes to frames and the SAME latent decodes to
/// audio; <c>CreateVideo</c> muxes them and <c>SaveVideo</c> writes a real mp4 with a baked-in stereo track. The
/// render pipeline stores/serves that mp4 by content-type (see <c>RenderOrchestrator.RunSlotAsync</c> +
/// <c>/image/{id}/mp4</c>), audio intact.</para>
///
/// <para>Weights: the int8-ConvRot diffusion + int8-ConvRot Qwen3-VL 32B text encoder (native tensor-core INT8 on
/// Ampere; the upstream template's nvfp4_awq encoder is Blackwell-only), both loaded through the plain
/// <c>UNETLoader</c>/<c>CLIPLoader</c> that read the embedded ConvRot metadata. Requires ComfyUI ≥ v0.30.1, which
/// adds <c>MiniMaxH3ImageToVideo</c> and the CLIPLoader <c>minimax</c> type.</para>
///
/// <para>The shared T2V/I2V/Ref2V graph is typed (#93). Each task has its own entry point taking only the sizing
/// input that task actually uses (#208): <see cref="BuildT2V"/> takes the aspect map's resolved dims, while
/// <see cref="BuildI2V"/>/<see cref="BuildRef2V"/> take the per-config <c>megapixels</c> budget the source is
/// scaled to. The graph is otherwise identical — one H3 node, one distilled sampler chain, dual (video+audio)
/// decode, one mp4-with-audio. The scalar knobs are read TYPED off each workflow's params record and passed in;
/// <c>seed</c> is already resolved (<c>ComfyGraph.Seed</c>) and <c>sampler</c>/<c>scheduler</c> are the RAW Forge
/// names (mapped here).</para>
/// </summary>
internal static class H3
{
    /// <summary>The i2v/ref2v source is scaled to the per-config <c>megapixels</c> budget on this 32-px grid and the
    /// clip renders at that size (#186).</summary>
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

    /// <summary>The shared loader head: graph + the outputs every task's encode section wires from.</summary>
    private readonly record struct Rig(ComfyWorkflowGraph Graph, Output<Slot.Model> Model, Output<Slot.Clip> Clip,
        Output<Slot.Vae> VideoVae, Output<Slot.Vae> AudioVae);

    /// <summary>Text→video: no source, the clip size is the aspect map's resolved <paramref name="dims"/>.</summary>
    public static ComfyWorkflowGraph BuildT2V(ResolvedRequirements req, WorkflowInputs inputs,
        string audioVae, int length, double fps, long seed, int steps, string sampler, string scheduler,
        string? lora, double loraStrength, (int w, int h) dims)
    {
        Rig rig = Loaders(req, audioVae, lora, loraStrength);
        rig.Graph[H3Nodes.Encode] = new MiniMaxH3ImageToVideoT2V { Clip = rig.Clip, Vae = rig.VideoVae, Prompt = inputs.Positive, Length = length, Width = dims.w, Height = dims.h };
        return Finish(rig, fps, seed, steps, sampler, scheduler, OutputPrefixes.Generate);
    }

    /// <summary>Image→video: the source image is the first frame and the clip size derives from it (scaled to the
    /// per-config <paramref name="budgetMp"/>).</summary>
    public static ComfyWorkflowGraph BuildI2V(ResolvedRequirements req, WorkflowInputs inputs,
        string audioVae, int length, double fps, long seed, int steps, string sampler, string scheduler,
        string? lora, double loraStrength, double budgetMp)
    {
        Rig rig = Loaders(req, audioVae, lora, loraStrength);
        ComfyWorkflowGraph g = rig.Graph;

        // Source = first frame. Scale to the config's megapixel budget (multiple of 32) and use those dims as the clip
        // size, so the clip keeps the source's aspect inside H3's canvas. An optional END frame pins the last frame.
        g[H3Nodes.Source] = new LoadImage { Image = inputs.SourceImageName ?? throw new RenderValidationException("MiniMax-H3 image→video needs a source image (the first frame), but none was provided.") };
        g[H3Nodes.ScaledSource] = new ImageScaleToTotalPixels { Image = LoadImage.ImageOut(H3Nodes.Source), UpscaleMethod = ComfyWidgets.Upscale.Lanczos, Megapixels = budgetMp, ResolutionSteps = BudgetSteps };
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
            g[H3Nodes.ScaledEndFrame] = new ImageScaleToTotalPixels { Image = LoadImage.ImageOut(H3Nodes.EndFrame), UpscaleMethod = ComfyWidgets.Upscale.Lanczos, Megapixels = budgetMp, ResolutionSteps = BudgetSteps };
            lastFrame = ImageScaleToTotalPixels.Out(H3Nodes.ScaledEndFrame);
        }
        // First/last-frame loop vs plain i2v is a choice of NODE, not a nullable input: the end frame either pins
        // the ending (its own record) or there is none.
        g[H3Nodes.Encode] = lastFrame is { } endFrame
            ? new MiniMaxH3FirstLastFrameToVideo
            {
                Clip = rig.Clip,
                Vae = rig.VideoVae,
                Prompt = inputs.Positive,
                Length = length,
                Width = GetImageSize.WidthOut(H3Nodes.SourceSize),
                Height = GetImageSize.HeightOut(H3Nodes.SourceSize),
                FirstFrame = ImageScaleToTotalPixels.Out(H3Nodes.ScaledSource),
                LastFrame = endFrame,
            }
            : new MiniMaxH3ImageToVideoI2V
            {
                Clip = rig.Clip,
                Vae = rig.VideoVae,
                Prompt = inputs.Positive,
                Length = length,
                Width = GetImageSize.WidthOut(H3Nodes.SourceSize),
                Height = GetImageSize.HeightOut(H3Nodes.SourceSize),
                FirstFrame = ImageScaleToTotalPixels.Out(H3Nodes.ScaledSource),
            };
        return Finish(rig, fps, seed, steps, sampler, scheduler, OutputPrefixes.Edit);
    }

    /// <summary>Reference→video: the open image is the PRIMARY subject reference (ref_image_0) and sets the output
    /// canvas (scaled to the per-config <paramref name="budgetMp"/>, exactly like i2v); any picker references follow as
    /// ref_image_1…N. They condition the subject/identity — NOT a first frame.</summary>
    public static ComfyWorkflowGraph BuildRef2V(ResolvedRequirements req, WorkflowInputs inputs,
        string audioVae, int length, double fps, long seed, int steps, string sampler, string scheduler,
        string? lora, double loraStrength, double budgetMp, int refMax)
    {
        Rig rig = Loaders(req, audioVae, lora, loraStrength);
        ComfyWorkflowGraph g = rig.Graph;

        // The primary reference sets the output canvas; picker references enter the ref node's autogrow ref_images
        // input, which resizes each internally (down only). The audio VAE is a direct input.
        g[H3Nodes.Source] = new LoadImage { Image = inputs.SourceImageName ?? throw new RenderValidationException("MiniMax-H3 reference→video needs a source image (the primary subject reference), but none was provided.") };
        g[H3Nodes.ScaledSource] = new ImageScaleToTotalPixels { Image = LoadImage.ImageOut(H3Nodes.Source), UpscaleMethod = ComfyWidgets.Upscale.Lanczos, Megapixels = budgetMp, ResolutionSteps = BudgetSteps };
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
            Clip = rig.Clip,
            Vae = rig.VideoVae,
            AudioVae = rig.AudioVae,
            Prompt = inputs.Positive,
            Length = length,
            Width = GetImageSize.WidthOut(H3Nodes.SourceSize),
            Height = GetImageSize.HeightOut(H3Nodes.SourceSize),
            RefImageSize = ComfyWidgets.RefImageSize.Match,
            RefInputs = MiniMaxH3ReferenceToVideo.Refs(imageEdges, videoFrameEdges, videoAudioEdges, audioEdges),
        };
        return Finish(rig, fps, seed, steps, sampler, scheduler, OutputPrefixes.Edit);
    }

    /// <summary>Loaders. Diffusion via DiffusionLoaderNode → plain UNETLoader (int8 ConvRot loads natively, weight_dtype
    /// default keeps its INT8). Qwen3-VL text encoder through CLIPLoader type "minimax". TWO VAEs: video (frames)
    /// and audio (the native stereo track); the audio VAE is the audio_vae model-ref slot. An optional model-only
    /// LoRA (the Turbo configs' distilled low-step LoRA) sits between the loader and everything downstream.</summary>
    private static Rig Loaders(ResolvedRequirements req, string audioVae, string? lora, double loraStrength)
    {
        ComfyWorkflowGraph g = new();
        g[H3Nodes.Model] = ComfyGraph.DiffusionLoaderNode(req.RequiredCheckpoint());   // H3 sets no weight_dtype → AutoWeightDtype (native INT8 ConvRot)
        Output<Slot.Model> model = ComfyGraph.ApplyLora(g, UNETLoader.ModelOut(H3Nodes.Model), lora, loraStrength, H3Nodes.Lora);
        g[H3Nodes.Clip] = new CLIPLoader { ClipName = req.TextEncoder(0), Type = ComfyWidgets.ClipType.Minimax, Device = ComfyWidgets.Device.Default };
        g[H3Nodes.VideoVae] = new VAELoader { VaeName = req.RequiredVae() };
        g[H3Nodes.AudioVae] = new VAELoader { VaeName = audioVae };
        return new Rig(g, model, CLIPLoader.ClipOut(H3Nodes.Clip), VAELoader.VaeOut(H3Nodes.VideoVae), VAELoader.VaeOut(H3Nodes.AudioVae));
    }

    /// <summary>Distilled sampling (BasicGuider — no CFG, no negative — + a res_multistep SamplerCustomAdvanced chain)
    /// off the <see cref="H3Nodes.Encode"/> node's (positive CONDITIONING, LATENT), then dual decode → one mp4 with
    /// audio. The SAME latent decodes to frames (video VAE) and to the native stereo track (audio VAE); CreateVideo
    /// muxes them; SaveVideo writes a real mp4 (format/codec auto = h264/aac).</summary>
    private static ComfyWorkflowGraph Finish(Rig rig, double fps, long seed, int steps, string sampler, string scheduler, string filenamePrefix)
    {
        ComfyWorkflowGraph g = rig.Graph;
        Output<Slot.Conditioning> positive = new(H3Nodes.Encode, 0);
        Output<Slot.Latent> latent = new(H3Nodes.Encode, 1);

        g[H3Nodes.Scheduler] = new BasicScheduler { Model = rig.Model, Scheduler = ComfyGraph.MapScheduler(scheduler), Steps = steps, Denoise = 1.0 };
        g[H3Nodes.SamplerSelect] = new KSamplerSelect { SamplerName = ComfyGraph.MapSampler(sampler) };
        g[H3Nodes.Noise] = new RandomNoise { NoiseSeed = seed };
        g[H3Nodes.Guider] = new BasicGuider { Model = rig.Model, Conditioning = positive };
        g[H3Nodes.Sampler] = new SamplerCustomAdvanced { Noise = RandomNoise.Out(H3Nodes.Noise), Guider = BasicGuider.Out(H3Nodes.Guider), Sampler = KSamplerSelect.Out(H3Nodes.SamplerSelect), Sigmas = BasicScheduler.Out(H3Nodes.Scheduler), LatentImage = latent };

        g[H3Nodes.VideoDecode] = new VAEDecode { Samples = SamplerCustomAdvanced.Out(H3Nodes.Sampler), Vae = rig.VideoVae };
        g[H3Nodes.AudioDecode] = new VAEDecodeAudio { Samples = SamplerCustomAdvanced.Out(H3Nodes.Sampler), Vae = rig.AudioVae };
        g[H3Nodes.CreateVideo] = new CreateVideo { Images = VAEDecode.Out(H3Nodes.VideoDecode), Fps = fps, Audio = VAEDecodeAudio.Out(H3Nodes.AudioDecode) };
        g[H3Nodes.Save] = new SaveVideo { Video = CreateVideo.Out(H3Nodes.CreateVideo), FilenamePrefix = filenamePrefix, Format = ComfyWidgets.SaveFormat.Auto, Codec = ComfyWidgets.VideoCodec.Auto };
        return g;
    }
}
