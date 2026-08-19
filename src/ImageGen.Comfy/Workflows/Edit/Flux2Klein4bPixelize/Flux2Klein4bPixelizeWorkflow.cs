namespace ImageGen.Comfy.Edit.Flux2Klein4bPixelize;

/// <summary>
/// Pixelizer on FLUX.2-Klein 4B. Mirrors the Klein custom-sampler edit graph (ReferenceLatent on the
/// source, BasicGuider + SamplerCustomAdvanced over a fresh Flux.2 latent), with the model patched by the
/// per-step <c>PixelManifoldProjection</c> before the guider and a final <c>PixelQuantize</c> render.
/// </summary>
public sealed class Flux2Klein4bPixelizeWorkflow : EditWorkflow<Flux2Klein4bPixelizeParams>
{
    public override bool NormalizesSourceResolution => true;
    public override string Name => "pixelize-klein4b";
    public override bool PreservesComposition => true;
    public override IReadOnlyList<ParamSpec> Schema => PixelizeSchema.KleinLike();

    /// <summary>The 64-px grid for the megapixels fallback — single source for the scale node and the ETA size.</summary>
    private const int BudgetSteps = 64;

    /// <summary>Renders at the working scale the graph picks — the clean k×VRES snap when snap-resolution is on, else
    /// the megapixels budget — NOT the raw upload dims. The ETA keys on that so pixel-art render time isn't credited by
    /// upload size.</summary>
    protected override (int Width, int Height) EtaRenderSize(Flux2Klein4bPixelizeParams p, ResolvedRequirements req, int sourceWidth, int sourceHeight)
        => PixelSnap.Target(req.Resolution, p.VirtualResolution, p.SnapResolution, p.Width, p.Height, sourceWidth, sourceHeight) is (int w, int h)
            ? (w, h)
            : BudgetScale.Snap(sourceWidth, sourceHeight, p.Megapixels, BudgetSteps);

    protected override ComfyWorkflowGraph Build(Flux2Klein4bPixelizeParams p, ResolvedRequirements req, WorkflowInputs inputs)
    {
        ComfyWorkflowGraph g = new();
        LoadModel(g, p.Loader, p.WeightDtype, p.ClipType, req, inputs, out Output<Slot.Model> model0, out Output<Slot.Clip> clip0, out Output<Slot.Vae> vae0);   // 4/5/6 + LoadImage 10
        Output<Slot.Image> src = PixelHarnessGraph.FlattenOnWhite(g);                     // flatten alpha onto white (11-14)

        string instruction = string.IsNullOrWhiteSpace(p.StylePrompt) ? inputs.Positive : p.StylePrompt;
        int gw = p.GridW;
        int gh = p.GridH;
        string palette = p.Palette;
        int vres = p.VirtualResolution;

        g[Nodes.Positive] = new CLIPTextEncode { Text = instruction, Clip = clip0 };
        (int w, int h)? snap = PixelSnap.Target(req.Resolution, vres, p.SnapResolution, p.Width, p.Height, inputs.SourceWidth, inputs.SourceHeight);   // override the megapixels bucket with the clean k×VRES size when on
        g[Nodes.ScaledImage] = snap is { } s
            ? PixelHarnessGraph.FixedScale(src, s.w, s.h)
            : new ImageScaleToTotalPixels { Image = src, UpscaleMethod = ComfyWidgets.Upscale.Lanczos, Megapixels = p.Megapixels, ResolutionSteps = BudgetSteps };
        g[Nodes.Encode] = new VAEEncode { Pixels = ImageScale.Out(Nodes.ScaledImage), Vae = vae0 };
        g[Nodes.ImageSize] = new GetImageSize { Image = ImageScale.Out(Nodes.ScaledImage) };
        g[Nodes.Guidance] = new FluxGuidance { Conditioning = CLIPTextEncode.Out(Nodes.Positive), Guidance = p.Guidance };
        g[Nodes.RefLatent] = new ReferenceLatent { Conditioning = FluxGuidance.Out(Nodes.Guidance), Latent = VAEEncode.Out(Nodes.Encode) };

        g[Nodes.Projection] = PixelizeSchema.Projection(model0, vae0, gw, gh, palette, vres, p.ProjMethod, p.WStart, p.WEnd, p.StartPercent, p.EndPercent, p.ProjectEvery);
        g[Nodes.Guider] = new BasicGuider { Model = PixelManifoldProjection.Out(Nodes.Projection), Conditioning = ReferenceLatent.Out(Nodes.RefLatent) };
        g[Nodes.EmptyLatentNode] = new EmptyFlux2LatentImage { Width = GetImageSize.WidthOut(Nodes.ImageSize), Height = GetImageSize.HeightOut(Nodes.ImageSize), BatchSize = 1 };
        g[Nodes.Scheduler] = new Flux2Scheduler { Steps = p.Steps, Width = GetImageSize.WidthOut(Nodes.ImageSize), Height = GetImageSize.HeightOut(Nodes.ImageSize) };
        g[Nodes.Noise] = new RandomNoise { NoiseSeed = ComfyGraph.Seed(p.Seed) };
        g[Nodes.SamplerSelect] = new KSamplerSelect { SamplerName = ComfyGraph.MapSampler(p.Sampler) };
        // reference% -> img2img: 0 generates from the empty latent over the full schedule; >0 inits from the source
        // latent and runs only the denoise tail (SplitSigmasDenoise low_sigmas = denoise fraction of the steps).
        Output<Slot.Sigmas> sigmas;
        Output<Slot.Latent> initLatent;
        if (p.Reference > 0)
        {
            g[Nodes.SplitSigmas] = new SplitSigmasDenoise { Sigmas = Flux2Scheduler.Out(Nodes.Scheduler), Denoise = PixelSnap.Denoise(p.Reference, 0) };
            sigmas = SplitSigmasDenoise.LowOut(Nodes.SplitSigmas);        // low_sigmas — the img2img tail
            initLatent = VAEEncode.Out(Nodes.Encode);    // source latent
        }
        else
        {
            sigmas = Flux2Scheduler.Out(Nodes.Scheduler);
            initLatent = EmptyFlux2LatentImage.Out(Nodes.EmptyLatentNode);
        }

        g[Nodes.Sampler] = new SamplerCustomAdvanced { Noise = RandomNoise.Out(Nodes.Noise), Guider = BasicGuider.Out(Nodes.Guider), Sampler = KSamplerSelect.Out(Nodes.SamplerSelect), Sigmas = sigmas, LatentImage = initLatent };
        g[Nodes.Decode] = new VAEDecode { Samples = SamplerCustomAdvanced.Out(Nodes.Sampler), Vae = vae0 };
        g[Nodes.FinalQuantize] = PixelizeSchema.FinalQuantize(VAEDecode.Out(Nodes.Decode), gw, gh, palette, vres, p.FinalMethod);
        g[Nodes.Save] = new SaveImage { Images = global::ImageGen.Comfy.PixelQuantize.Out(Nodes.FinalQuantize), FilenamePrefix = OutputPrefixes.Edit };
        return g;
    }
}
