namespace ImageGen.Comfy.Edit.FluxKontextPixelize;

/// <summary>
/// Pixelizer on FLUX.1-Kontext. Mirrors the Kontext edit graph (CLIP encode → ReferenceLatent on the
/// source's encoded latent → FluxGuidance), but patches the model with the per-step
/// <c>PixelManifoldProjection</c> so every denoise step clamps the x0 estimate onto a fixed grid+palette,
/// and renders the authoritative output with a final <c>PixelQuantize</c>. Virtual resolution sets the
/// sprite's pixel count independent of Kontext's render bucket.
/// </summary>
public sealed class FluxKontextPixelizeWorkflow : EditWorkflow<FluxKontextPixelizeParams>
{
    public override string Name => "pixelize-kontext";
    public override bool PreservesComposition => true;
    public override IReadOnlyList<ParamSpec> Schema => PixelizeSchema.KontextLike();

    protected override ComfyWorkflowGraph Build(FluxKontextPixelizeParams p, ResolvedRequirements req, WorkflowInputs inputs)
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
        (int w, int h)? snap = PixelSnap.Target(req.Resolution, vres, p.SnapResolution, p.Width, p.Height, inputs.SourceWidth, inputs.SourceHeight);   // override the Kontext bucket with the clean k×VRES size when on
        g[Nodes.Scale] = snap is { } s
            ? PixelHarnessGraph.FixedScale(src, s.w, s.h)
            : new FluxKontextImageScale { Image = src };
        g[Nodes.Encode] = new VAEEncode { Pixels = FluxKontextImageScale.Out(Nodes.Scale), Vae = vae0 };
        g[Nodes.RefLatent] = new ReferenceLatent { Conditioning = CLIPTextEncode.Out(Nodes.Positive), Latent = VAEEncode.Out(Nodes.Encode) };
        g[Nodes.Guidance] = new FluxGuidance { Conditioning = ReferenceLatent.Out(Nodes.RefLatent), Guidance = p.Guidance };
        g[Nodes.NegativeZero] = new ConditioningZeroOut { Conditioning = CLIPTextEncode.Out(Nodes.Positive) };

        g[Nodes.Projection] = PixelizeSchema.Projection(model0, vae0, gw, gh, palette, vres, p.ProjMethod, p.WStart, p.WEnd, p.StartPercent, p.EndPercent, p.ProjectEvery);
        g[Nodes.Sampler] = new KSampler
        {
            Seed = ComfyGraph.Seed(p.Seed),
            Steps = p.Steps,
            Cfg = p.Cfg,
            SamplerName = ComfyGraph.MapSampler(p.Sampler),
            Scheduler = ComfyGraph.MapScheduler(p.Scheduler),
            Denoise = PixelSnap.Denoise(p.Reference, 0),   // reference% -> denoise; 0 (default) == 1.0 == regenerate from the source ref
            Model = PixelManifoldProjection.Out(Nodes.Projection),
            Positive = FluxGuidance.Out(Nodes.Guidance),
            Negative = ConditioningZeroOut.Out(Nodes.NegativeZero),
            LatentImage = VAEEncode.Out(Nodes.Encode),
        };
        g[Nodes.Decode] = new VAEDecode { Samples = KSampler.Out(Nodes.Sampler), Vae = vae0 };
        g[Nodes.Quantize] = PixelizeSchema.FinalQuantize(VAEDecode.Out(Nodes.Decode), gw, gh, palette, vres, p.FinalMethod);
        g[Nodes.Save] = new SaveImage { Images = global::ImageGen.Comfy.PixelQuantize.Out(Nodes.Quantize), FilenamePrefix = OutputPrefixes.Edit };
        return g;
    }
}
