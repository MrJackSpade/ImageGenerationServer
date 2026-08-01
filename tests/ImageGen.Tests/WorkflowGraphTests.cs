using System.Text.Json;
using ImageGen.Comfy;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImageGen.Tests;

/// <summary>
/// Exercises the workflow-focused path end to end without a backend: load the real workflows.json +
/// requirements.json, resolve a configuration to its workflow, merge its parameter settings layer, and build the
/// ComfyUI graph. Asserts the structural fingerprints of each loader/latent/guidance/edit family so a parsing,
/// coercion, or merge regression is caught. (Graph topology itself is a verbatim lift of the old builders.)
/// </summary>
public sealed class WorkflowGraphTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "configurations", "models")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new DirectoryNotFoundException("configurations/ not found above the test bin dir.");
    }

    private static (WorkflowCatalog catalog, WorkflowRegistry registry) Build()
    {
        var root = RepoRoot();
        var cfg = new ComfyOptions
        {
            CatalogPath = Path.Combine(root, "configurations"),
        };
        var catalog = new WorkflowCatalog(cfg, NullLogger<WorkflowCatalog>.Instance);
        // Bind every slot to a synthetic filename derived from its id. These tests assert that the file bound to a
        // slot reaches the right loader node -- which is the actual invariant. They used to assert the AUTHOR's
        // filenames, which baked one machine's disk into the suite and is exactly the coupling this catalogue split
        // removed.
        // One rule, no special cases: a slot no longer declares a precision, so there is nothing here to key a
        // .gguf off. WHICH loader node a graph emits is a property of the bound file now, and is asserted by
        // The_diffusion_loader_is_chosen_by_the_bound_file rather than by every workflow test in passing.
        catalog.SetBindings(catalog.AllRequirements().ToDictionary(r => r.Id, r => r.Id + ".safetensors"));
        IWorkflow[] all =
        {
            new PonyV6Workflow(), new ZImageTurboWorkflow(), new Sd35MediumWorkflow(), new Flux1DevWorkflow(),
            new SdxlWorkflow(), new Sd15Workflow(),
            new FluxKontextEditWorkflow(), new WanI2VWorkflow(), new LtxvI2VWorkflow(),
            new QwenImageEditWorkflow(), new AnimateDiffSd15Workflow(), new Flux2Klein4bEditWorkflow(),
            new AnimaWorkflow(), new PixelAnimaWorkflow(),
            new AnimaInpaintWorkflow(), new Img2ImgRedrawWorkflow(), new AnimaOutpaintWorkflow(),
            new QwenImageInpaintWorkflow(), new QwenImageOutpaintWorkflow(),
            new FluxFillInpaintWorkflow(), new FluxFillOutpaintWorkflow(),
            new AnimateDiffLightningI2VWorkflow(), new AnimateLcmI2VWorkflow(),
            // 24GB-tier generation models
            new QwenImageWorkflow(), new Flux2DevWorkflow(), new HiDreamWorkflow(),
            new Sd35TripleClipWorkflow(), new ChromaWorkflow(), new Krea2Workflow(), new Krea2RefineWorkflow(),
            new Krea2RedrawWorkflow(),
            // 24GB-tier generation models (HunyuanImage 2.1)
            new HunyuanImage21Workflow(),
            // 24GB-tier video models
            new WanA14bI2VWorkflow(), new WanA14bT2VWorkflow(), new HunyuanVideo15T2VWorkflow(),
            new HunyuanVideoT2VWorkflow(), new LtxV2I2VWorkflow(), new HunyuanVideo15I2VWorkflow(),
            // Model-free pixel-quantize video-to-video.
            new PixelQuantizeVideoWorkflow(),
            // The generic diffusion pixelizer, which serves pixelize-sd35 and pixelize-hidream among others.
            new PixelizeWorkflow(),
            // Model-free feed-forward upscalers (anime PLKSR 2x / photo DAT2 4x) + the SeedVR2 diffusion restorer.
            new UpscaleWorkflow(), new SeedVr2UpscaleWorkflow(),
        };
        return (catalog, new WorkflowRegistry(all));
    }

    /// <summary>
    /// Replicates ComfyClient.MergeParams: schema defaults overlaid by the configuration's settings layer, then
    /// IsModelRef parameters resolved from slot id to bound filename.
    ///
    /// <para>The duplication is a known wart — this has to stay in step with <c>MergeParamsDict</c> by hand, and
    /// it did not, which is how the model-ref resolution was missing here after it was added there.</para>
    /// </summary>
    private static ParamValues Merge(WorkflowCatalog catalog, IWorkflow wf, WorkflowConfiguration cfg)
    {
        var v = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in wf.Schema) if (s.Default is not null) v[s.Key] = s.Default;
        foreach (var kv in cfg.Params) v[kv.Key] = kv.Value.Value;
        // Mirrors ComfyClient.MergeParamsDict: this machine's settings sit over the shipped configuration.
        foreach (var kv in catalog.ParamOverridesFor(cfg.Id)) v[kv.Key] = kv.Value;
        // The real thing, not a copy of it. This loop used to be duplicated here and fell out of step with the
        // renderer once already — which is precisely how a resolution rule can be right in the tests and wrong live.
        catalog.ResolveModelRefs(wf, cfg.Id, v);
        return new ParamValues(v);
    }

    private static string BuildJson(string configId, WorkflowInputs inputs)
    {
        var (catalog, registry) = Build();
        var cfg = catalog.FindConfig(configId);
        Assert.NotNull(cfg);
        var wf = registry.Find(cfg!.WorkflowName);
        Assert.NotNull(wf);
        var graph = wf!.Build(Merge(catalog, wf, cfg), catalog.Resolve(cfg), inputs);
        Assert.NotEmpty(graph);
        return JsonSerializer.Serialize(graph);
    }

    private static WorkflowInputs Gen => new() { Positive = "a cat", Negative = "blurry", Aspect = "square" };
    private static WorkflowInputs Edit => new() { Positive = "make it red", SourceImageName = "src.png", SourceWidth = 1216, SourceHeight = 832 };
    private static WorkflowInputs EditMasked => new() { Positive = "make it red", SourceImageName = "src.png", MaskImageName = "mask.png", SourceWidth = 1216, SourceHeight = 832 };

    [Fact]
    public void Catalog_loads_all_configurations()
    {
        var (catalog, _) = Build();

        // Every file in the tree loads, and none is silently dropped. Counted against the directory rather than a
        // literal: a hardcoded number turns "a workflow failed to load" and "somebody added one" into the same
        // red test, and the number is then updated by whoever is in a hurry.
        var onDisk = Directory.GetFiles(Path.Combine(RepoRoot(), "configurations", "workflows"), "*.json").Length;
        Assert.Equal(onDisk, catalog.AllConfigs().Count);
        Assert.NotNull(catalog.FindConfig("pony-v6"));
        Assert.NotNull(catalog.FindRequirement(catalog.FindConfig("pony-v6")!.Requirements.Checkpoint));
    }

    /// <summary>
    /// Every requirement link resolves to a model slot that exists. A dangling link is invisible until the
    /// workflow is picked, where it presents as "not installed on this machine" — a lie about the box.
    /// </summary>
    [Fact]
    public void Every_requirement_links_to_a_slot_that_exists()
    {
        var (catalog, _) = Build();

        var dangling = catalog.AllConfigs()
            .SelectMany(c => c.Requirements.All().Select(slot => (Config: c.Id, Slot: slot)))
            .Where(x => catalog.FindRequirement(x.Slot) is null)
            .Select(x => $"{x.Config} -> {x.Slot}")
            .ToList();

        Assert.Empty(dangling);
    }

    [Fact]
    public void PixelAnima_is_a_generate_workflow_txt2img_under_projection_plus_final_quantize()
    {
        var (catalog, registry) = Build();
        var cfg = catalog.FindConfig("pixelanima");
        Assert.NotNull(cfg);
        var wf = registry.Find(cfg!.WorkflowName);
        Assert.NotNull(wf);
        // It's a GENERATE workflow (text→image, no source), not an edit.
        Assert.Equal(WorkflowKind.Generate, wf!.Kind);
        Assert.Equal(WorkflowMedia.Image, wf.Media);

        var json = JsonSerializer.Serialize(wf.Build(Merge(catalog, wf, cfg), catalog.Resolve(cfg),
            new WorkflowInputs { Positive = "1girl, solo", Aspect = "square" }));

        // Plain txt2img topology from the base (Anima loads via UNETLoader; no source LoadImage).
        Assert.Contains("\"UNETLoader\"", json);
        Assert.Contains("\"CLIPTextEncode\"", json);
        Assert.Contains("\"KSampler\"", json);
        Assert.Contains("\"VAEDecode\"", json);
        Assert.DoesNotContain("\"LoadImage\"", json);

        // The denoise model is patched with the per-step pixel-manifold projection (node 35), and the sampler
        // reads the PATCHED model, not the raw loader output.
        Assert.Contains("\"PixelManifoldProjection\"", json);
        Assert.Contains("\"model\":[\"35\",0]", json);
        // The authoritative crisp render is a final PixelQuantize (node 36) that SaveImage consumes.
        Assert.Contains("\"PixelQuantize\"", json);
        Assert.Contains("\"images\":[\"36\",0]", json);
        // The exposed virtual-resolution knob (config 384) reaches BOTH the projection and the final quantize.
        Assert.Contains("\"virtual_resolution\":384", json);
    }

    [Fact]
    public void PixelQuantizeVideo_builds_v2v_load_quantize_save_chain()
    {
        var inputs = new WorkflowInputs { SourceVideoName = "forgemcp_edit_src.mp4" };
        var (catalog, registry) = Build();
        var cfg = catalog.FindConfig("pixel-quantize-video");
        Assert.NotNull(cfg);
        var wf = registry.Find(cfg!.WorkflowName);
        Assert.NotNull(wf);
        // It declares a VIDEO source (so the edit submit uploads a real clip + loads it) and a VIDEO output.
        Assert.Equal(WorkflowMedia.Video, wf!.SourceMedia);
        Assert.Equal(WorkflowMedia.Video, wf.Media);
        Assert.False(wf.RequiresModel);   // model-free — survives the catalog's no-checkpoint gate

        var json = JsonSerializer.Serialize(wf.Build(Merge(catalog, wf, cfg), catalog.Resolve(cfg), inputs));
        // Decode the clip → frames, pixel-quantize every frame, re-encode an animated WEBP. No model head.
        Assert.Contains("\"LoadVideo\"", json);
        Assert.Contains("forgemcp_edit_src.mp4", json);
        Assert.Contains("\"GetVideoComponents\"", json);
        Assert.Contains("\"PixelQuantize\"", json);
        Assert.Contains("\"SaveAnimatedWEBP\"", json);
        Assert.Contains("\"filename_prefix\":\"forgemcp_edit\"", json);   // required by SaveAnimatedWEBP
        Assert.DoesNotContain("\"LoadImage\"", json);
        Assert.DoesNotContain("CheckpointLoaderSimple", json);
        Assert.DoesNotContain("UNETLoader", json);
        // fps default 0 → the output keeps the source clip's frame rate, wired from GetVideoComponents (node 11, out 2).
        Assert.Contains("\"fps\":[\"11\",2]", json);
        // The configured quantizer knobs reach the node (locked palette = temporally consistent).
        Assert.Contains("\"palette\":\"chroma-256\"", json);
        Assert.Contains("\"virtual_resolution\":128", json);
    }

    [Fact]
    public void PixelQuantizeVideo_fp_engine_routes_to_feature_preserving_node()
    {
        var inputs = new WorkflowInputs { SourceVideoName = "forgemcp_edit_src.mp4" };
        var (catalog, registry) = Build();
        var cfg = catalog.FindConfig("pixel-quantize-video");
        var wf = registry.Find(cfg!.WorkflowName)!;

        // Schema defaults, then flip the engine to fp.
        var v = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in wf.Schema) if (s.Default is not null) v[s.Key] = s.Default;
        v["engine"] = "fp";
        var json = JsonSerializer.Serialize(wf.Build(new ParamValues(v), catalog.Resolve(cfg), inputs));

        // Same V2V scaffolding, but the quantize node is the feature-preserving one (not PixelQuantize).
        Assert.Contains("\"LoadVideo\"", json);
        Assert.Contains("\"GetVideoComponents\"", json);
        Assert.Contains("\"SaveAnimatedWEBP\"", json);
        Assert.Contains("\"PixelQuantizeFP\"", json);
        Assert.DoesNotContain("\"PixelQuantize\"", json);   // the median node must NOT be emitted for fp
        // fp knobs reach the node from schema defaults.
        Assert.Contains("\"thicken\":0.75", json);
        Assert.Contains("\"tau\":0.6", json);
        Assert.Contains("\"virtual_resolution\":128", json);
    }

    [Fact]
    public void AnimaInpaint_builds_masked_img2img_with_separate_mask()
    {
        var json = BuildJson("anima-inpaint", EditMasked);
        // The mask is a SEPARATE white-on-black image (LoadImageMask, red channel), grown, confining denoise. The
        // source stays pristine (VAEEncode of the source RGB) so the region outside the mask is preserved.
        Assert.Contains("\"LoadImageMask\"", json);
        Assert.Contains("mask.png", json);
        Assert.Contains("\"SetLatentNoiseMask\"", json);
        Assert.Contains("\"GrowMask\"", json);
        Assert.Contains("\"VAEEncode\"", json);                 // source RGB → latent (img2img, not an empty latent)
        Assert.DoesNotContain("EmptyLatentImage", json);
        Assert.Contains("anima", json);                          // same checkpoint as the Anima generator
        // The full prompt gets the quality prefix; the negative is the config default (no UI negative in EditMasked).
        Assert.Contains("masterpiece, best quality, score_7, make it red", json);
        Assert.Contains("worst quality", json);
    }

    [Theory]
    // default alone (blank/absent user negative → just the model default)
    [InlineData("worst quality, score_1", null, "worst quality, score_1")]
    [InlineData("worst quality, score_1", "   ", "worst quality, score_1")]
    // user text LEADS, the model default follows (trailing commas on either side normalized away)
    [InlineData("worst quality, score_1", "extra hands", "extra hands, worst quality, score_1")]
    [InlineData("worst quality, score_1,", "extra hands", "extra hands, worst quality, score_1")]
    public void ComposeNegative_leads_with_user_then_default(string modelDefault, string? user, string expected)
        => Assert.Equal(expected, ComfyGraph.ComposeNegative(modelDefault, user));

    [Fact]
    public void ComposeNegative_falls_back_to_the_shared_default_when_config_has_none()
    {
        // No config default → the shared DefaultNegative is the baseline; a user negative leads, the default follows.
        Assert.Equal(ComfyGraph.DefaultNegative, ComfyGraph.ComposeNegative(null, null));
        Assert.Equal(ComfyGraph.DefaultNegative, ComfyGraph.ComposeNegative("  ", ""));
        Assert.Equal("mutated, " + ComfyGraph.DefaultNegative, ComfyGraph.ComposeNegative(null, "mutated"));
    }

    [Fact]
    public void AnimaRedraw_builds_whole_image_img2img_with_no_mask()
    {
        var json = BuildJson("anima-redraw", Edit);
        // Whole-image img2img: the source RGB is VAE-encoded to the latent and sampled with NO mask.
        Assert.Contains("\"VAEEncode\"", json);
        Assert.DoesNotContain("EmptyLatentImage", json);
        Assert.DoesNotContain("SetLatentNoiseMask", json);
        Assert.DoesNotContain("LoadImageMask", json);
        Assert.Contains("anima", json);
        // Runs at Anima's native resolution: the >1 MP source (1216x832) is downscaled before encode (ImageScale).
        Assert.Contains("\"ImageScale\"", json);
        // Full prompt gets the quality prefix; the negative is Anima's CANONICAL default (the config `negative`), NOT
        // the generic shared DefaultNegative. No UI negative here (Edit inputs sets none) → the default stands alone.
        Assert.Contains("masterpiece, best quality, score_7, make it red", json);
        Assert.Contains("worst quality, low quality, score_1, score_2, score_3, artist name", json);
        Assert.DoesNotContain("bad anatomy", json);   // the generic DefaultNegative must NOT leak in for Anima
        // The optional nodes the FLUX/Chroma redraw configs turn on must stay OFF here — Anima's graph is unchanged.
        Assert.DoesNotContain("FluxGuidance", json);
        Assert.DoesNotContain("ModelSamplingAuraFlow", json);
        Assert.DoesNotContain("T5TokenizerOptions", json);
    }

    [Fact]
    public void Flux1DevRedraw_puts_the_distilled_guidance_in_the_conditioning()
    {
        var json = BuildJson("flux1-dev-redraw", Edit);
        // Same whole-image img2img topology as the Anima redraw — one graph class, a FLUX config.
        Assert.Contains("\"VAEEncode\"", json);
        Assert.DoesNotContain("SetLatentNoiseMask", json);
        Assert.DoesNotContain("EmptyLatentImage", json);
        // Guidance-distilled: the strength rides the conditioning (FluxGuidance) and real CFG stays 1.
        Assert.Contains("\"FluxGuidance\"", json);
        Assert.Contains("\"guidance\":3.5", json);
        Assert.Contains("\"cfg\":1", json);
        // FLUX's split loaders: gguf unet + the CLIP-L/T5 pair + the ae VAE.
        Assert.Contains("\"UNETLoader\"", json);
        Assert.Contains("\"DualCLIPLoader\"", json);
        // native_pixels 0 → the source is sampled at its OWN resolution; nothing is rescaled.
        Assert.DoesNotContain("\"ImageScale\"", json);
        // Not a Chroma config, so neither Chroma node appears.
        Assert.DoesNotContain("ModelSamplingAuraFlow", json);
        Assert.DoesNotContain("T5TokenizerOptions", json);
    }

    [Fact]
    public void Flux1SchnellRedraw_omits_the_guidance_node_the_distilled_weight_has_no_use_for()
    {
        var json = BuildJson("flux1-schnell-redraw", Edit);
        Assert.Contains("\"VAEEncode\"", json);
        Assert.Contains("\"steps\":4", json);
        // schnell is LADD-distilled with no guidance embedding: the config declares no `guidance`, so no node.
        Assert.DoesNotContain("FluxGuidance", json);
    }

    [Fact]
    public void Chroma1HdRedraw_adds_the_flow_shift_and_the_t5_padding_fix()
    {
        var json = BuildJson("chroma1-hd-redraw", Edit);
        Assert.Contains("\"VAEEncode\"", json);
        // Chroma's two required nodes, mirroring its generate graph.
        Assert.Contains("\"T5TokenizerOptions\"", json);
        Assert.Contains("\"min_padding\":0", json);
        Assert.Contains("\"ModelSamplingAuraFlow\"", json);
        Assert.Contains("\"shift\":1", json);
        // Real CFG with a working negative — NOT distilled guidance.
        Assert.Contains("\"cfg\":3.8", json);
        Assert.DoesNotContain("FluxGuidance", json);
        Assert.Contains("bad anatomy", json);   // no config `negative` → the shared baseline stands in
    }

    [Fact]
    public void Flux2Klein4bBaseRedraw_samples_the_non_distilled_base_at_real_cfg()
    {
        var json = BuildJson("flux2-klein-4b-base-redraw", Edit);
        // The base model, not a quality tier of the distilled one: real CFG 5 over 20 steps, so no guidance node.
        Assert.Contains("\"cfg\":5", json);
        Assert.Contains("\"steps\":20", json);
        Assert.DoesNotContain("FluxGuidance", json);
        // Flux.2 loads a single CLIP (Qwen3), not the FLUX.1 CLIP-L/T5 pair.
        Assert.Contains("\"CLIPLoader\"", json);
        Assert.DoesNotContain("DualCLIPLoader", json);
    }

    /// <summary>The text-encoder eviction was removed, so NO redraw config may emit the node — including the one it
    /// was built for. Kept as a Theory over the whole family so a reintroduction has to be deliberate rather than
    /// arriving with a config flag nobody notices.</summary>
    [Theory]
    [InlineData("anima-redraw")]
    [InlineData("photanima-redraw")]
    [InlineData("flux1-dev-redraw")]
    [InlineData("flux2-dev-redraw")]
    [InlineData("flux2-klein-4b-redraw-hq")]
    [InlineData("chroma1-hd-redraw")]
    public void No_redraw_config_evicts_the_text_encoder(string id)
    {
        Assert.DoesNotContain("EvictCLIPFromGPU", BuildJson(id, Edit));
    }

    [Fact]
    public void PhotAnimaRedraw_reuses_the_shared_redraw_graph_on_the_photanima_checkpoint()
    {
        var json = BuildJson("photanima-redraw", Edit);
        // Same whole-image img2img topology as anima-redraw — one graph class, two configs.
        Assert.Contains("\"VAEEncode\"", json);
        Assert.DoesNotContain("EmptyLatentImage", json);
        Assert.DoesNotContain("SetLatentNoiseMask", json);
        Assert.Contains("photanima-v21-noturbo.safetensors", json);
        Assert.DoesNotContain("anima-base", json);   // the Anima weight, not merely a substring of "photanima"
        // Photanima's OWN prefix leads the prompt (not Anima's), and its own de-turbo'd recipe reaches the sampler.
        Assert.Contains("masterpiece, score_9, absurdres, best quality, highres, photo ", json);
        Assert.Contains("real life, make it red", json);
        Assert.DoesNotContain("score_7", json);
        Assert.Contains("\"sampler_name\":\"er_sde\"", json);
        Assert.Contains("\"steps\":40", json);
        // No config `negative` → the shared baseline stands in, matching photanima's generate path.
        Assert.Contains("bad anatomy", json);
    }

    [Fact]
    public void Redraw_downscales_to_each_configs_own_native_pixel_budget()
    {
        // The budget is a config param, not a constant baked into the graph. Proof: the SAME 1216x832 source (1.01 MP)
        // is over Anima's 0.92 MP bucket (→ downscaled) but under Photanima's 1.04 MP bucket (→ left alone).
        Assert.Contains("\"ImageScale\"", BuildJson("anima-redraw", Edit));
        Assert.DoesNotContain("\"ImageScale\"", BuildJson("photanima-redraw", Edit));

        // Push well past Photanima's budget and it downscales too — to /16-snapped dims, aspect preserved.
        var big = new WorkflowInputs { Positive = "make it red", SourceImageName = "src.png", SourceWidth = 2048, SourceHeight = 2048 };
        var (catalog, registry) = Build();
        var cfg = catalog.FindConfig("photanima-redraw")!;
        var wf = registry.Find(cfg.WorkflowName)!;
        var bigJson = JsonSerializer.Serialize(wf.Build(Merge(catalog, wf, cfg), catalog.Resolve(cfg), big));
        Assert.Contains("\"ImageScale\"", bigJson);
        Assert.Contains("\"width\":1024", bigJson);    // sqrt(1044480/2048^2)*2048 = 1022 → snapped to 1024
        Assert.Contains("\"height\":1024", bigJson);
    }

    [Theory]
    [InlineData("anima-redraw")]
    [InlineData("photanima-redraw")]
    [InlineData("krea2-redraw")]
    public void Redraw_denoise_knob_steps_finely_enough_to_reach_its_default(string configId)
    {
        // A 0.1 step can't express 0.35 (and makes 0.6 a coarse slider). The configs omit `step`, so the value has to
        // come from the workflow's ParamSpec — assert it survives the config→spec fallback.
        var (catalog, registry) = Build();
        var cfg = catalog.FindConfig(configId)!;
        var wf = registry.Find(cfg.WorkflowName)!;
        var spec = wf.Schema.First(s => s.Key == "denoise");
        Assert.Equal(0.01, spec.Step);
        Assert.Null(cfg.Params["denoise"].Step);   // nothing overrides it at the config layer
        Assert.True(cfg.Params["denoise"].Exposed);
    }

    [Fact]
    public void Prompt_semantics_match_what_each_editor_actually_renders_from_the_prompt()
    {
        // Drives the editor's "Prompt" vs "Change" wording and the API prompting guides. A redraw re-renders the
        // whole frame from the prompt; an instruction editor is handed a change to make; FLUX Fill renders the
        // prompt INTO the masked region (its official examples prompt the patch — a whole-scene prompt at guidance
        // 30 crams a miniature of the scene into the hole; measured −60 luminance levels on a ground-truth sky
        // fill vs −6 with a region prompt). Getting any of these wrong is a lie to the user.
        Assert.Equal(PromptSemantics.WholeImage, new Img2ImgRedrawWorkflow().PromptSemantics);
        Assert.Equal(PromptSemantics.WholeImage, new Krea2RedrawWorkflow().PromptSemantics);
        Assert.Equal(PromptSemantics.WholeImage, new AnimaInpaintWorkflow().PromptSemantics);
        Assert.Equal(PromptSemantics.WholeImage, new AnimaOutpaintWorkflow().PromptSemantics);
        Assert.Equal(PromptSemantics.WholeImage, new FluxFillOutpaintWorkflow().PromptSemantics);

        Assert.Equal(PromptSemantics.MaskedRegion, new FluxFillInpaintWorkflow().PromptSemantics);

        Assert.Equal(PromptSemantics.Instruction, new QwenImageEditWorkflow().PromptSemantics);
        Assert.Equal(PromptSemantics.Instruction, new FluxKontextEditWorkflow().PromptSemantics);
    }

    [Fact]
    public void Redraw_configs_share_one_picker_section_and_drop_it_from_their_names()
    {
        // The "Redraw" header carries the category, so the names must not repeat it.
        var (catalog, _) = Build();
        foreach (var id in new[] { "anima-redraw", "photanima-redraw", "krea2-redraw" })
        {
            var cfg = catalog.FindConfig(id)!;
            Assert.Equal("Redraw", cfg.EditGroup);
            Assert.Null(cfg.EffectType);   // must NOT be an effect — that would move it to the Effects tab
            Assert.DoesNotContain("redraw", (cfg.FriendlyName ?? "").ToLowerInvariant());
        }
    }

    [Fact]
    public void Upscale_configs_share_one_picker_section_and_drop_it_from_their_names()
    {
        // The "Upscale" header carries the category, so the names must not repeat it.
        var (catalog, _) = Build();
        foreach (var id in new[] { "upscale-anime", "upscale-photo", "seedvr2-upscale" })
        {
            var cfg = catalog.FindConfig(id)!;
            Assert.Equal("Upscale", cfg.EditGroup);
            Assert.Null(cfg.EffectType);   // must NOT be an effect — that would move it to the Effects tab
            Assert.DoesNotContain("upscale", (cfg.FriendlyName ?? "").ToLowerInvariant());
        }
    }

    [Fact]
    public void SeedVr2_builds_a_one_frame_dit_vae_upscaler_chain_with_blockswap()
    {
        var json = BuildJson("seedvr2-upscale", Edit);
        // The pack's three nodes, wired DiT + VAE -> upscaler.
        Assert.Contains("\"SeedVR2LoadDiTModel\"", json);
        Assert.Contains("\"SeedVR2LoadVAEModel\"", json);
        Assert.Contains("\"SeedVR2VideoUpscaler\"", json);
        Assert.Contains("seedvr2-3b.safetensors", json);
        Assert.Contains("seedvr2-vae.safetensors", json);
        // A still is a ONE-frame clip: batch_size must follow 4n+1 => 1. Anything else is a video batch.
        Assert.Contains("\"batch_size\":1", json);
        Assert.Contains("\"uniform_batch_size\":false", json);
        // 8 GB fit: BlockSwap on, DiT offloaded to system RAM, VAE tiled on both ends.
        Assert.Contains("\"blocks_to_swap\":32", json);
        Assert.Contains("\"swap_io_components\":true", json);
        Assert.Contains("\"offload_device\":\"cpu\"", json);
        Assert.Contains("\"encode_tiled\":true", json);
        Assert.Contains("\"decode_tiled\":true", json);
        // The UI's integer scale is converted to the node's short-edge target: min(1216,832) * 2 = 1664.
        // max_resolution stays at the node's "no limit" so nothing is silently clamped below what was asked for.
        Assert.Contains("\"resolution\":1664", json);
        Assert.Contains("\"max_resolution\":0", json);
        // No text conditioning anywhere, and the instruction never reaches the graph.
        Assert.DoesNotContain("CLIPTextEncode", json);
        Assert.DoesNotContain("make it red", json);
        Assert.Contains("forgemcp_edit", json);
    }

    [Fact]
    public void SeedVr2_scale_converts_to_a_short_edge_target_and_falls_back_without_source_dims()
    {
        var wf = new SeedVr2UpscaleWorkflow();
        ParamValues P(int scale) => new(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["dit_model"] = "d.gguf", ["vae_model"] = "v.safetensors", ["scale"] = scale,
            ["max_resolution"] = 0, ["fallback_short_edge"] = 1080, ["batch_size"] = 1,
        });
        string Json(ParamValues p, WorkflowInputs i) => JsonSerializer.Serialize(wf.Build(p, new ResolvedRequirements(), i));

        // Portrait source: the SHORT edge drives it (832), not the long one.
        Assert.Contains("\"resolution\":832", Json(P(1), Edit));
        Assert.Contains("\"resolution\":2496", Json(P(3), Edit));

        // Odd short edge must snap up to even (the node's step): 833 * 1 -> 834.
        var odd = new WorkflowInputs { SourceImageName = "s.png", SourceWidth = 1000, SourceHeight = 833 };
        Assert.Contains("\"resolution\":834", Json(P(1), odd));

        // No source dims -> nothing to multiply; fall back to the node's own default rather than emit a bogus size.
        var noDims = new WorkflowInputs { SourceImageName = "s.png" };
        Assert.Contains("\"resolution\":1080", Json(P(4), noDims));

        // The node caps resolution at 16384 and rejects the WHOLE prompt above it — clamp instead of emitting a 400.
        var huge = new WorkflowInputs { SourceImageName = "s.png", SourceWidth = 9000, SourceHeight = 5000 };
        Assert.Contains("\"resolution\":16384", Json(P(4), huge));
    }

    [Fact]
    public void SeedVr2_folds_the_64bit_seed_into_the_nodes_uint32_range()
    {
        // The upstream node caps seed at 2^32-1, unlike ComfyUI's samplers. Passing the app's 64-bit seed straight
        // through made ComfyUI reject the whole prompt: "Value 2709052392662243722 bigger than max of 4294967295".
        var wf = new SeedVr2UpscaleWorkflow();
        ParamValues P(long seed) => new(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["dit_model"] = "d.gguf", ["vae_model"] = "v.safetensors", ["scale"] = 2,
            ["max_resolution"] = 0, ["fallback_short_edge"] = 1080, ["batch_size"] = 1, ["seed"] = seed,
        });
        long SeedOf(long s)
        {
            var graph = wf.Build(P(s), new ResolvedRequirements(), Edit);
            var inputs = JsonSerializer.SerializeToElement(((Dictionary<string, object>)graph["32"])["inputs"]);
            return inputs.GetProperty("seed").GetInt64();
        }

        // The exact seed from the live 400, and the extremes.
        foreach (var s in new[] { 2709052392662243722L, long.MaxValue, 4294967296L, 4294967295L, 1L })
        {
            var got = SeedOf(s);
            Assert.InRange(got, 0L, 4294967295L);
        }
        // In-range seeds pass through untouched, and the fold is deterministic (a re-run reproduces).
        Assert.Equal(1L, SeedOf(1L));
        Assert.Equal(4294967295L, SeedOf(4294967295L));
        Assert.Equal(0L, SeedOf(4294967296L));
        Assert.Equal(SeedOf(2709052392662243722L), SeedOf(2709052392662243722L));
    }

    [Fact]
    public void SeedVr2_needs_no_checkpoint_and_preserves_composition()
    {
        var wf = new SeedVr2UpscaleWorkflow();
        Assert.False(wf.RequiresModel);         // the pack's own loaders fetch the DiT + VAE
        Assert.True(wf.PreservesComposition);   // a restore must never trip the no-change gate
    }

    [Fact]
    public void SeedVr2_is_gated_on_loader_reported_weights_not_on_the_custom_node_directory()
    {
        // The node pack IS linked, and is satisfied by ComfyUI having the node registered rather than by a file.
        // It used to be deliberately unlinked, because eligibility demanded a file binding for every requirement
        // and a node slot can never have one — linking it gated the configuration off permanently. Eligibility is
        // node-aware now, so the link expresses the real dependency instead of being a trap.
        var (catalog, _) = Build();
        var cfg = catalog.FindConfig("seedvr2-upscale")!;
        Assert.Contains("comfyui-seedvr2-node", cfg.Requirements.All());

        var node = catalog.FindRequirement("comfyui-seedvr2-node");
        Assert.NotNull(node);
        Assert.False(string.IsNullOrWhiteSpace(node!.Node));   // met by node presence, not by a bound file

        foreach (var id in cfg.Requirements.All())
            Assert.NotNull(catalog.FindRequirement(id));
    }

    /// <summary>
    /// The shared edit head chose between one CLIP loader and two from a `dual` boolean, which cannot express
    /// three or four. Both failures were invisible until render: pixelize-sd35 declared three encoders alongside a
    /// checkpoint loader and got the checkpoint's CLIP output — null, for a checkpoint that ships without any —
    /// and pixelize-hidream declared four, got a single CLIPLoader, and fed it the generic workflow's "flux"
    /// default, a type that loader does not accept.
    /// </summary>
    [Theory]
    [InlineData("pixelize-sd35", "TripleCLIPLoader")]
    [InlineData("pixelize-hidream", "QuadrupleCLIPLoader")]
    public void A_configuration_gets_the_clip_loader_its_encoder_count_calls_for(string configId, string loader)
    {
        var json = BuildJson(configId, Edit);
        Assert.Contains($"\"{loader}\"", json);
        // And never the single-encoder loader it used to fall back to.
        Assert.DoesNotContain("\"CLIPLoaderGGUF\"", json);
    }

    /// <summary>
    /// A checkpoint that carries no encoders must not have its null CLIP output wired to the text encode. The
    /// declared encoders are the source when there are any.
    /// </summary>
    [Fact]
    public void A_checkpoint_without_encoders_uses_the_declared_ones_instead_of_its_null_clip()
    {
        var json = BuildJson("pixelize-sd35", Edit);
        // Node 4 is CheckpointLoaderSimple; its output 1 is the CLIP that does not exist here.
        Assert.DoesNotContain("[\"4\",1]", json);
        Assert.Contains("\"TripleCLIPLoader\"", json);
    }

    [Fact]
    public void UpscaleAnime_runs_the_2x_net_and_emits_no_resample_at_native_scale()
    {
        var json = BuildJson("upscale-anime", Edit);
        // Feed-forward SR only: no diffusion, no conditioning, no latent anywhere in the graph.
        Assert.Contains("\"UpscaleModelLoader\"", json);
        Assert.Contains("\"ImageUpscaleWithModel\"", json);
        Assert.Contains("anime-sharp-v2-rplksr-sharp-2x.safetensors", json);
        Assert.DoesNotContain("KSampler", json);
        Assert.DoesNotContain("CLIPTextEncode", json);
        Assert.DoesNotContain("VAEEncode", json);
        Assert.DoesNotContain("VAEDecode", json);
        Assert.DoesNotContain("EmptyLatentImage", json);
        // scale (2) == model_scale (2) → the net's output IS the answer; the fit node must not be emitted.
        Assert.DoesNotContain("\"ImageScaleBy\"", json);
        // Saved through the edit path's required prefix.
        Assert.Contains("forgemcp_edit", json);
    }

    [Fact]
    public void UpscalePhoto_fits_its_4x_net_down_to_the_requested_scale()
    {
        var json = BuildJson("upscale-photo", Edit);
        Assert.Contains("nomos2-hq-dat2-4x.safetensors", json);
        Assert.Contains("\"ImageUpscaleWithModel\"", json);
        Assert.DoesNotContain("KSampler", json);
        // config scale 2 over a native 4x net → downscale the super-resolved output by 0.5, never stretch the source.
        Assert.Contains("\"ImageScaleBy\"", json);
        Assert.Contains("\"scale_by\":0.5", json);
        Assert.Contains("\"upscale_method\":\"lanczos\"", json);
    }

    [Fact]
    public void Upscale_scale_above_the_native_factor_still_upscales_after_the_net()
    {
        // Guards the ratio arithmetic in both directions: a 2x net asked for 4x must resample UP by 2.0 after the
        // SR pass (not silently clamp, and not skip the node as if it were native).
        var wf = new UpscaleWorkflow();
        var p = new ParamValues(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["upscale_model"] = "anime-sharp-v2-rplksr-sharp-2x.safetensors",
            ["model_scale"] = 2.0,
            ["scale"] = 4,
            ["resample"] = "lanczos",
        });
        var json = JsonSerializer.Serialize(wf.Build(p, new ResolvedRequirements(), Edit));
        Assert.Contains("\"ImageScaleBy\"", json);
        Assert.Contains("\"scale_by\":2", json);
    }

    [Fact]
    public void Upscale_editors_take_no_prompt_so_the_editor_hides_its_instruction_box()
    {
        // TakesPrompt=false is what makes edit.js hide the instruction field. Both upscalers are prompt-less; the
        // instruction editors and redraws must stay true or their prompt box would vanish.
        Assert.False(new UpscaleWorkflow().TakesPrompt);
        Assert.False(new SeedVr2UpscaleWorkflow().TakesPrompt);
        Assert.True(new Img2ImgRedrawWorkflow().TakesPrompt);
        Assert.True(new QwenImageEditWorkflow().TakesPrompt);
        Assert.True(new AnimaInpaintWorkflow().TakesPrompt);
    }

    [Fact]
    public void Upscale_takes_no_prompt_and_preserves_composition()
    {
        var wf = new UpscaleWorkflow();
        Assert.False(wf.RequiresModel);          // no checkpoint — the SR net loads itself
        Assert.True(wf.PreservesComposition);    // an upscale must never trip the no-change gate
        // The instruction is carried by the edit path but has nowhere to go — assert it never reaches the graph.
        var json = BuildJson("upscale-photo", Edit);
        Assert.DoesNotContain("make it red", json);
    }

    [Fact]
    public void Krea2Redraw_builds_single_turbo_partial_denoise_over_the_source()
    {
        var json = BuildJson("krea2-redraw", Edit);
        // Whole-image img2img: the source RGB is VAE-encoded to the init latent and sampled with NO mask.
        Assert.Contains("\"VAEEncode\"", json);
        Assert.DoesNotContain("EmptyLatentImage", json);
        Assert.DoesNotContain("SetLatentNoiseMask", json);
        Assert.DoesNotContain("LoadImageMask", json);
        // ONE weight — the Turbo distill. The RAW base of krea2-refine must not be loaded (this is the cheap pass).
        Assert.Contains("krea2-turbo.safetensors", json);
        Assert.DoesNotContain("krea2_raw", json);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(json, "\"UNETLoader\""));
        // Unlike anima-redraw, the source is NOT rescaled — Krea 2 is native at ~1K and this is a polish pass.
        Assert.DoesNotContain("\"ImageScale\"", json);
        // Distilled: partial denoise at the config's 8 steps / cfg 1.
        Assert.Contains("\"denoise\":0.35", json);
        Assert.Contains("\"steps\":8", json);
        Assert.Contains("\"cfg\":1", json);
        // The config bakes the uncensor preset on, so the rebalance node splices in (at "15", clear of the encodes).
        Assert.Contains("\"ConditioningKrea2Rebalance\"", json);
        Assert.Contains("\"multiplier\":4", json);
    }

    [Fact]
    public void Krea2Rebalance_is_skipped_when_both_knobs_are_neutral()
    {
        // Neutral knobs emit no node at all — the graph stays byte-identical to plain Krea 2. Guards the shared helper
        // now that the txt2img base, the refiner, and the Turbo redraw all route through it.
        var wf = new Krea2RedrawWorkflow();
        var neutral = new ParamValues(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["rebalance_multiplier"] = 1.0,
            ["per_layer_weights"] = Krea2Rebalance.NeutralWeights,
        });
        Assert.False(Krea2Rebalance.IsActive(neutral));

        var graph = new Dictionary<string, object>();
        var positive = ComfyGraph.Ref("13", 0);
        Assert.Same(positive, Krea2Rebalance.Apply(graph, positive, neutral, "15"));
        Assert.Empty(graph);

        // ...and a single non-neutral layer weight is enough to switch it on.
        var oneLayerHot = new ParamValues(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["rebalance_multiplier"] = 1.0,
            ["per_layer_weights"] = "1.0,1.0,1.0,1.0,1.0,1.0,1.0,1.0,2.0,1.0,1.0,1.0",
        });
        Assert.True(Krea2Rebalance.IsActive(oneLayerHot));
        Assert.NotSame(positive, Krea2Rebalance.Apply(graph, positive, oneLayerHot, "15"));
        Assert.True(graph.ContainsKey("15"));
        Assert.Contains("denoise", wf.Schema.Select(s => s.Key));
    }

    [Fact]
    public void AnimaEdit_appends_the_ui_negative_to_the_config_default()
    {
        // The core of the feature: a UI negative (WorkflowInputs.Negative) is merged with the model's config default
        // negative, never replacing it. The user's tags LEAD, the Anima default follows, comma-joined.
        var withNeg = new WorkflowInputs
        {
            Positive = "make it red", SourceImageName = "src.png", MaskImageName = "mask.png",
            SourceWidth = 1216, SourceHeight = 832, Negative = "extra hands, jpeg artifacts",
        };
        var json = BuildJson("anima-inpaint", withNeg);
        Assert.Contains("extra hands, jpeg artifacts, worst quality, low quality, score_1, score_2, score_3, artist name", json);
    }

    [Fact]
    public void AnimaOutpaint_builds_pad_for_outpaint_masked_img2img()
    {
        var json = BuildJson("anima-outpaint", Edit);
        // Masked-denoise op (grow the border mask, confine denoise to it) PLUS the inpainting LLLite that conditions
        // the border on the known pixels + hole so it continues structure. No composite hack.
        Assert.Contains("\"ImagePadForOutpaint\"", json);
        Assert.Contains("\"AnimaLLLiteApply\"", json);              // the inpaint fill-conditioning
        Assert.Contains("anima-lllite-inpainting-v2.safetensors", json);  // resolved controlnet weight
        Assert.Contains("\"VAEEncode\"", json);
        Assert.Contains("\"GrowMask\"", json);                      // mirrors the inpaint seam blend
        Assert.Contains("\"SetLatentNoiseMask\"", json);            // border-only denoise natively preserves the original
        Assert.DoesNotContain("ImageCompositeMasked", json);        // no regenerate-whole-and-paste-back
        Assert.DoesNotContain("EmptyLatentImage", json);
        Assert.Contains("anima", json);
        Assert.Contains("masterpiece, best quality, score_7, make it red", json);
    }

    [Fact]
    public void FluxFill_takes_the_mask_as_native_model_conditioning_not_a_controlnet()
    {
        var json = BuildJson("flux1-fill-inpaint", EditMasked);
        // The whole point of this workflow: the mask is the MODEL's input. InpaintModelConditioning feeds the model
        // the blanked region as concat_latent_image + the mask as concat_mask. No ControlNet is involved anywhere.
        Assert.Contains("\"InpaintModelConditioning\"", json);
        // noise_mask must be TRUE: the per-step latent pinning anchors the fill's CONTENT to the surroundings.
        // Full-frame sampling (noise_mask=false, the diffusers-reference shape) was tried 2026-07-20 so the
        // outside pixels would witness the fill's exposure drift for the color fit — and measured strictly worse:
        // Fill freewheels without the anchor (a moon hallucinated into an empty-prompt sky fill; −27/−89 luminance
        // vs −6 pinned). The drift is attacked at its source instead, not by unpinning.
        Assert.Contains("\"noise_mask\":true", json);
        Assert.DoesNotContain("ControlNet", json);
        // ...and none of the scaffolding the ControlNet path needed to survive its own seams: no SetLatentNoiseMask
        // (InpaintModelConditioning's noise_mask IS that), and no pre-fill of the region (the 0.5-grey blanking is
        // the model's TRAINED fill signal — painting scene content in would hide it).
        Assert.DoesNotContain("SetLatentNoiseMask", json);
        // Seam blending is done by the denoiser: DifferentialDiffusion turns the soft mask edge into a per-pixel
        // denoise schedule, so the band is harmonized across steps rather than cross-faded after the fact.
        Assert.Contains("\"DifferentialDiffusion\"", json);
        Assert.Contains("\"GrowMask\"", json);
        Assert.Contains("\"ImageBlur\"", json);
        Assert.Contains("\"MaskComposite\"", json);      // one-sided ramp: hard 1 over the region being filled

        // Flux conditioning shape: guidance-distilled, so real CFG is 1 and the negative is the positive zeroed.
        Assert.Contains("\"FluxGuidance\"", json);
        Assert.Contains("\"ConditioningZeroOut\"", json);
        Assert.Contains("\"cfg\":1", json);
        Assert.Contains("\"guidance\":30", json);
        // 12B bf16 must be cast at load or it swaps against T5 on a 24GB card.
        Assert.Contains("\"weight_dtype\":\"fp8_e4m3fn\"", json);
        Assert.Contains("flux1-fill-dev.safetensors", json);
        Assert.Contains("\"DualCLIPLoader\"", json);
        // Paste-back keeps everything outside the region bit-identical to the source — via the fork's color-
        // correcting composite, which first fits the fill's drift on the outside pixels and inverts it (Linear2).
        Assert.Contains("\"ImageCompositeMaskedColorCorrected\"", json);
        Assert.Contains("\"correction_method\":\"Linear2\"", json);
    }

    [Fact]
    public void FluxFillOutpaint_pads_the_canvas_and_leaves_the_grey_pad_alone()
    {
        var (catalog, registry) = Build();
        var cfg = catalog.FindConfig("flux1-fill-outpaint");
        var wf = registry.Find(cfg!.WorkflowName)!;
        var v = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in wf.Schema) if (s.Default is not null) v[s.Key] = s.Default;
        foreach (var kv in cfg.Params) v[kv.Key] = kv.Value.Value;
        // Mirrors ComfyClient.MergeParamsDict: this machine's settings sit over the shipped configuration.
        foreach (var kv in catalog.ParamOverridesFor(cfg.Id)) v[kv.Key] = kv.Value;
        v["pad_left"] = 256; v["pad_right"] = 256;
        var inputs = new WorkflowInputs { Positive = "wider", SourceImageName = "src.png", SourceWidth = 1024, SourceHeight = 1024 };
        var json = JsonSerializer.Serialize(wf.Build(new ParamValues(v), catalog.Resolve(cfg), inputs));

        Assert.Contains("\"ImagePadForOutpaint\"", json);
        Assert.Contains("\"feathering\":0", json);       // softening happens once, in the mask chain
        Assert.Contains("\"InpaintModelConditioning\"", json);
        Assert.Contains("\"noise_mask\":true", json);    // latent pinning anchors content; see the inpaint test
        Assert.Contains("\"DifferentialDiffusion\"", json);
        Assert.Contains("\"ImageCompositeMaskedColorCorrected\"", json);
        // Unlike the ControlNet path, the pad's 0.5-grey needs NO engineering around it: InpaintModelConditioning
        // re-blanks the masked region to that same grey as the model's trained fill signal, and nothing ever
        // alpha-blends the pad into the output. So there is no pre-fill scaffold here on purpose.
        Assert.DoesNotContain("\"ImageScale\"", json);   // no stretch-and-blur prefill (that's the Qwen path's fix)
    }

    [Fact]
    public void QwenImageInpaint_conditions_the_fill_on_the_instantx_controlnet_and_pastes_back()
    {
        var json = BuildJson("qwen-image-inpaint", EditMasked);
        // The fill conditioning: the InstantX inpainting ControlNet, applied through AliMama's node (Comfy reuses it
        // — there is no Qwen-specific apply node). Without this the masked region is invented, not continued.
        Assert.Contains("\"ControlNetLoader\"", json);
        Assert.Contains("qwen-image-instantx-controlnet-inpainting.safetensors", json);
        Assert.Contains("\"ControlNetInpaintingAliMamaApply\"", json);
        // Mask arrives as a separate white-on-black upload, and drives BOTH the latent denoise and the paste-back.
        Assert.Contains("\"LoadImageMask\"", json);
        Assert.Contains("mask.png", json);
        Assert.Contains("\"SetLatentNoiseMask\"", json);
        Assert.Contains("\"ImageBlur\"", json);
        // Same one-sided ramp as outpaint: grow 16 places the crossfade band outside the painted region, and the
        // MaskComposite clamp holds the painted region itself at a hard 1 — this app's inpaint masks cover flat
        // WHITE space to fill, so any mask deficit inside them leaks white exactly like the outpaint pad leaked grey.
        Assert.Contains("\"GrowMask\"", json);
        Assert.Contains("\"MaskComposite\"", json);
        // ...and the white hole is pre-filled with the blurred surround before encoding, grey's-twin treatment.
        Assert.Contains("\"VAEEncode\"", json);
        Assert.DoesNotContain("EmptyLatentImage", json);
        // Paste-back keeps everything outside the mask bit-identical — the whole point, and the one place this
        // deliberately differs from the Anima flows (which save the decode directly).
        Assert.Contains("\"ImageCompositeMasked\"", json);
        Assert.Contains("\"ModelSamplingAuraFlow\"", json);
        // BASE Qwen-Image, not the Edit fine-tune: a plain CLIPTextEncode, and the base GGUF weight.
        Assert.Contains("\"CLIPTextEncode\"", json);
        Assert.DoesNotContain("TextEncodeQwenImageEdit", json);
        Assert.Contains("qwen-image-2512.safetensors", json);
        Assert.Contains("\"UNETLoader\"", json);      // native int8 ConvRot, not the GGUF loader
        Assert.DoesNotContain("UnetLoaderGGUF", json);
    }

    [Fact]
    public void QwenImageOutpaint_pads_the_canvas_and_conditions_the_border_on_the_controlnet()
    {
        var json = BuildJson("qwen-image-outpaint", Edit);
        // ImagePadForOutpaint supplies both the enlarged canvas and the border mask — no painted mask upload here.
        Assert.Contains("\"ImagePadForOutpaint\"", json);
        Assert.DoesNotContain("\"LoadImageMask\"", json);
        // Same ControlNet fill conditioning as the inpaint sibling, so the border continues the scene.
        Assert.Contains("\"ControlNetInpaintingAliMamaApply\"", json);
        Assert.Contains("qwen-image-instantx-controlnet-inpainting.safetensors", json);
        // Latent noise mask ON — deliberate deviation from the template's outpaint branch (VAEEncode straight into
        // KSampler): without it the fill's tone is unanchored and the panels drift ~15 RGB bright. Its hard-1px-seam
        // failure mode is handled by the wide mask ramp instead (see the grey-pad-fill test below).
        Assert.Contains("\"SetLatentNoiseMask\"", json);
        // The grey pad NEVER reaches the sampler: ImagePadForOutpaint exists only for its mask, and the sampled
        // canvas is the pre-filled one — source stretched to the padded size, blurred into a scene-tone scaffold,
        // original pasted back on top. Grey is what every measured halo was made of; blending anything soft over it
        // (mask ramp, latent blend, composite) mixes grey into the seam, so the fix is that grey does not exist.
        Assert.Contains("\"ImageScale\"", json);
        Assert.Contains("\"ImageCompositeMasked\"", json);
        Assert.Contains("\"VAEEncode\"", json);
        Assert.DoesNotContain("EmptyLatentImage", json);
        Assert.DoesNotContain("TextEncodeQwenImageEdit", json);
        Assert.Contains("qwen-image-2512.safetensors", json);
        Assert.Contains("\"UNETLoader\"", json);      // native int8 ConvRot, not the GGUF loader
        Assert.DoesNotContain("UnetLoaderGGUF", json);
    }

    [Fact]
    public void QwenOutpaint_never_softens_the_mask_over_the_grey_pad_fill()
    {
        // REGRESSION: the first cut used FeatherMask, which ramps the mask in from the CANVAS EDGES rather than from
        // the mask's own boundary. On an outpaint the fill region touches those edges, so the mask fell toward 0
        // exactly over ImagePadForOutpaint's 0.5-GREY fill — the sampler left the grey half-denoised AND
        // ImageCompositeMasked blended it in, producing a grey frame (measured: RGB 127 at x=0).
        var (catalog, registry) = Build();
        var cfg = catalog.FindConfig("qwen-image-outpaint");
        var wf = registry.Find(cfg!.WorkflowName)!;
        var v = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in wf.Schema) if (s.Default is not null) v[s.Key] = s.Default;
        foreach (var kv in cfg.Params) v[kv.Key] = kv.Value.Value;
        // Mirrors ComfyClient.MergeParamsDict: this machine's settings sit over the shipped configuration.
        foreach (var kv in catalog.ParamOverridesFor(cfg.Id)) v[kv.Key] = kv.Value;
        v["pad_left"] = 256; v["pad_right"] = 256;
        var inputs = new WorkflowInputs { Positive = "wider", SourceImageName = "src.png", SourceWidth = 1024, SourceHeight = 1024 };
        var graph = wf.Build(new ParamValues(v), catalog.Resolve(cfg), inputs);
        var json = JsonSerializer.Serialize(graph);

        Assert.DoesNotContain("FeatherMask", json);              // the node that caused it
        Assert.Contains("\"ImageBlur\"", json);                  // blur the mask's own boundary instead

        // The pad node must not ALSO feather, or the softening stacks into a wide partial-denoise band (mushy seam).
        Assert.Contains("\"feathering\":0", JsonSerializer.Serialize(graph["20"]));

        // Every consumer of the CANVAS takes the pre-filled scene-tone one (node 23), never ImagePadForOutpaint's
        // grey canvas (node 20 output 0, kept only for its mask): the VAE encode, the ControlNet apply's control
        // image, and the composite's destination. Grey under any soft mask edge = the halo.
        Assert.Contains("\"23\"", JsonSerializer.Serialize(graph["12"]));   // VAEEncode
        Assert.Contains("\"23\"", JsonSerializer.Serialize(graph["108"]));  // ControlNet control image
        Assert.Contains("\"23\"", JsonSerializer.Serialize(graph["126"])); // composite destination
        // The scaffold: stretch to the padded size, blur, paste the original back at its pad offset.
        Assert.Contains("\"ImageScale\"", JsonSerializer.Serialize(graph["21"]));
        Assert.Contains("\"ImageBlur\"", JsonSerializer.Serialize(graph["22"]));
        Assert.Contains("\"x\":256", JsonSerializer.Serialize(graph["23"])); // original pasted back at its pad offset

        // Outpaint's ramp: sigma 8 (not the template's 1) keeps SetLatentNoiseMask's latent-space blend from landing
        // inside a single 8px cell and decoding as a hard 1px line along the frame-spanning join (measured: a lone
        // ~63 gradient column with a near-binary mask). Grow 16 = 2σ places the 50% blend point 16px inside the
        // original with the descent starting right at the pad boundary.
        Assert.Contains("\"expand\":16", JsonSerializer.Serialize(graph["30"]));
        var blurNode = JsonSerializer.Serialize(graph["33"]);
        Assert.Contains("\"blur_radius\":31", blurNode);
        Assert.Contains("\"sigma\":8", blurNode);

        // The blurred mask is clamped back to a hard 1 over the raw pad region (MaskComposite "add" against the raw
        // ImagePadForOutpaint mask). ANY deficit below 1 over the grey pad leaks 0.5-grey into that column through
        // the latent re-injection and the composite: measured seam columns of 51/34/10 as the unclamped gaussian's
        // boundary value went 0.933/0.977/0.9987, seam-free only with a hard 1 there.
        var clamp = JsonSerializer.Serialize(graph["35"]);
        Assert.Contains("\"MaskComposite\"", clamp);
        Assert.Contains("\"add\"", clamp);
        Assert.Contains("\"20\"", clamp);                                   // clamped against the RAW pad mask

        // Every mask consumer takes the SAME softened+clamped mask: the ControlNet apply, SetLatentNoiseMask and the
        // composite. Splitting any of them off has failed twice (raw-to-ControlNet dirtied the seam; raw-to-composite
        // hard-switched on pixels the ControlNet was blind to and the extension didn't line up).
        Assert.Contains("\"35\"", JsonSerializer.Serialize(graph["108"]));  // ControlNet gets the SOFTENED mask
        Assert.Contains("\"35\"", JsonSerializer.Serialize(graph["31"]));   // latent noise mask: same softened mask
        Assert.Contains("\"35\"", JsonSerializer.Serialize(graph["126"])); // composite: same softened mask

        // The sampler goes through SetLatentNoiseMask — the exposure anchor. Without it (template outpaint branch,
        // VAEEncode straight in) the ControlNet anchors structure but not tone, and the measured side panels came
        // out ~15 RGB brighter than the frame they extend (the "color balance" halo).
        Assert.Contains("\"31\"", JsonSerializer.Serialize(graph["3"]));
    }

    [Fact]
    public void QwenImageInpaint_leaves_a_source_under_the_ceiling_at_native_resolution()
    {
        // 1216x832 is under the 1536 ceiling — nothing may be resized. Guards the standing rule that we never
        // silently change a user's resolution, and the fact that Comfy's own node would have UPSCALED this to 1536.
        var json = BuildJson("qwen-image-inpaint", EditMasked);
        // ImageScale is the only node that resizes; MaskToImage is NOT a proxy for scaling — the mask blur
        // round-trips through IMAGE too, so it is present either way.
        Assert.DoesNotContain("\"ImageScale\"", json);
        Assert.DoesNotContain("ImageScaleToMaxDimension", json);
    }

    [Fact]
    public void QwenImageInpaint_scales_canvas_and_mask_together_when_over_the_ceiling()
    {
        var big = new WorkflowInputs
        {
            Positive = "make it red", SourceImageName = "src.png", MaskImageName = "mask.png",
            SourceWidth = 4000, SourceHeight = 3000,
        };
        var json = BuildJson("qwen-image-inpaint", big);
        // 4000x3000 -> long edge capped at 1536, aspect kept, both snapped down to a multiple of 16: 1536x1152.
        Assert.Contains("\"width\":1536", json);
        Assert.Contains("\"height\":1152", json);
        // The mask must make the SAME trip, or ImageCompositeMasked gets a mismatched mask and the paste-back breaks.
        Assert.Contains("\"MaskToImage\"", json);
        Assert.Contains("\"ImageToMask\"", json);
    }

    [Fact]
    public void QwenImageOutpaint_measures_the_ceiling_against_the_PADDED_canvas()
    {
        // Source is under the ceiling, but the pads push the real canvas over it — the ceiling must see the padded size.
        var (catalog, registry) = Build();
        var cfg = catalog.FindConfig("qwen-image-outpaint");
        Assert.NotNull(cfg);
        var wf = registry.Find(cfg!.WorkflowName);
        Assert.NotNull(wf);
        var v = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in wf!.Schema) if (s.Default is not null) v[s.Key] = s.Default;
        foreach (var kv in cfg.Params) v[kv.Key] = kv.Value.Value;
        v["pad_left"] = 600; v["pad_right"] = 600;
        var inputs = new WorkflowInputs { Positive = "wider", SourceImageName = "src.png", SourceWidth = 1216, SourceHeight = 832 };
        var json = JsonSerializer.Serialize(wf.Build(new ParamValues(v), catalog.Resolve(cfg), inputs));
        // 1216 + 600 + 600 = 2416 wide > 1536, so it scales even though the SOURCE alone was under the ceiling.
        Assert.Contains("\"ImageScale\"", json);
        Assert.Contains("\"width\":1536", json);
    }

    [Fact]
    public void HunyuanImage21_builds_with_sd3_sampling_and_image_latent()
    {
        var json = BuildJson("hunyuanimage21", Gen);
        Assert.Contains("\"EmptyHunyuanImageLatent\"", json);
        Assert.Contains("\"ModelSamplingSD3\"", json);
        Assert.Contains("hunyuanimage21-distilled.safetensors", json);
        Assert.Contains("hunyuan-image-2-1-vae.safetensors", json);
        Assert.Contains("\"type\":\"hunyuan_image\"", json);
    }

    [Fact]
    public void HunyuanVideo15_i2v_sr_appends_super_resolution_pass()
    {
        var json = BuildJson("hunyuanvideo15-i2v-sr", Edit);
        Assert.Contains("\"LatentUpscaleModelLoader\"", json);
        Assert.Contains("\"HunyuanVideo15LatentUpscaleWithModel\"", json);
        Assert.Contains("\"HunyuanVideo15SuperResolution\"", json);
        Assert.Contains("hunyuanvideo15-1080p-sr-distilled.safetensors", json);
        Assert.Contains("hunyuanvideo15-latent-upsampler-1080p.safetensors", json);
        Assert.Contains("\"VAEDecodeTiled\"", json);   // SR active → tiled decode
        // Exact node field names validated live against ComfyUI (object_info): the upscale node takes "model"
        // (not "upscale_model"); the SR node requires "noise_augmentation" (not "denoise").
        Assert.Contains("\"noise_augmentation\"", json);
        Assert.DoesNotContain("\"upscale_model\"", json);
    }

    [Fact]
    public void HunyuanVideo15_i2v_without_sr_has_no_super_resolution_nodes()
    {
        var json = BuildJson("hunyuanvideo15-i2v", Edit);
        Assert.DoesNotContain("HunyuanVideo15SuperResolution", json);
        Assert.DoesNotContain("LatentUpscaleModelLoader", json);
    }

    [Fact]
    public void PonyV6_builds_a_checkpoint_clipskip_graph()
    {
        var json = BuildJson("pony-v6", Gen);
        Assert.Contains("\"CheckpointLoaderSimple\"", json);
        Assert.Contains("\"CLIPSetLastLayer\"", json);     // clip_skip = 2
        Assert.Contains("\"EmptyLatentImage\"", json);     // std latent
        Assert.Contains("\"SaveImage\"", json);
        Assert.Contains("\"steps\":25", json);
        Assert.Contains("\"cfg\":7", json);
    }

    [Fact]
    public void ZImageTurbo_builds_a_gguf_sd3_graph()
    {
        var json = BuildJson("z-image-turbo", Gen);
        Assert.Contains("\"UNETLoader\"", json);
        Assert.Contains("\"EmptySD3LatentImage\"", json);
        Assert.Contains("\"steps\":8", json);
    }

    [Fact]
    public void Sd35Medium_builds_an_sd3_latent_graph()
    {
        var json = BuildJson("sd35-medium", Gen);
        Assert.Contains("\"CheckpointLoaderSimple\"", json);
        Assert.Contains("\"EmptySD3LatentImage\"", json);
    }

    [Fact]
    public void Flux1Dev_applies_flux_guidance()
    {
        var json = BuildJson("flux1-dev", Gen);
        Assert.Contains("\"UNETLoader\"", json);
        Assert.Contains("\"FluxGuidance\"", json);
        Assert.Contains("\"guidance\":3.5", json);
    }

    [Fact]
    public void FluxKontext_builds_a_reference_latent_edit_graph()
    {
        var json = BuildJson("flux1-kontext", Edit);
        Assert.Contains("\"ReferenceLatent\"", json);
        Assert.Contains("\"FluxGuidance\"", json);
        Assert.Contains("\"SaveImage\"", json);
    }

    [Fact]
    public void WanI2V_builds_a_video_graph()
    {
        var json = BuildJson("wan22-ti2v-5b", Edit);
        Assert.Contains("\"Wan22ImageToVideoLatent\"", json);
        Assert.Contains("\"SaveAnimatedWEBP\"", json);
    }

    [Fact]
    public void Ltxv_builds_a_video_graph()
    {
        var json = BuildJson("ltxv-i2v", Edit);
        Assert.Contains("\"LTXVImgToVideo\"", json);
        Assert.Contains("\"SaveAnimatedWEBP\"", json);
    }

    [Fact]
    public void QwenImageEdit_builds_a_qwen_edit_graph()
    {
        var json = BuildJson("qwen-image-edit", Edit);
        Assert.Contains("\"TextEncodeQwenImageEditPlus\"", json);
        Assert.Contains("\"SaveImage\"", json);
    }

    [Fact]
    public void QwenImageEdit_without_a_mask_is_unchanged()
    {
        // Default path (all mask_*_pct=0 from the schema defaults): the sampler reads the source latent directly and
        // SaveImage consumes the raw VAEDecode — no reframe nodes at all.
        var json = BuildJson("qwen-image-edit", Edit);
        Assert.DoesNotContain("\"ImageCompositeMasked\"", json);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("14", root.GetProperty("3").GetProperty("inputs").GetProperty("latent_image")[0].GetString());
        Assert.Equal("8", root.GetProperty("9").GetProperty("inputs").GetProperty("images")[0].GetString());
    }

    [Theory]                                                     // L    R    T    B    → rect x, y, w, h  (src 1216×832)
    [InlineData(0,  0,  50, 0,   0,   416, 1216, 416)]           // lower half
    [InlineData(0,  0,  34, 0,   0,   282, 1216, 550)]           // lower ⅔ — the crouch case
    [InlineData(25, 25, 0,  0,   304, 0,   608,  832)]           // centre column
    [InlineData(0,  0,  0,  50,  0,   0,   1216, 416)]           // upper half
    public void QwenImageEdit_mask_pct_samples_the_rect_and_composites_it_back(int l, int r, int t, int b, int x, int y, int w, int h)
    {
        // Each mask_*_pct blocks dim·pct/100 px on that side; what's left is the drawing rect. The sampler runs on a
        // latent shaped like the RECT (stride-aligned), and the decode is pasted back onto a white full-size canvas at
        // the rect's offset — so the model fills a correctly-shaped frame rather than being clipped by a mask.
        var (catalog, registry) = Build();
        var cfg = catalog.FindConfig("qwen-image-edit")!;
        var wf = registry.Find(cfg.WorkflowName)!;
        var v = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in wf.Schema) if (s.Default is not null) v[s.Key] = s.Default;
        foreach (var kv in cfg.Params) v[kv.Key] = kv.Value.Value;
        // Mirrors ComfyClient.MergeParamsDict: this machine's settings sit over the shipped configuration.
        foreach (var kv in catalog.ParamOverridesFor(cfg.Id)) v[kv.Key] = kv.Value;
        v["mask_left_pct"] = l; v["mask_right_pct"] = r; v["mask_top_pct"] = t; v["mask_bottom_pct"] = b;
        var graph = wf.Build(new ParamValues(v), catalog.Resolve(cfg), Edit);   // Edit = 1216×832

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(graph));
        var root = doc.RootElement;

        // The sampled canvas is the rect, rounded DOWN to the 16px latent stride.
        var seed = root.GetProperty("80");
        Assert.Equal("EmptyImage", seed.GetProperty("class_type").GetString());
        Assert.Equal(w - w % 16, seed.GetProperty("inputs").GetProperty("width").GetInt32());
        Assert.Equal(h - h % 16, seed.GetProperty("inputs").GetProperty("height").GetInt32());
        Assert.Equal("81", root.GetProperty("3").GetProperty("inputs").GetProperty("latent_image")[0].GetString());

        // Stride rounding undone, then pasted at the rect offset onto a full-size white canvas.
        var back = root.GetProperty("82");
        Assert.Equal(w, back.GetProperty("inputs").GetProperty("width").GetInt32());
        Assert.Equal(h, back.GetProperty("inputs").GetProperty("height").GetInt32());
        var canvas = root.GetProperty("83");
        Assert.Equal(1216, canvas.GetProperty("inputs").GetProperty("width").GetInt32());
        Assert.Equal(832, canvas.GetProperty("inputs").GetProperty("height").GetInt32());
        Assert.Equal(16777215, canvas.GetProperty("inputs").GetProperty("color").GetInt32());   // white margin
        var comp = root.GetProperty("84");
        Assert.Equal("ImageCompositeMasked", comp.GetProperty("class_type").GetString());
        Assert.Equal(x, comp.GetProperty("inputs").GetProperty("x").GetInt32());
        Assert.Equal(y, comp.GetProperty("inputs").GetProperty("y").GetInt32());

        // SaveImage takes the composited canvas, resized to the unmasked path's own output dims (node 11's bucket).
        Assert.Equal("86", root.GetProperty("9").GetProperty("inputs").GetProperty("images")[0].GetString());
        Assert.Equal("11", root.GetProperty("85").GetProperty("inputs").GetProperty("image")[0].GetString());

        // Conditioning is untouched: the text encoder still sees the FULL source image, preserving identity + scale.
        Assert.Equal("11", root.GetProperty("13").GetProperty("inputs").GetProperty("image1")[0].GetString());
        Assert.Equal("14", root.GetProperty("30").GetProperty("inputs").GetProperty("latent")[0].GetString());
    }

    [Theory]
    [InlineData(60, 60, 0, 0)]     // opposing margins leave no width
    [InlineData(0, 0, 50, 50)]     // opposing margins leave no height
    [InlineData(0, 0, 120, 0)]     // out of range
    public void QwenImageEdit_rejects_a_degenerate_mask(int l, int r, int t, int b)
    {
        var (catalog, registry) = Build();
        var cfg = catalog.FindConfig("qwen-image-edit")!;
        var wf = registry.Find(cfg.WorkflowName)!;
        var v = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in wf.Schema) if (s.Default is not null) v[s.Key] = s.Default;
        foreach (var kv in cfg.Params) v[kv.Key] = kv.Value.Value;
        // Mirrors ComfyClient.MergeParamsDict: this machine's settings sit over the shipped configuration.
        foreach (var kv in catalog.ParamOverridesFor(cfg.Id)) v[kv.Key] = kv.Value;
        v["mask_left_pct"] = l; v["mask_right_pct"] = r; v["mask_top_pct"] = t; v["mask_bottom_pct"] = b;
        Assert.ThrowsAny<ArgumentException>(() => wf.Build(new ParamValues(v), catalog.Resolve(cfg), Edit));
    }

    [Fact]
    public void AnimateDiffI2V_builds_sparsectrl_ipadapter_graph()
    {
        var ltng = BuildJson("animatediff-lightning-i2v", Edit);
        Assert.Contains("IPAdapterUnifiedLoader", ltng);
        Assert.Contains("\"IPAdapter\"", ltng);               // the apply node (distinct from the loader)
        Assert.Contains("ACN_SparseCtrlLoaderAdvanced", ltng);
        Assert.Contains("ControlNetApplyAdvanced", ltng);
        Assert.Contains("\"SaveAnimatedWEBP\"", ltng);
        Assert.DoesNotContain("LoraLoaderModelOnly", ltng);   // Lightning has no LCM LoRA

        var lcm = BuildJson("animatelcm-i2v", Edit);
        Assert.Contains("LoraLoaderModelOnly", lcm);          // AnimateLCM applies the LCM LoRA
        Assert.Contains("\"sampler_name\":\"lcm\"", lcm);     // and samples with lcm
    }

    [Fact]
    public void HiDream_builds_a_quad_clip_sd3_graph()
    {
        var json = BuildJson("hidream-full", Gen);
        Assert.Contains("\"QuadrupleCLIPLoader\"", json);
        Assert.Contains("\"ModelSamplingSD3\"", json);
        Assert.Contains("\"EmptySD3LatentImage\"", json);
        Assert.Contains("\"shift\":3", json);              // Full flow-shift
        Assert.Contains("\"clip_name4\"", json);           // the llama encoder slot
    }

    [Fact]
    public void HiDreamDev_uses_its_own_shift_and_sampler()
    {
        var json = BuildJson("hidream-dev", Gen);
        Assert.Contains("\"shift\":6", json);              // Dev flow-shift (per-config, flows through MergeParams)
        Assert.Contains("\"sampler_name\":\"lcm\"", json);
        Assert.Contains("\"steps\":28", json);
    }

    [Fact]
    public void Sd35LargeTriple_builds_a_checkpoint_triple_clip_graph()
    {
        var json = BuildJson("sd35-large-bf16", Gen);
        Assert.Contains("\"CheckpointLoaderSimple\"", json);
        Assert.Contains("\"TripleCLIPLoader\"", json);
        Assert.Contains("\"EmptySD3LatentImage\"", json);
        Assert.DoesNotContain("\"ModelSamplingSD3\"", json);   // official sd3 t2i wires checkpoint MODEL straight in
    }

    [Fact]
    public void Chroma_builds_a_t5_only_auraflow_graph()
    {
        var json = BuildJson("chroma1-hd", Gen);
        Assert.Contains("\"CLIPLoader\"", json);
        Assert.Contains("\"T5TokenizerOptions\"", json);
        Assert.Contains("\"ModelSamplingAuraFlow\"", json);
        Assert.Contains("\"EmptySD3LatentImage\"", json);
    }

    [Fact]
    public void QwenImageBase_builds_on_the_generic_pipeline()
    {
        var json = BuildJson("qwen-image", Gen);
        Assert.Contains("\"UNETLoader\"", json);
        Assert.Contains("\"ModelSamplingAuraFlow\"", json);   // auraflow 3.1
        Assert.Contains("\"EmptySD3LatentImage\"", json);
    }

    [Fact]
    public void Flux2Dev_applies_flux_guidance()
    {
        var json = BuildJson("flux2-dev", Gen);
        Assert.Contains("\"UNETLoader\"", json);
        Assert.Contains("\"FluxGuidance\"", json);
        Assert.Contains("\"EmptyFlux2LatentImage\"", json);
    }

    /// <summary>
    /// No configuration is another one wearing a different name. The catalogue grew a second workflow per model
    /// -- an "-hq" sibling -- whose only difference was a VRAM floor and which precision it loaded, and then a
    /// third that was byte-identical apart from one link. Both of those are now the user's choice at bind time,
    /// so a pair that agrees on graph, requirements and parameters is a duplicate, not a variant.
    /// </summary>
    [Fact]
    public void No_configuration_is_another_one_under_a_different_name()
    {
        var (catalog, _) = Build();

        var duplicates = catalog.AllConfigs()
            .GroupBy(c => string.Join("|",
                c.WorkflowName,
                string.Join(",", c.Requirements.All().OrderBy(x => x, StringComparer.Ordinal)),
                string.Join(",", c.Params.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                                         .Select(kv => kv.Key + "=" + kv.Value.Value))))
            .Where(g => g.Count() > 1)
            .Select(g => string.Join(" == ", g.Select(c => c.Id)))
            .ToList();

        Assert.Empty(duplicates);
    }

    /// <summary>
    /// The loader node follows the FILE, not the configuration. A workflow does not know or care which
    /// quantisation you have — that is the whole reason the catalogue no longer needs a second workflow per
    /// precision — so the same config must emit UnetLoaderGGUF for a .gguf and UNETLoader for a .safetensors.
    /// </summary>
    [Theory]
    [InlineData("flux1-dev")]
    [InlineData("z-image-turbo")]
    public void The_diffusion_loader_is_chosen_by_the_bound_file(string configId)
    {
        Assert.Contains("\"UnetLoaderGGUF\"", BuildJsonBoundTo(configId, ".gguf"));
        Assert.Contains("\"UNETLoader\"", BuildJsonBoundTo(configId, ".safetensors"));
    }

    /// <summary>Build a config's graph with every slot bound to a file of the given extension.</summary>
    private static string BuildJsonBoundTo(string configId, string extension)
    {
        var (catalog, registry) = Build();
        catalog.SetBindings(catalog.AllRequirements().ToDictionary(r => r.Id, r => r.Id + extension));
        var cfg = catalog.FindConfig(configId);
        Assert.NotNull(cfg);
        var wf = registry.Find(cfg!.WorkflowName);
        Assert.NotNull(wf);
        return JsonSerializer.Serialize(wf!.Build(Merge(catalog, wf, cfg), catalog.Resolve(cfg), Gen));
    }

    /// <summary>
    /// A machine setting reaches the graph. The override endpoint stored rows that nothing read — every size or
    /// step change a user made was accepted and silently ignored — so this asserts the whole path: stored value,
    /// through the merge, into the emitted node.
    /// </summary>
    [Fact]
    public void A_machine_setting_overrides_the_shipped_one()
    {
        var (catalog, registry) = Build();
        var cfg = catalog.FindConfig("flux1-dev");
        Assert.NotNull(cfg);
        var wf = registry.Find(cfg!.WorkflowName);
        Assert.NotNull(wf);

        var shipped = JsonSerializer.Serialize(wf!.Build(Merge(catalog, wf, cfg), catalog.Resolve(cfg), Gen));
        Assert.Contains("\"steps\":28", shipped);

        catalog.SetParamOverrides(new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["flux1-dev"] = new Dictionary<string, string> { ["param.steps"] = "12" },
        });

        var overridden = JsonSerializer.Serialize(wf.Build(Merge(catalog, wf, cfg), catalog.Resolve(cfg), Gen));
        Assert.Contains("\"steps\":12", overridden);
        Assert.DoesNotContain("\"steps\":28", overridden);
    }

    [Fact]
    public void WanA14b_i2v_builds_a_two_expert_moe_video_graph()
    {
        var json = BuildJson("wan22-i2v-a14b", Edit);
        Assert.Contains("\"WanImageToVideo\"", json);
        Assert.Contains("\"KSamplerAdvanced\"", json);
        Assert.Contains("\"return_with_leftover_noise\":\"enable\"", json);   // high-noise expert
        Assert.Contains("\"return_with_leftover_noise\":\"disable\"", json);  // low-noise expert
        Assert.Contains("wan2-2-i2v-low-noise-14b.safetensors", json);     // second expert via unet_low (int8 ConvRot)
        Assert.Contains("\"UNETLoader\"", json);                               // int8 .safetensors -> UNETLoader, not GGUF
        Assert.Contains("\"SaveAnimatedWEBP\"", json);
    }

    [Fact]
    public void WanA14b_i2v_with_a_last_frame_swaps_to_WanFirstLastFrameToVideo()
    {
        // An END frame (the source is the first frame) flips the conditioning node to WanFirstLastFrameToVideo with
        // both start_image and end_image wired, and loads the end frame via its own LoadImage. The plain i2v node is gone.
        var inputs = new WorkflowInputs { Positive = "make it red", SourceImageName = "src.png", EndImageName = "forgemcp_edit_last.png", SourceWidth = 1216, SourceHeight = 832 };
        var json = BuildJson("wan22-i2v-a14b", inputs);
        Assert.Contains("\"WanFirstLastFrameToVideo\"", json);
        Assert.DoesNotContain("\"WanImageToVideo\"", json);
        Assert.Contains("forgemcp_edit_last.png", json);
        Assert.Contains("\"end_image\"", json);
    }

    [Theory]
    //          L,   R,   T,   B  → canvasW, canvasH, offsetX, offsetY   (source Edit = 1216×832)
    [InlineData(200, 0,   0,   0,   3648, 832,  2432, 0)]     // left:   whitespace left,   char flush right
    [InlineData(100, 100, 0,   0,   3648, 832,  1216, 0)]     // center: whitespace split,  char centered
    [InlineData(0,   200, 0,   0,   3648, 832,  0,    0)]     // right:  whitespace right,  char flush left
    [InlineData(0,   0,   100, 0,   1216, 1664, 0,    832)]   // top:    whitespace top,    char flush bottom
    [InlineData(0,   0,   0,   100, 1216, 1664, 0,    0)]     // bottom: whitespace bottom, char flush top
    public void WanA14b_i2v_pad_pct_builds_the_expected_canvas(int l, int r, int t, int b, int w, int h, int x, int y)
    {
        // Each pad_*_pct adds dim·pct/100 px on that side; the source is composited onto the enlarged white canvas at
        // the top-left additions, and THAT feeds the total-pixel scale instead of the raw LoadImage.
        var (catalog, registry) = Build();
        var cfg = catalog.FindConfig("wan22-i2v-a14b")!;
        var wf = registry.Find(cfg.WorkflowName)!;
        var v = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in wf.Schema) if (s.Default is not null) v[s.Key] = s.Default;
        foreach (var kv in cfg.Params) v[kv.Key] = kv.Value.Value;
        // Mirrors ComfyClient.MergeParamsDict: this machine's settings sit over the shipped configuration.
        foreach (var kv in catalog.ParamOverridesFor(cfg.Id)) v[kv.Key] = kv.Value;
        v["pad_left_pct"] = l; v["pad_right_pct"] = r; v["pad_top_pct"] = t; v["pad_bottom_pct"] = b;
        var graph = wf.Build(new ParamValues(v), catalog.Resolve(cfg), Edit);   // Edit = 1216×832

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(graph));
        var root = doc.RootElement;
        var canvas = root.GetProperty("71");
        Assert.Equal("EmptyImage", canvas.GetProperty("class_type").GetString());
        Assert.Equal(w, canvas.GetProperty("inputs").GetProperty("width").GetInt32());
        Assert.Equal(h, canvas.GetProperty("inputs").GetProperty("height").GetInt32());
        Assert.Equal(16777215, canvas.GetProperty("inputs").GetProperty("color").GetInt32());   // white
        var comp = root.GetProperty("73");
        Assert.Equal("ImageCompositeMasked", comp.GetProperty("class_type").GetString());
        Assert.Equal(x, comp.GetProperty("inputs").GetProperty("x").GetInt32());
        Assert.Equal(y, comp.GetProperty("inputs").GetProperty("y").GetInt32());
        // The budget scale consumes the padded composite (node 73), not the raw LoadImage (node 10).
        Assert.Equal("73", root.GetProperty("11").GetProperty("inputs").GetProperty("image")[0].GetString());
        Assert.Contains("\"InvertMask\"", JsonSerializer.Serialize(graph));   // alpha-respecting composite
    }

    [Fact]
    public void WanA14b_i2v_without_padding_is_unchanged()
    {
        // Default path (all pad_*_pct=0 from the schema defaults): no canvas/composite, scale reads the raw image.
        var json = BuildJson("wan22-i2v-a14b", Edit);
        Assert.DoesNotContain("\"EmptyImage\"", json);
        Assert.DoesNotContain("\"ImageCompositeMasked\"", json);
        using var doc = JsonDocument.Parse(json);
        var scaleImg = doc.RootElement.GetProperty("11").GetProperty("inputs").GetProperty("image");
        Assert.Equal("10", scaleImg[0].GetString());   // scale consumes LoadImage directly
    }

    [Fact]
    public void WanA14b_t2v_builds_an_empty_latent_moe_graph()
    {
        var json = BuildJson("wan22-t2v-a14b", Gen);
        Assert.Contains("\"EmptyHunyuanLatentVideo\"", json);
        Assert.Contains("\"KSamplerAdvanced\"", json);
        Assert.Contains("wan2-2-t2v-low-noise-14b.safetensors", json);   // low-noise expert via unet_low (int8 ConvRot)
        Assert.Contains("\"UNETLoader\"", json);                            // int8 .safetensors -> UNETLoader, not GGUF
        Assert.DoesNotContain("\"WanImageToVideo\"", json);   // t2v has no source image
    }

    [Fact]
    public void HunyuanVideo15_t2v_builds_a_cfgguider_video_graph()
    {
        var json = BuildJson("hunyuanvideo15-t2v", Gen);
        Assert.Contains("\"EmptyHunyuanVideo15Latent\"", json);
        Assert.Contains("\"hunyuan_video_15\"", json);
        Assert.Contains("\"CFGGuider\"", json);
        Assert.Contains("\"SamplerCustomAdvanced\"", json);
        Assert.Contains("\"SaveAnimatedWEBP\"", json);
    }

    [Fact]
    public void HunyuanVideo_t2v_builds_a_fluxguidance_basicguider_graph()
    {
        var json = BuildJson("hunyuanvideo-t2v", Gen);
        Assert.Contains("\"UNETLoader\"", json);
        Assert.Contains("\"EmptyHunyuanLatentVideo\"", json);
        Assert.Contains("\"FluxGuidance\"", json);
        Assert.Contains("\"BasicGuider\"", json);
        Assert.Contains("\"VAEDecodeTiled\"", json);
    }

    [Fact]
    public void Ltx23_i2v_reuses_the_ltx2_graph_with_23_files()
    {
        var json = BuildJson("ltx23-i2v", Edit);
        Assert.Contains("\"LTXVImgToVideo\"", json);
        Assert.Contains("\"LTXVScheduler\"", json);
        Assert.Contains("ltx-2-3-22b-distilled-1-1.safetensors", json);
        Assert.Contains("ltx-2-3-text-projection.safetensors", json);   // loaded as DualCLIPLoader clip2
        Assert.Contains("\"SaveAnimatedWEBP\"", json);
    }

    [Fact]
    public void Krea2_neutral_rebalance_omits_the_node()
    {
        // Neutral values (multiplier 1.0 + all-ones weights) → no rebalance node; the graph is plain Krea 2. The
        // krea2 configs now BAKE a non-neutral rebalance, so force neutral here to exercise the workflow's skip path.
        var (catalog, registry) = Build();
        var cfg = catalog.FindConfig("krea2-turbo")!;
        var wf = registry.Find(cfg.WorkflowName)!;
        var v = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in wf.Schema) if (s.Default is not null) v[s.Key] = s.Default;
        foreach (var kv in cfg.Params) v[kv.Key] = kv.Value.Value;
        // Mirrors ComfyClient.MergeParamsDict: this machine's settings sit over the shipped configuration.
        foreach (var kv in catalog.ParamOverridesFor(cfg.Id)) v[kv.Key] = kv.Value;
        v["rebalance_multiplier"] = 1.0;
        v["per_layer_weights"] = "1.0,1.0,1.0,1.0,1.0,1.0,1.0,1.0,1.0,1.0,1.0,1.0";
        var json = JsonSerializer.Serialize(wf.Build(new ParamValues(v), catalog.Resolve(cfg), Gen));
        Assert.Contains("\"type\":\"krea2\"", json);
        Assert.Contains("krea2-turbo.safetensors", json);
        Assert.DoesNotContain("ConditioningKrea2Rebalance", json);
        using var doc = JsonDocument.Parse(json);
        // The sampler reads the positive text-encode (node 6) directly.
        Assert.Equal("6", doc.RootElement.GetProperty("3").GetProperty("inputs").GetProperty("positive")[0].GetString());
    }

    [Fact]
    public void Krea2_turbo_config_bakes_the_uncensor_rebalance_by_default()
    {
        // The krea2-turbo config bakes multiplier 4.0 + the uncensor weights (exposed:false), so a plain build with
        // merged config defaults (no overrides) splices the rebalance node between the encode and the sampler.
        var json = BuildJson("krea2-turbo", Gen);
        using var doc = JsonDocument.Parse(json);
        var node = doc.RootElement.GetProperty("13");
        Assert.Equal("ConditioningKrea2Rebalance", node.GetProperty("class_type").GetString());
        Assert.Equal(4.0, node.GetProperty("inputs").GetProperty("multiplier").GetDouble());
        Assert.Equal("1.0,1.0,1.0,1.0,1.0,1.0,1.0,2.5,5.0,1.1,4.0,1.0",
            node.GetProperty("inputs").GetProperty("per_layer_weights").GetString());
        Assert.Equal("13", doc.RootElement.GetProperty("3").GetProperty("inputs").GetProperty("positive")[0].GetString());
    }

    [Fact]
    public void Krea2_with_rebalance_splices_the_node_between_encode_and_sampler()
    {
        var (catalog, registry) = Build();
        var cfg = catalog.FindConfig("krea2-turbo")!;
        var wf = registry.Find(cfg.WorkflowName)!;
        var v = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in wf.Schema) if (s.Default is not null) v[s.Key] = s.Default;
        foreach (var kv in cfg.Params) v[kv.Key] = kv.Value.Value;
        // Mirrors ComfyClient.MergeParamsDict: this machine's settings sit over the shipped configuration.
        foreach (var kv in catalog.ParamOverridesFor(cfg.Id)) v[kv.Key] = kv.Value;
        v["rebalance_multiplier"] = 2.0;
        v["per_layer_weights"] = "1.0,1.0,1.0,1.0,1.0,1.0,1.0,2.5,5.0,1.1,4.0,1.0";
        var graph = wf.Build(new ParamValues(v), catalog.Resolve(cfg), Gen);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(graph));
        var root = doc.RootElement;
        var node = root.GetProperty("13");
        Assert.Equal("ConditioningKrea2Rebalance", node.GetProperty("class_type").GetString());
        Assert.Equal(2.0, node.GetProperty("inputs").GetProperty("multiplier").GetDouble());
        Assert.Equal("1.0,1.0,1.0,1.0,1.0,1.0,1.0,2.5,5.0,1.1,4.0,1.0",
            node.GetProperty("inputs").GetProperty("per_layer_weights").GetString());
        // The rebalance consumes the positive text-encode (node 6)...
        Assert.Equal("6", node.GetProperty("inputs").GetProperty("conditioning")[0].GetString());
        // ...and the sampler now consumes the rebalance (node 13), not the raw encode.
        Assert.Equal("13", root.GetProperty("3").GetProperty("inputs").GetProperty("positive")[0].GetString());
    }

    [Fact]
    public void Krea2_rebalance_activates_on_weights_only()
    {
        // Multiplier neutral (1.0) but non-neutral weights still activates the node (weights-only rebalance).
        var (catalog, registry) = Build();
        var cfg = catalog.FindConfig("krea2")!;
        var wf = registry.Find(cfg.WorkflowName)!;
        var v = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in wf.Schema) if (s.Default is not null) v[s.Key] = s.Default;
        foreach (var kv in cfg.Params) v[kv.Key] = kv.Value.Value;
        // Mirrors ComfyClient.MergeParamsDict: this machine's settings sit over the shipped configuration.
        foreach (var kv in catalog.ParamOverridesFor(cfg.Id)) v[kv.Key] = kv.Value;
        v["rebalance_multiplier"] = 1.0;   // force neutral multiplier (the config now bakes 4.0) to isolate weights-only
        v["per_layer_weights"] = "1.0,1.0,1.0,1.0,1.0,1.0,1.0,2.5,5.0,1.1,4.0,1.0";
        var json = JsonSerializer.Serialize(wf.Build(new ParamValues(v), catalog.Resolve(cfg), Gen));
        Assert.Contains("ConditioningKrea2Rebalance", json);
        Assert.Contains("\"multiplier\":1", json);
    }

    [Fact]
    public void Krea2Refine_builds_base_then_turbo_polish_chain()
    {
        var json = BuildJson("krea2-refine", Gen);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        // Two UNet loaders: RAW base in node 4, Turbo refiner (motion_model slot) in node 40. The RAW base resolves to
        // the int8 quant — it's the registered file that's actually on disk (the fp8 one is registered but not
        // downloaded, and the requirement presence-gates on what's there).
        Assert.Equal("krea2-raw.safetensors", root.GetProperty("4").GetProperty("inputs").GetProperty("unet_name").GetString());
        Assert.Equal("krea2-turbo.safetensors", root.GetProperty("40").GetProperty("inputs").GetProperty("unet_name").GetString());
        // Baked uncensor rebalance is spliced (node 13).
        Assert.Equal("ConditioningKrea2Rebalance", root.GetProperty("13").GetProperty("class_type").GetString());
        // Stage 1 base sampler (node 3): full denoise at base cfg 4, base model (node 4), from the empty latent (node 5).
        var s1 = root.GetProperty("3").GetProperty("inputs");
        Assert.Equal(1.0, s1.GetProperty("denoise").GetDouble());
        Assert.Equal(4.0, s1.GetProperty("cfg").GetDouble());
        Assert.Equal("4", s1.GetProperty("model")[0].GetString());
        Assert.Equal("13", s1.GetProperty("positive")[0].GetString());
        Assert.Equal("5", s1.GetProperty("latent_image")[0].GetString());
        // Stage 2 Turbo polish (node 30): partial denoise over the base latent (node 3), cfg 1, Turbo model (node 40).
        var s2 = root.GetProperty("30").GetProperty("inputs");
        Assert.Equal(0.35, s2.GetProperty("denoise").GetDouble());
        Assert.Equal(1.0, s2.GetProperty("cfg").GetDouble());
        Assert.Equal("40", s2.GetProperty("model")[0].GetString());
        Assert.Equal("3", s2.GetProperty("latent_image")[0].GetString());
        Assert.Equal("13", s2.GetProperty("positive")[0].GetString());
        // The decode reads the polish sampler (node 30), not the base pass.
        Assert.Equal("30", root.GetProperty("8").GetProperty("inputs").GetProperty("samples")[0].GetString());
    }

    [Fact]
    public void Overrides_change_the_emitted_steps()
    {
        // The settings layer + an override flow into the graph: raise steps via a merged value.
        var (catalog, registry) = Build();
        var cfg = catalog.FindConfig("pony-v6")!;
        var wf = registry.Find(cfg.WorkflowName)!;
        var v = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in wf.Schema) if (s.Default is not null) v[s.Key] = s.Default;
        foreach (var kv in cfg.Params) v[kv.Key] = kv.Value.Value;
        // Mirrors ComfyClient.MergeParamsDict: this machine's settings sit over the shipped configuration.
        foreach (var kv in catalog.ParamOverridesFor(cfg.Id)) v[kv.Key] = kv.Value;
        v["steps"] = 42;   // an override
        var json = JsonSerializer.Serialize(wf.Build(new ParamValues(v), catalog.Resolve(cfg), Gen));
        Assert.Contains("\"steps\":42", json);
    }

    [Fact]
    public void Qwen_pixelizer_with_reference_and_snap_references_the_scale_node_not_an_inline_dict()
    {
        // Regression: the QIE pixelizer's reference>0 branch VAE-encodes a snapped source. The FixedScale must be
        // its OWN node and REFERENCED — passing the node dict inline as `pixels` handed the encoder a dict
        // ('dict' object has no attribute 'shape'). Shared by pixelize-qwen/-longcat/-longcat-turbo/-firered.
        var req = new ResolvedRequirements
        {
            Checkpoint = "qwen.gguf", TextEncoders = new[] { "te.gguf" }, Vae = "vae.safetensors",
            Resolution = new ModelResolution { MinW = 928, MinH = 928, MaxW = 1664, MaxH = 1664, Step = 16 },
        };
        var pv = new ParamValues(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["reference"] = 80, ["virtual_resolution"] = 256, ["snap_resolution"] = true,
            ["loader"] = "unet_gguf", ["clip_type"] = "qwen_image",
        });
        var inputs = new WorkflowInputs { SourceImageName = "src.png", SourceWidth = 1216, SourceHeight = 832 };
        var graph = new QwenPixelizeWorkflow().Build(pv, req, inputs);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(graph));
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("25", out var n25));                       // FixedScale exists as its own node
        Assert.Equal("ImageScale", n25.GetProperty("class_type").GetString());
        var pixels = root.GetProperty("21").GetProperty("inputs").GetProperty("pixels");
        Assert.Equal(JsonValueKind.Array, pixels.ValueKind);                        // a [id, idx] ref, not an inline node
        Assert.Equal("25", pixels[0].GetString());
    }
}
