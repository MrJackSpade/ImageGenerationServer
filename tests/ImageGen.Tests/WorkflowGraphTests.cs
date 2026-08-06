using ImageGen.Application.Rendering;
using ImageGen.Domain;
using ImageGen.Comfy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace ImageGen.Tests;

/// <summary>
/// Exercises the workflow-focused path end to end without a backend: load the real workflows.json +
/// requirements.json, resolve a configuration to its workflow, merge its parameter settings layer, and build the
/// ComfyUI graph. Asserts the structural fingerprints of each loader/latent/guidance/edit family so a parsing,
/// coercion, or merge regression is caught.
/// </summary>
public sealed class WorkflowGraphTests
{
    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "configurations", "models")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new DirectoryNotFoundException("configurations/ not found above the test bin dir.");
    }

    private static (WorkflowCatalog catalog, WorkflowRegistry registry) Build()
    {
        string root = RepoRoot();
        ComfyOptions cfg = new()
        {
            CatalogPath = Path.Combine(root, "configurations"),
        };
        WorkflowCatalog catalog = new(cfg, NullLogger<WorkflowCatalog>.Instance);
        // Bind every slot to a synthetic filename derived from its id. These tests assert that the file bound to a
        // slot reaches the right loader node -- which is the actual invariant. Asserting the AUTHOR's filenames would
        // bake one machine's disk into the suite.
        // One rule, no special cases: a slot no longer declares a precision, so there is nothing here to key a
        // .gguf off. WHICH loader node a graph emits is a property of the bound file now, and is asserted by
        // The_diffusion_loader_is_chosen_by_the_bound_file rather than by every workflow test in passing.
        catalog.SetBindings(catalog.AllRequirements().ToDictionary(r => r.Id, r => r.Id + ".safetensors"));
        // The FULL workflow set from the real DI registration — the exact set the app serves, including the
        // factory-registered decorators (the PixelVideoWorkflow wrappers). A hardcoded list would drift out of date
        // (new workflows would silently escape the graph tests); this stays complete on its own.
        IWorkflow[] all = [.. new ServiceCollection().AddWorkflows().BuildServiceProvider().GetServices<IWorkflow>()];
        return (catalog, new WorkflowRegistry(all));
    }

    /// <summary>
    /// Replicates ComfyClient.MergeParams: schema defaults overlaid by the configuration's settings layer, then
    /// IsModelRef parameters resolved from slot id to bound filename.
    ///
    /// <para>The duplication is a known wart — this has to stay in step with <c>MergeParamsDict</c> by hand, and
    /// falling out of step lets a rule like model-ref resolution be present there and missing here.</para>
    /// </summary>
    private static Dictionary<string, object?> Merge(WorkflowCatalog catalog, IWorkflow wf, WorkflowConfiguration cfg)
    {
        Dictionary<string, object?> v = new(StringComparer.OrdinalIgnoreCase);
        foreach (ParamSpec s in wf.Schema)
        {
            if (s.Default is not null)
            {
                v[s.Key] = s.Default;
            }
        }

        foreach (KeyValuePair<string, ConfigParam> kv in cfg.Params)
        {
            v[kv.Key] = kv.Value.Value;
        }
        // Mirrors ComfyClient.MergeParamsDict: this machine's settings sit over the shipped configuration.
        foreach (KeyValuePair<string, JsonElement> kv in catalog.ParamOverridesFor(cfg.Id))
        {
            v[kv.Key] = kv.Value;
        }
        // The real thing, not a copy of it. Duplicating this loop here would let it fall out of step with the
        // renderer — which is precisely how a resolution rule can be right in the tests and wrong live.
        catalog.ResolveModelRefs(wf, cfg.Id, v);
        return v;
    }

    private static string BuildJson(string configId, WorkflowInputs inputs)
    {
        (WorkflowCatalog? catalog, WorkflowRegistry? registry) = Build();
        WorkflowConfiguration? cfg = catalog.FindConfig(configId);
        Assert.NotNull(cfg);
        IWorkflow? wf = registry.Find(cfg.WorkflowName);
        Assert.NotNull(wf);
        ComfyWorkflowGraph graph = wf.Build(Merge(catalog, wf, cfg), catalog.Resolve(cfg), inputs);
        Assert.NotEmpty(graph.Raw);
        return JsonSerializer.Serialize(graph);
    }

    /// <summary>Like <see cref="BuildJson"/> but lets a test override merged param values (e.g. drive a strength knob
    /// to 0) before the graph is built — the path an API/MCP submission takes past the UI slider.</summary>
    private static string BuildJson(string configId, WorkflowInputs inputs, IReadOnlyDictionary<string, object?> overrides)
    {
        (WorkflowCatalog? catalog, WorkflowRegistry? registry) = Build();
        WorkflowConfiguration? cfg = catalog.FindConfig(configId);
        Assert.NotNull(cfg);
        IWorkflow? wf = registry.Find(cfg.WorkflowName);
        Assert.NotNull(wf);
        Dictionary<string, object?> merged = new(Merge(catalog, wf, cfg), StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, object?> o in overrides)
        {
            merged[o.Key] = o.Value;
        }

        ComfyWorkflowGraph graph = wf.Build(merged, catalog.Resolve(cfg), inputs);
        Assert.NotEmpty(graph.Raw);
        return JsonSerializer.Serialize(graph);
    }

    /// <summary>Krea 2 refine's polish pass (<c>polish_denoise</c>) has a floor of 0, and at 0 the whole Turbo stage is
    /// OMITTED — not emitted at strength 0 — the neutral-skip pattern (#104). At its normal strength both the refiner
    /// model (node 40) and the second sampler (node 30) are present and the decode reads the polished latent; at 0
    /// neither node is emitted and the decode reads the base sampler (node 3) directly.</summary>
    [Fact]
    public void Krea2Refine_omits_the_turbo_polish_pass_at_polish_denoise_zero()
    {
        string polished = BuildJson("krea2-refine", Gen, new Dictionary<string, object?> { [WorkflowParamKeys.PolishDenoise] = 0.35 });
        Assert.Contains("\"30\":{\"class_type\":\"KSampler\"", polished);   // the Turbo polish sampler
        Assert.Contains("\"40\":", polished);                                // the Turbo refiner model loader
        Assert.Contains("\"8\":{\"class_type\":\"VAEDecode\",\"inputs\":{\"samples\":[\"30\",0]", polished);

        string skipped = BuildJson("krea2-refine", Gen, new Dictionary<string, object?> { [WorkflowParamKeys.PolishDenoise] = 0.0 });
        Assert.DoesNotContain("\"30\":", skipped);   // no polish sampler
        Assert.DoesNotContain("\"40\":", skipped);   // refiner model never loaded
        Assert.Contains("\"8\":{\"class_type\":\"VAEDecode\",\"inputs\":{\"samples\":[\"3\",0]", skipped);   // decodes the base render
    }

    private static WorkflowInputs Gen => new() { Positive = "a cat", Negative = "blurry", Aspect = "square" };
    private static WorkflowInputs Edit => new() { Positive = "make it red", SourceImageName = "src.png", SourceWidth = 1216, SourceHeight = 832 };
    private static WorkflowInputs EditMasked => new() { Positive = "make it red", SourceImageName = "src.png", MaskImageName = "mask.png", SourceWidth = 1216, SourceHeight = 832 };

    [Fact]
    public void DeflickerAuto_emits_the_typed_video_correction_graph()
    {
        // First workflow on the typed-graph rails (Build returns a ComfyWorkflowGraph of typed nodes). Locks the
        // emitted JSON byte-for-byte against the hand-built graph it replaced: node ids, class_type-before-inputs,
        // the slot-typed edges, and the literal save settings.
        string json = BuildJson("deflicker-auto", new WorkflowInputs { SourceVideoName = "clip.webm" });

        Assert.Contains("\"10\":{\"class_type\":\"LoadVideo\",\"inputs\":{\"file\":\"clip.webm\"}}", json);
        Assert.Contains("\"11\":{\"class_type\":\"GetVideoComponents\",\"inputs\":{\"video\":[\"10\",0]}}", json);
        // DeflickerAuto: frames from GetVideoComponents out 0, then the four robust thresholds (order preserved).
        Assert.Contains("\"20\":{\"class_type\":\"DeflickerAuto\",\"inputs\":{\"image\":[\"11\",0],\"mad_k\":", json);
        Assert.Contains("\"min_dev\":", json);
        Assert.Contains("\"alpha_cut\":", json);
        Assert.Contains("\"time_sigma\":", json);
        // Save: images from DeflickerAuto out 0, fps from GetVideoComponents out 2 (a wired input, not a literal).
        Assert.Contains("\"class_type\":\"SaveAnimatedWEBP\",\"inputs\":{\"images\":[\"20\",0],\"filename_prefix\":\"forgemcp_edit\",\"fps\":[\"11\",2],\"lossless\":true,\"quality\":100,\"method\":\"default\"}", json);
    }

    [Fact]
    public void Catalog_loads_all_configurations()
    {
        (WorkflowCatalog? catalog, WorkflowRegistry _) = Build();

        // Every file in the tree loads, and none is silently dropped. Counted against the directory rather than a
        // literal: a hardcoded number turns "a workflow failed to load" and "somebody added one" into the same
        // red test, and the number is then updated by whoever is in a hurry.
        int onDisk = Directory.GetFiles(Path.Combine(RepoRoot(), "configurations", "workflows"), "*.json").Length;
        Assert.Equal(onDisk, catalog.AllConfigs().Count);
        WorkflowConfiguration? pony = catalog.FindConfig("pony-v6");
        Assert.NotNull(pony);
        Assert.NotNull(catalog.FindRequirement(pony.Requirements.Checkpoint));
    }

    /// <summary>
    /// EVERY configuration builds a graph from only the parameters it (and its machine overrides) actually declare —
    /// with schema-level <c>ParamSpec.Default</c> stripped, a value a workflow needs must live in that workflow's
    /// JSON, never in code. Any workflow that reaches for a required parameter the config never set refuses the build;
    /// this turns that render-time refusal into a build-time failure naming the exact config and message.
    /// </summary>
    [Fact]
    public void Every_configuration_builds_from_only_its_declared_params()
    {
        (WorkflowCatalog? catalog, WorkflowRegistry? registry) = Build();

        // Maximal inputs so NO input-related refusal (missing source/mask/end/refs) fires — every failure that remains
        // is then a config that failed to supply a parameter its workflow requires.
        WorkflowInputs inputs = new()
        {
            Positive = "a cat",
            Negative = "blurry",
            Aspect = "square",
            SourceImageName = "src.png",
            MaskImageName = "mask.png",
            EndImageName = "end.png",
            SourceVideoName = "src.mp4",
            SourceWidth = 1216,
            SourceHeight = 832,
            References = [new ReferenceInput("ref1.png", ReferenceKind.Image), new ReferenceInput("ref2.png", ReferenceKind.Image)],
        };

        List<string> failures = [];
        foreach (WorkflowConfiguration cfg in catalog.AllConfigs())
        {
            IWorkflow? wf = registry.Find(cfg.WorkflowName);
            if (wf is null)
            {
                failures.Add($"{cfg.Id}: no workflow '{cfg.WorkflowName}'");
                continue;
            }

            try
            {
                _ = wf.Build(Merge(catalog, wf, cfg), catalog.Resolve(cfg), inputs);
            }
            catch (Exception ex)
            {
                failures.Add($"{cfg.Id} ({cfg.WorkflowName}): {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} config(s) could not build:\n" + string.Join("\n", failures));
    }

    /// <summary>
    /// Every requirement link resolves to a model slot that exists. A dangling link is invisible until the
    /// workflow is picked, where it presents as "not installed on this machine" — a lie about the box.
    /// </summary>
    [Fact]
    public void Every_requirement_links_to_a_slot_that_exists()
    {
        (WorkflowCatalog? catalog, WorkflowRegistry _) = Build();

        List<string> dangling = [.. catalog.AllConfigs()
            .SelectMany(c => c.Requirements.All().Select(slot => (Config: c.Id, Slot: slot)))
            .Where(x => catalog.FindRequirement(x.Slot) is null)
            .Select(x => $"{x.Config} -> {x.Slot}")];

        Assert.Empty(dangling);
    }

    /// <summary>
    /// MiniMax-H3's whole reason to exist is NATIVE audio: the video latent decodes to both frames and an audio track,
    /// which <c>CreateVideo</c> muxes into one mp4 written by <c>SaveVideo</c>. It must NOT fall back to the silent
    /// <c>SaveAnimatedWEBP</c> every other video model uses (webp can't carry audio). Two VAEs are loaded — video and
    /// audio — and the prompt is encoded by the single H3 node, so there is no separate CLIPTextEncode.
    /// </summary>
    [Fact]
    public void MiniMaxH3_t2v_builds_the_native_audio_topology_not_a_silent_webp()
    {
        string json = BuildJson("minimax-h3-t2v", Gen);
        Assert.Contains("MiniMaxH3ImageToVideo", json);      // the one H3-specific conditioning+latent node
        Assert.Contains("UNETLoader", json);                 // int8 ConvRot loads through the plain diffusion loader
        Assert.Contains("\"minimax\"", json);                // CLIPLoader type for the Qwen3-VL encoder
        Assert.Contains("BasicGuider", json);                // distilled: no CFG / no negative
        Assert.Contains("SamplerCustomAdvanced", json);
        Assert.Contains("VAEDecodeAudio", json);             // the native audio path
        Assert.Contains("CreateVideo", json);                // muxes frames + audio
        Assert.Contains("SaveVideo", json);                  // a real mp4 with the audio track
        Assert.DoesNotContain("SaveAnimatedWEBP", json);     // NEVER the silent webp
        Assert.Equal(2, json.Split("\"VAELoader\"").Length - 1);   // video VAE + audio VAE
    }

    /// <summary>H3 image→video feeds the uploaded still as the FIRST frame of the same audio topology.</summary>
    [Fact]
    public void MiniMaxH3_i2v_feeds_the_source_as_the_first_frame()
    {
        string json = BuildJson("minimax-h3-i2v", Edit);
        Assert.Contains("MiniMaxH3ImageToVideo", json);
        Assert.Contains("first_frame", json);
        Assert.Contains("LoadImage", json);
        Assert.Contains("SaveVideo", json);
        Assert.DoesNotContain("SaveAnimatedWEBP", json);
    }

    [Fact]
    public void MiniMaxH3_i2v_scales_the_end_frame_like_the_first_frame()
    {
        // An END frame must pass through the SAME ImageScaleToTotalPixels as the first frame, so both reach
        // MiniMaxH3ImageToVideo at identical dims — a same-image loop (#110) then holds still instead of stretching.
        WorkflowInputs inputs = new() { Positive = "make it red", SourceImageName = "src.png", EndImageName = "end.png", SourceWidth = 1216, SourceHeight = 832 };
        string json = BuildJson("minimax-h3-i2v", inputs);
        using JsonDocument doc = JsonDocument.Parse(json);
        // last_frame is wired to the scaled end-frame node (13), not the raw LoadImage (12).
        JsonElement last = doc.RootElement.GetProperty("14").GetProperty("inputs").GetProperty("last_frame");
        Assert.Equal("13", last[0].GetString());
        // Node 13 is an ImageScaleToTotalPixels that consumes the end-frame LoadImage (12), with the SAME settings as
        // the first-frame scale (node 11).
        JsonElement scaled = doc.RootElement.GetProperty("13");
        Assert.Equal("ImageScaleToTotalPixels", scaled.GetProperty("class_type").GetString());
        Assert.Equal("12", scaled.GetProperty("inputs").GetProperty("image")[0].GetString());
        JsonElement first = doc.RootElement.GetProperty("11").GetProperty("inputs");
        JsonElement end = scaled.GetProperty("inputs");
        Assert.Equal(first.GetProperty("megapixels").GetDouble(), end.GetProperty("megapixels").GetDouble());
        Assert.Equal(first.GetProperty("resolution_steps").GetInt32(), end.GetProperty("resolution_steps").GetInt32());
        Assert.Equal(first.GetProperty("upscale_method").GetString(), end.GetProperty("upscale_method").GetString());
    }

    /// <summary>H3 reference→video (ref2va) conditions on the SUBJECT via reference images — the open image is
    /// ref_image_0 and picker references follow — through the <c>MiniMaxH3ReferenceToVideo</c> node, NOT as a first
    /// frame. The references ride the node's autogrow input as the flat dotted wire keys <c>ref_images.ref_image_{i}</c>
    /// (ComfyUI re-nests them server-side), and the audio VAE is a direct node input. Same native-audio topology.</summary>
    [Fact]
    public void MiniMaxH3_ref2v_conditions_on_references_not_a_first_frame()
    {
        WorkflowInputs inputs = new()
        {
            Positive = "she walks through a neon-lit street. Audio: city traffic, a moody synth line.",
            SourceImageName = "src.png",
            SourceWidth = 1216,
            SourceHeight = 832,
            References = [new ReferenceInput("ref1.png", ReferenceKind.Image), new ReferenceInput("ref2.png", ReferenceKind.Image)],
        };
        string json = BuildJson("minimax-h3-ref2v", inputs);
        Assert.Contains("MiniMaxH3ReferenceToVideo", json);      // the ref2va conditioning+latent node
        Assert.DoesNotContain("MiniMaxH3ImageToVideo", json);    // NOT the i2v/t2v node
        Assert.DoesNotContain("first_frame", json);              // references are NOT first frames
        // Source is ref_image_0; the two picker references follow as ref_image_1/_2 — flat DOTTED autogrow keys.
        Assert.Contains("ref_images.ref_image_0", json);
        Assert.Contains("ref_images.ref_image_1", json);
        Assert.Contains("ref_images.ref_image_2", json);
        Assert.DoesNotContain("ref_images.ref_image_3", json);   // exactly 1 source + 2 picker refs
        Assert.Contains("audio_vae", json);                      // the ref node takes the audio VAE directly
        Assert.Contains("ref_image_size", json);
        Assert.Contains("\"match\"", json);
        Assert.Contains("VAEDecodeAudio", json);                 // native audio path intact
        Assert.Contains("CreateVideo", json);
        Assert.Contains("SaveVideo", json);
        Assert.DoesNotContain("SaveAnimatedWEBP", json);
    }

    /// <summary>ref2va routes each reference to the node input for its media KIND: image stills to <c>ref_images</c>,
    /// a driving video (decoded to frames via LoadVideo→GetVideoComponents) to <c>ref_videos</c>, and a driving audio
    /// clip (LoadAudio) to <c>ref_audios</c> — #154's audio+video reference inputs, verified against the shipped node.</summary>
    [Fact]
    public void MiniMaxH3_ref2v_routes_image_video_and_audio_references_to_their_inputs()
    {
        WorkflowInputs inputs = new()
        {
            Positive = "she speaks to camera in a neon street. Audio: her voice, city ambience.",
            SourceImageName = "src.png",
            SourceWidth = 1216,
            SourceHeight = 832,
            References =
            [
                new ReferenceInput("subject.png", ReferenceKind.Image),
                new ReferenceInput("motion.mp4", ReferenceKind.Video),
                new ReferenceInput("voice.wav", ReferenceKind.Audio),
            ],
        };
        string json = BuildJson("minimax-h3-ref2v", inputs);
        // Image: source is ref_image_0, the picker still is ref_image_1.
        Assert.Contains("ref_images.ref_image_0", json);
        Assert.Contains("ref_images.ref_image_1", json);
        // Video: decoded once to frames (ref_videos) AND its own soundtrack (ref_video_audios), same index.
        Assert.Contains("LoadVideo", json);
        Assert.Contains("GetVideoComponents", json);
        Assert.Contains("ref_videos.ref_video_0", json);
        Assert.Contains("ref_video_audios.ref_video_audio_0", json);
        // Audio: a standalone clip loaded and wired to ref_audios.ref_audio_0.
        Assert.Contains("LoadAudio", json);
        Assert.Contains("ref_audios.ref_audio_0", json);
    }

    /// <summary>ref2va refuses more picker references than the configured cap (reference_max=3), rather than silently
    /// dropping them.</summary>
    [Fact]
    public void MiniMaxH3_ref2v_rejects_more_references_than_the_cap()
    {
        WorkflowInputs inputs = new()
        {
            Positive = "a scene. Audio: ambience.",
            SourceImageName = "src.png",
            SourceWidth = 1216,
            SourceHeight = 832,
            References = [new ReferenceInput("r1.png", ReferenceKind.Image), new ReferenceInput("r2.png", ReferenceKind.Image), new ReferenceInput("r3.png", ReferenceKind.Image), new ReferenceInput("r4.png", ReferenceKind.Image)],
        };
        _ = Assert.Throws<RenderValidationException>(() => BuildJson("minimax-h3-ref2v", inputs));
    }

    /// <summary>
    /// Mage-Flow-Edit's Encode/Sampler must NOT reuse the inherited loader-head ids ("5" = CLIPLoader, "6" =
    /// VAELoader). If they do, the last write per id wins and the two loaders vanish, leaving the encode's clip/vae
    /// and the decode's vae pointing at the encode/sampler nodes' own outputs — an invalid graph ComfyUI rejects
    /// (CONDITIONING/LATENT where a CLIP/VAE is required). This asserts both loaders survive with their own ids and
    /// that the clip/vae edges resolve to them, not to the encode/sampler nodes.
    /// </summary>
    [Fact]
    public void MageFlowEdit_keeps_the_clip_and_vae_loaders_and_wires_the_encode_decode_to_them()
    {
        string json = BuildJson("mage-flow-edit", Edit);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        // The split-loader head survives: node "5" is the CLIP loader, node "6" the VAE loader — NOT overwritten by
        // the encode/sampler.
        Assert.Equal("CLIPLoader", root.GetProperty("5").GetProperty("class_type").GetString());
        Assert.Equal("VAELoader", root.GetProperty("6").GetProperty("class_type").GetString());

        // The unified encode node (on its OWN id, not "5") reads the real CLIP loader ("5") and VAE loader ("6").
        JsonElement encode = root.EnumerateObject().Single(p => p.Value.GetProperty("class_type").GetString() == "TextEncodeMageFlowEdit").Value;
        Assert.Equal("5", encode.GetProperty("inputs").GetProperty("clip")[0].GetString());
        Assert.Equal("6", encode.GetProperty("inputs").GetProperty("vae")[0].GetString());

        // The decode reads the same VAE loader ("6"), and its samples come from the KSampler (on its OWN id, not "6").
        JsonElement decode = root.EnumerateObject().Single(p => p.Value.GetProperty("class_type").GetString() == "VAEDecode").Value;
        Assert.Equal("6", decode.GetProperty("inputs").GetProperty("vae")[0].GetString());
        string? samplerId = decode.GetProperty("inputs").GetProperty("samples")[0].GetString();
        Assert.NotNull(samplerId);
        Assert.Equal("KSampler", root.GetProperty(samplerId).GetProperty("class_type").GetString());
        Assert.NotEqual("6", samplerId);
    }

    [Fact]
    public void PixelAnima_is_a_generate_workflow_txt2img_under_projection_plus_final_quantize()
    {
        (WorkflowCatalog? catalog, WorkflowRegistry? registry) = Build();
        WorkflowConfiguration? cfg = catalog.FindConfig("pixelanima");
        Assert.NotNull(cfg);
        IWorkflow? wf = registry.Find(cfg.WorkflowName);
        Assert.NotNull(wf);
        // It's a GENERATE workflow (text→image, no source), not an edit.
        Assert.Equal(WorkflowKind.Generate, wf.Kind);
        Assert.Equal(WorkflowMedia.Image, wf.Media);

        string json = JsonSerializer.Serialize(wf.Build(Merge(catalog, wf, cfg), catalog.Resolve(cfg),
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
        WorkflowInputs inputs = new() { SourceVideoName = "forgemcp_edit_src.mp4" };
        (WorkflowCatalog? catalog, WorkflowRegistry? registry) = Build();
        WorkflowConfiguration? cfg = catalog.FindConfig("pixel-quantize-video");
        Assert.NotNull(cfg);
        IWorkflow? wf = registry.Find(cfg.WorkflowName);
        Assert.NotNull(wf);
        // It declares a VIDEO source (so the edit submit uploads a real clip + loads it) and a VIDEO output.
        Assert.Equal(WorkflowMedia.Video, wf.SourceMedia);
        Assert.Equal(WorkflowMedia.Video, wf.Media);
        Assert.False(wf.RequiresModel);   // model-free — survives the catalog's no-checkpoint gate

        string json = JsonSerializer.Serialize(wf.Build(Merge(catalog, wf, cfg), catalog.Resolve(cfg), inputs));
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
        WorkflowInputs inputs = new() { SourceVideoName = "forgemcp_edit_src.mp4" };
        (WorkflowCatalog? catalog, WorkflowRegistry? registry) = Build();
        WorkflowConfiguration? cfg = catalog.FindConfig("pixel-quantize-video");
        Assert.NotNull(cfg);
        IWorkflow? wf = registry.Find(cfg.WorkflowName);
        Assert.NotNull(wf);

        // Schema no longer carries defaults (Phase B); supply every param the graph reads, engine flipped to fp.
        Dictionary<string, object?> v = new(StringComparer.OrdinalIgnoreCase)
        {
            ["virtual_resolution"] = 128,
            ["palette"] = "chroma-256",
            ["final_method"] = "median",
            ["fps"] = 0,
            ["engine"] = "fp",
            ["thicken"] = 0.75,
            ["tau"] = 0.6,
            ["lam"] = 0.015,
            ["k"] = 31,
            ["beta"] = 0.5,
            ["step"] = 5.6,
            ["key_background"] = false,
            ["grid_w"] = 384,
            ["grid_h"] = 256,   // grid carries no schema default now — the config supplies it
        };
        string json = JsonSerializer.Serialize(wf.Build(v, catalog.Resolve(cfg), inputs));

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
    public void PixelQuantize_median_config_deserializes_to_the_median_contract()
    {
        // The shipped single-frame config is engine=median; it must materialize as PixelQuantizeMedianParams and emit
        // the median node with its palette — never the fp node.
        string json = BuildJson("pixel-quantize", Edit);
        Assert.Contains("\"PixelQuantize\"", json);
        Assert.DoesNotContain("\"PixelQuantizeFP\"", json);
        Assert.Contains("\"palette\":\"adaptive\"", json);
        Assert.Contains("\"virtual_resolution\":384", json);
    }

    [Fact]
    public void PixelQuantize_fp_engine_deserializes_to_the_fp_contract()
    {
        // Flip the engine to fp (the API-submission path past the UI): the same bag must now materialize as
        // PixelQuantizeFpParams and route to the feature-preserving node with its knobs, never the median node.
        Dictionary<string, object?> fp = new(StringComparer.OrdinalIgnoreCase)
        {
            [WorkflowParamKeys.Engine] = "fp",
            [WorkflowParamKeys.Thicken] = 0.75,
            [WorkflowParamKeys.Tau] = 0.6,
            [WorkflowParamKeys.Lam] = 0.015,
            [WorkflowParamKeys.K] = 31,
            [WorkflowParamKeys.Beta] = 0.5,
            [WorkflowParamKeys.Step] = 5.6,
        };
        string json = BuildJson("pixel-quantize", Edit, fp);
        Assert.Contains("\"PixelQuantizeFP\"", json);
        Assert.DoesNotContain("\"PixelQuantize\"", json);   // the median node must NOT be emitted for fp
        Assert.Contains("\"thicken\":0.75", json);
    }

    [Fact]
    public void AnimaInpaint_builds_masked_img2img_with_separate_mask()
    {
        string json = BuildJson("anima-inpaint", EditMasked);
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
    public void ComposeNegative_has_no_shared_baseline_when_config_declares_none()
    {
        // There is NO shared/implicit baseline: a model gets a negative only if its config sets one.
        // Both sides blank → an empty negative (unconditioned).
        Assert.Equal("", ComfyGraph.ComposeNegative(null, null));
        Assert.Equal("", ComfyGraph.ComposeNegative("  ", ""));
        // A user negative with no model negative stands alone.
        Assert.Equal("mutated", ComfyGraph.ComposeNegative(null, "mutated"));
    }

    [Fact]
    public void AnimaRedraw_builds_whole_image_img2img_with_no_mask()
    {
        string json = BuildJson("anima-redraw", Edit);
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
        string json = BuildJson("flux1-dev-redraw", Edit);
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
        string json = BuildJson("flux1-schnell-redraw", Edit);
        Assert.Contains("\"VAEEncode\"", json);
        Assert.Contains("\"steps\":4", json);
        // schnell is LADD-distilled with no guidance embedding: the config declares no `guidance`, so no node.
        Assert.DoesNotContain("FluxGuidance", json);
    }

    [Fact]
    public void Chroma1HdRedraw_adds_the_flow_shift_and_the_t5_padding_fix()
    {
        string json = BuildJson("chroma1-hd-redraw", Edit);
        Assert.Contains("\"VAEEncode\"", json);
        // Chroma's two required nodes, mirroring its generate graph.
        Assert.Contains("\"T5TokenizerOptions\"", json);
        Assert.Contains("\"min_padding\":0", json);
        Assert.Contains("\"ModelSamplingAuraFlow\"", json);
        Assert.Contains("\"shift\":1", json);
        // Real CFG with a working negative — NOT distilled guidance.
        Assert.Contains("\"cfg\":3.8", json);
        Assert.DoesNotContain("FluxGuidance", json);
        Assert.Contains("restricted palette, flat colors", json);   // chroma1-hd's OWN documented negative (from its config JSON), not a shared baseline
    }

    [Fact]
    public void Flux2Klein4bBaseRedraw_samples_the_non_distilled_base_at_real_cfg()
    {
        string json = BuildJson("flux2-klein-4b-base-redraw", Edit);
        // The base model, not a quality tier of the distilled one: real CFG 5 over 20 steps, so no guidance node.
        Assert.Contains("\"cfg\":5", json);
        Assert.Contains("\"steps\":20", json);
        Assert.DoesNotContain("FluxGuidance", json);
        // Flux.2 loads a single CLIP (Qwen3), not the FLUX.1 CLIP-L/T5 pair.
        Assert.Contains("\"CLIPLoader\"", json);
        Assert.DoesNotContain("DualCLIPLoader", json);
    }

    /// <summary>No redraw config may emit the text-encoder eviction node — including the one it was built for. Kept as
    /// a Theory over the whole family so a reintroduction has to be deliberate rather than arriving with a config flag
    /// nobody notices.</summary>
    [Theory]
    [InlineData("anima-redraw")]
    [InlineData("photanima-redraw")]
    [InlineData("flux1-dev-redraw")]
    [InlineData("flux2-dev-redraw")]
    [InlineData("flux2-klein-4b-redraw-hq")]
    [InlineData("chroma1-hd-redraw")]
    public void No_redraw_config_evicts_the_text_encoder(string id) => Assert.DoesNotContain("EvictCLIPFromGPU", BuildJson(id, Edit));

    [Fact]
    public void PhotAnimaRedraw_reuses_the_shared_redraw_graph_on_the_photanima_checkpoint()
    {
        string json = BuildJson("photanima-redraw", Edit);
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
        // photanima's OWN documented negative (from its config JSON), not a shared baseline.
        Assert.Contains("toon (style)", json);
    }

    [Fact]
    public void Redraw_downscales_to_each_configs_own_native_pixel_budget()
    {
        // The budget is a config param, not a constant baked into the graph. Proof: the SAME 1216x832 source (1.01 MP)
        // is over Anima's 0.92 MP bucket (→ downscaled) but under Photanima's 1.04 MP bucket (→ left alone).
        Assert.Contains("\"ImageScale\"", BuildJson("anima-redraw", Edit));
        Assert.DoesNotContain("\"ImageScale\"", BuildJson("photanima-redraw", Edit));

        // Push well past Photanima's budget and it downscales too — to /16-snapped dims, aspect preserved.
        WorkflowInputs big = new() { Positive = "make it red", SourceImageName = "src.png", SourceWidth = 2048, SourceHeight = 2048 };
        (WorkflowCatalog? catalog, WorkflowRegistry? registry) = Build();
        WorkflowConfiguration? cfg = catalog.FindConfig("photanima-redraw");
        Assert.NotNull(cfg);
        IWorkflow? wf = registry.Find(cfg.WorkflowName);
        Assert.NotNull(wf);
        string bigJson = JsonSerializer.Serialize(wf.Build(Merge(catalog, wf, cfg), catalog.Resolve(cfg), big));
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
        (WorkflowCatalog? catalog, WorkflowRegistry? registry) = Build();
        WorkflowConfiguration? cfg = catalog.FindConfig(configId);
        Assert.NotNull(cfg);
        IWorkflow? wf = registry.Find(cfg.WorkflowName);
        Assert.NotNull(wf);
        ParamSpec spec = wf.Schema.First(s => s.Key == "denoise");
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
        (WorkflowCatalog? catalog, WorkflowRegistry _) = Build();
        foreach (string? id in new[] { "anima-redraw", "photanima-redraw", "krea2-redraw" })
        {
            WorkflowConfiguration? cfg = catalog.FindConfig(id);
            Assert.NotNull(cfg);
            Assert.Equal("Redraw", cfg.EditGroup);
            Assert.Null(cfg.EffectType);   // must NOT be an effect — that would move it to the Effects tab
            Assert.DoesNotContain("redraw", (cfg.FriendlyName ?? "").ToLowerInvariant());
        }
    }

    [Fact]
    public void Upscale_configs_share_one_picker_section_and_drop_it_from_their_names()
    {
        // The "Upscale" header carries the category, so the names must not repeat it.
        (WorkflowCatalog? catalog, WorkflowRegistry _) = Build();
        foreach (string? id in new[] { "upscale-anime", "upscale-photo", "seedvr2-upscale" })
        {
            WorkflowConfiguration? cfg = catalog.FindConfig(id);
            Assert.NotNull(cfg);
            Assert.Equal("Upscale", cfg.EditGroup);
            Assert.Null(cfg.EffectType);   // must NOT be an effect — that would move it to the Effects tab
            Assert.DoesNotContain("upscale", (cfg.FriendlyName ?? "").ToLowerInvariant());
        }
    }

    [Fact]
    public void SeedVr2_builds_a_one_frame_dit_vae_upscaler_chain_with_blockswap()
    {
        string json = BuildJson("seedvr2-upscale", Edit);
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
        SeedVr2UpscaleWorkflow wf = new();
        Dictionary<string, object?> P(int scale)
        {
            return new(StringComparer.OrdinalIgnoreCase)
            {
                ["dit_model"] = "d.gguf",
                ["vae_model"] = "v.safetensors",
                ["scale"] = scale,
                ["max_resolution"] = 0,
                ["batch_size"] = 1,
                ["device"] = "cuda:0",
                ["offload_device"] = "cpu",
                ["vae_tile_size"] = 512,
                ["vae_tile_overlap"] = 64,
                ["blocks_to_swap"] = 32,
                ["attention_mode"] = "sdpa",
                ["color_correction"] = "lab",
            };
        }

        string Json(IReadOnlyDictionary<string, object?> p, WorkflowInputs i)
        {
            return JsonSerializer.Serialize(wf.Build(p, new ResolvedRequirements(), i));
        }

        // Portrait source: the SHORT edge drives it (832), not the long one.
        Assert.Contains("\"resolution\":832", Json(P(1), Edit));
        Assert.Contains("\"resolution\":2496", Json(P(3), Edit));

        // Odd short edge must snap up to even (the node's step): 833 * 1 -> 834.
        WorkflowInputs odd = new() { SourceImageName = "s.png", SourceWidth = 1000, SourceHeight = 833 };
        Assert.Contains("\"resolution\":834", Json(P(1), odd));

        // No source dims is a broken image source, not a real state — REFUSED, not upscaled to a fabricated size.
        WorkflowInputs noDims = new() { SourceImageName = "s.png" };
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => wf.Build(P(4), new ResolvedRequirements(), noDims));

        // A computed target above the node's 16384 ceiling is REFUSED, not clamped to it — a silent clamp hands back a
        // smaller upscale than the scale asked for. 5000 short edge * 4 = 20000 > 16384.
        WorkflowInputs huge = new() { SourceImageName = "s.png", SourceWidth = 9000, SourceHeight = 5000 };
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => wf.Build(P(4), new ResolvedRequirements(), huge));

        // A scale below 1 is REFUSED, not floored to 1 — a 0x upscale is the caller's mistake to see, not to have
        // silently turned into a 1x copy. Now caught by the scale param's declared [Range] at the ParamsCodec boundary
        // (before the graph is built), so the refusal is the canonical RenderValidationException naming the value.
        _ = Assert.Throws<RenderValidationException>(() => wf.Build(P(0), new ResolvedRequirements(), Edit));
    }

    [Fact]
    public void SeedVr2_folds_the_64bit_seed_into_the_nodes_uint32_range()
    {
        // The upstream node caps seed at 2^32-1, unlike ComfyUI's samplers. Passing the app's 64-bit seed straight
        // through makes ComfyUI reject the whole prompt: "Value 2709052392662243722 bigger than max of 4294967295".
        SeedVr2UpscaleWorkflow wf = new();
        Dictionary<string, object?> P(long seed)
        {
            return new(StringComparer.OrdinalIgnoreCase)
            {
                ["dit_model"] = "d.gguf",
                ["vae_model"] = "v.safetensors",
                ["scale"] = 2,
                ["max_resolution"] = 0,
                ["batch_size"] = 1,
                ["seed"] = seed,
                ["device"] = "cuda:0",
                ["offload_device"] = "cpu",
                ["vae_tile_size"] = 512,
                ["vae_tile_overlap"] = 64,
                ["blocks_to_swap"] = 32,
                ["attention_mode"] = "sdpa",
                ["color_correction"] = "lab",
            };
        }

        long SeedOf(long s)
        {
            ComfyWorkflowGraph graph = wf.Build(P(s), new ResolvedRequirements(), Edit);
            using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(graph));
            return doc.RootElement.GetProperty("32").GetProperty("inputs").GetProperty("seed").GetInt64();
        }

        // The seed that overflows the cap, and the extremes.
        foreach (long s in new[] { 2709052392662243722L, long.MaxValue, 4294967296L, 4294967295L, 1L })
        {
            long got = SeedOf(s);
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
        SeedVr2UpscaleWorkflow wf = new();
        Assert.False(wf.RequiresModel);         // the pack's own loaders fetch the DiT + VAE
        Assert.True(wf.PreservesComposition);   // a restore must never trip the no-change gate
    }

    [Fact]
    public void SeedVr2_is_gated_on_loader_reported_weights_not_on_the_custom_node_directory()
    {
        // The node pack IS linked, and is satisfied by ComfyUI having the node registered rather than by a file.
        // Eligibility is node-aware, so the link expresses the real dependency instead of being a trap: were it to
        // demand a file binding for every requirement, a node slot — which can never have one — would gate the
        // configuration off permanently.
        (WorkflowCatalog? catalog, WorkflowRegistry _) = Build();
        WorkflowConfiguration? cfg = catalog.FindConfig("seedvr2-upscale");
        Assert.NotNull(cfg);
        Assert.Contains("comfyui-seedvr2-node", cfg.Requirements.All());

        Requirement? node = catalog.FindRequirement("comfyui-seedvr2-node");
        Assert.NotNull(node);
        Assert.False(string.IsNullOrWhiteSpace(node.Node));   // met by node presence, not by a bound file

        foreach (string id in cfg.Requirements.All())
        {
            Assert.NotNull(catalog.FindRequirement(id));
        }
    }

    /// <summary>
    /// Choosing between one CLIP loader and two from a `dual` boolean cannot express three or four, and the failure is
    /// invisible until render: pixelize-sd35 declares three encoders alongside a checkpoint loader and would get the
    /// checkpoint's CLIP output — null, for a checkpoint that ships without any — and pixelize-hidream declares four
    /// and would get a single CLIPLoader fed the generic workflow's "flux" default, a type that loader does not accept.
    /// </summary>
    [Theory]
    [InlineData("pixelize-sd35", "TripleCLIPLoader")]
    [InlineData("pixelize-hidream", "QuadrupleCLIPLoader")]
    public void A_configuration_gets_the_clip_loader_its_encoder_count_calls_for(string configId, string loader)
    {
        string json = BuildJson(configId, Edit);
        Assert.Contains($"\"{loader}\"", json);
        // And never the single-encoder loader the dual-boolean selection would fall back to.
        Assert.DoesNotContain("\"CLIPLoaderGGUF\"", json);
    }

    /// <summary>
    /// A checkpoint that carries no encoders must not have its null CLIP output wired to the text encode. The
    /// declared encoders are the source when there are any.
    /// </summary>
    [Fact]
    public void A_checkpoint_without_encoders_uses_the_declared_ones_instead_of_its_null_clip()
    {
        string json = BuildJson("pixelize-sd35", Edit);
        // Node 4 is CheckpointLoaderSimple; its output 1 is the CLIP that does not exist here.
        Assert.DoesNotContain("[\"4\",1]", json);
        Assert.Contains("\"TripleCLIPLoader\"", json);
    }

    [Fact]
    public void UpscaleAnime_runs_the_2x_net_and_emits_no_resample_at_native_scale()
    {
        string json = BuildJson("upscale-anime", Edit);
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
        string json = BuildJson("upscale-photo", Edit);
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
        UpscaleWorkflow wf = new();
        Dictionary<string, object?> p = new(StringComparer.OrdinalIgnoreCase)
        {
            ["upscale_model"] = "anime-sharp-v2-rplksr-sharp-2x.safetensors",
            ["model_scale"] = 2.0,
            ["scale"] = 4,
            ["resample"] = "lanczos",
        };
        string json = JsonSerializer.Serialize(wf.Build(p, new ResolvedRequirements(), Edit));
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
        UpscaleWorkflow wf = new();
        Assert.False(wf.RequiresModel);          // no checkpoint — the SR net loads itself
        Assert.True(wf.PreservesComposition);    // an upscale must never trip the no-change gate
        // The instruction is carried by the edit path but has nowhere to go — assert it never reaches the graph.
        string json = BuildJson("upscale-photo", Edit);
        Assert.DoesNotContain("make it red", json);
    }

    [Fact]
    public void Krea2Redraw_builds_single_turbo_partial_denoise_over_the_source()
    {
        string json = BuildJson("krea2-redraw", Edit);
        // Whole-image img2img: the source RGB is VAE-encoded to the init latent and sampled with NO mask.
        Assert.Contains("\"VAEEncode\"", json);
        Assert.DoesNotContain("EmptyLatentImage", json);
        Assert.DoesNotContain("SetLatentNoiseMask", json);
        Assert.DoesNotContain("LoadImageMask", json);
        // ONE weight — the Turbo distill. The RAW base of krea2-refine must not be loaded (this is the cheap pass).
        Assert.Contains("krea2-turbo.safetensors", json);
        Assert.DoesNotContain("krea2_raw", json);
        _ = Assert.Single(System.Text.RegularExpressions.Regex.Matches(json, "\"UNETLoader\""));
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
        Krea2RedrawWorkflow wf = new();
        Assert.False(Krea2Rebalance.IsActive(1.0, Krea2RebalanceWeights.NeutralWeights));

        ComfyWorkflowGraph graph = new();
        Output<Slot.Conditioning> positive = new("13", 0);
        Assert.Equal(positive, Krea2Rebalance.Apply(graph, positive, 1.0, Krea2RebalanceWeights.NeutralWeights, "15"));
        Assert.Empty(graph.Raw);

        // ...and a single non-neutral layer weight is enough to switch it on.
        const string oneLayerHot = "1.0,1.0,1.0,1.0,1.0,1.0,1.0,1.0,2.0,1.0,1.0,1.0";
        Assert.True(Krea2Rebalance.IsActive(1.0, oneLayerHot));
        Assert.NotEqual(positive, Krea2Rebalance.Apply(graph, positive, 1.0, oneLayerHot, "15"));
        Assert.True(graph.Raw.ContainsKey("15"));
        Assert.Contains("denoise", wf.Schema.Select(s => s.Key));
    }

    [Fact]
    public void AnimaEdit_appends_the_ui_negative_to_the_config_default()
    {
        // The core of the feature: a UI negative (WorkflowInputs.Negative) is merged with the model's config default
        // negative, never replacing it. The user's tags LEAD, the Anima default follows, comma-joined.
        WorkflowInputs withNeg = new()
        {
            Positive = "make it red",
            SourceImageName = "src.png",
            MaskImageName = "mask.png",
            SourceWidth = 1216,
            SourceHeight = 832,
            Negative = "extra hands, jpeg artifacts",
        };
        string json = BuildJson("anima-inpaint", withNeg);
        Assert.Contains("extra hands, jpeg artifacts, worst quality, low quality, score_1, score_2, score_3, artist name", json);
    }

    [Fact]
    public void AnimaOutpaint_builds_pad_for_outpaint_masked_img2img()
    {
        string json = BuildJson("anima-outpaint", Edit);
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
        string json = BuildJson("flux1-fill-inpaint", EditMasked);
        // The whole point of this workflow: the mask is the MODEL's input. InpaintModelConditioning feeds the model
        // the blanked region as concat_latent_image + the mask as concat_mask. No ControlNet is involved anywhere.
        Assert.Contains("\"InpaintModelConditioning\"", json);
        // noise_mask must be TRUE: the per-step latent pinning anchors the fill's CONTENT to the surroundings.
        // Full-frame sampling (noise_mask=false, the diffusers-reference shape) would let the outside pixels witness
        // the fill's exposure drift for the color fit, but measures strictly worse: Fill freewheels without the anchor
        // (a moon hallucinated into an empty-prompt sky fill; −27/−89 luminance vs −6 pinned). The drift is attacked
        // at its source instead, not by unpinning.
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
        (WorkflowCatalog? catalog, WorkflowRegistry? registry) = Build();
        WorkflowConfiguration? cfg = catalog.FindConfig("flux1-fill-outpaint");
        Assert.NotNull(cfg);
        IWorkflow? wf = registry.Find(cfg.WorkflowName);
        Assert.NotNull(wf);
        Dictionary<string, object?> v = new(StringComparer.OrdinalIgnoreCase);
        foreach (ParamSpec s in wf.Schema)
        {
            if (s.Default is not null)
            {
                v[s.Key] = s.Default;
            }
        }

        foreach (KeyValuePair<string, ConfigParam> kv in cfg.Params)
        {
            v[kv.Key] = kv.Value.Value;
        }
        // Mirrors ComfyClient.MergeParamsDict: this machine's settings sit over the shipped configuration.
        foreach (KeyValuePair<string, JsonElement> kv in catalog.ParamOverridesFor(cfg.Id))
        {
            v[kv.Key] = kv.Value;
        }

        v["pad_left"] = 256;
        v["pad_right"] = 256;
        WorkflowInputs inputs = new() { Positive = "wider", SourceImageName = "src.png", SourceWidth = 1024, SourceHeight = 1024 };
        string json = JsonSerializer.Serialize(wf.Build(v, catalog.Resolve(cfg), inputs));

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
        string json = BuildJson("qwen-image-inpaint", EditMasked);
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
        string json = BuildJson("qwen-image-outpaint", Edit);
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
        // FeatherMask ramps the mask in from the CANVAS EDGES rather than from the mask's own boundary. On an
        // outpaint the fill region touches those edges, so the mask would fall toward 0 exactly over
        // ImagePadForOutpaint's 0.5-GREY fill — the sampler would leave the grey half-denoised AND
        // ImageCompositeMasked would blend it in, producing a grey frame (measured: RGB 127 at x=0).
        (WorkflowCatalog? catalog, WorkflowRegistry? registry) = Build();
        WorkflowConfiguration? cfg = catalog.FindConfig("qwen-image-outpaint");
        Assert.NotNull(cfg);
        IWorkflow? wf = registry.Find(cfg.WorkflowName);
        Assert.NotNull(wf);
        Dictionary<string, object?> v = new(StringComparer.OrdinalIgnoreCase);
        foreach (ParamSpec s in wf.Schema)
        {
            if (s.Default is not null)
            {
                v[s.Key] = s.Default;
            }
        }

        foreach (KeyValuePair<string, ConfigParam> kv in cfg.Params)
        {
            v[kv.Key] = kv.Value.Value;
        }
        // Mirrors ComfyClient.MergeParamsDict: this machine's settings sit over the shipped configuration.
        foreach (KeyValuePair<string, JsonElement> kv in catalog.ParamOverridesFor(cfg.Id))
        {
            v[kv.Key] = kv.Value;
        }

        v["pad_left"] = 256;
        v["pad_right"] = 256;
        WorkflowInputs inputs = new() { Positive = "wider", SourceImageName = "src.png", SourceWidth = 1024, SourceHeight = 1024 };
        ComfyWorkflowGraph graph = wf.Build(v, catalog.Resolve(cfg), inputs);
        string json = JsonSerializer.Serialize(graph);
        using JsonDocument gdoc = JsonDocument.Parse(json);
        string? ClassType(string id)
        {
            return gdoc.RootElement.GetProperty(id).GetProperty("class_type").GetString();
        }
        // A node's inputs, serialized by its RUNTIME record type (graph.Raw[id] is statically ComfyNode).
        string Node(string id)
        {
            return JsonSerializer.Serialize(graph.Raw[id], graph.Raw[id].GetType());
        }

        Assert.DoesNotContain("FeatherMask", json);              // the node that would cause it
        Assert.Contains("\"ImageBlur\"", json);                  // blur the mask's own boundary instead

        // The pad node must not ALSO feather, or the softening stacks into a wide partial-denoise band (mushy seam).
        Assert.Contains("\"feathering\":0", Node("20"));

        // Every consumer of the CANVAS takes the pre-filled scene-tone one (node 23), never ImagePadForOutpaint's
        // grey canvas (node 20 output 0, kept only for its mask): the VAE encode, the ControlNet apply's control
        // image, and the composite's destination. Grey under any soft mask edge = the halo.
        Assert.Contains("\"23\"", Node("12"));   // VAEEncode
        Assert.Contains("\"23\"", Node("108"));  // ControlNet control image
        Assert.Contains("\"23\"", Node("126")); // composite destination
        // The scaffold: stretch to the padded size, blur, paste the original back at its pad offset.
        Assert.Equal("ImageScale", ClassType("21"));
        Assert.Equal("ImageBlur", ClassType("22"));
        Assert.Contains("\"x\":256", Node("23")); // original pasted back at its pad offset

        // Outpaint's ramp: sigma 8 (not the template's 1) keeps SetLatentNoiseMask's latent-space blend from landing
        // inside a single 8px cell and decoding as a hard 1px line along the frame-spanning join (measured: a lone
        // ~63 gradient column with a near-binary mask). Grow 16 = 2σ places the 50% blend point 16px inside the
        // original with the descent starting right at the pad boundary.
        Assert.Contains("\"expand\":16", Node("30"));
        string blurNode = Node("33");
        Assert.Contains("\"blur_radius\":31", blurNode);
        Assert.Contains("\"sigma\":8", blurNode);

        // The blurred mask is clamped back to a hard 1 over the raw pad region (MaskComposite "add" against the raw
        // ImagePadForOutpaint mask). ANY deficit below 1 over the grey pad leaks 0.5-grey into that column through
        // the latent re-injection and the composite: measured seam columns of 51/34/10 as the unclamped gaussian's
        // boundary value went 0.933/0.977/0.9987, seam-free only with a hard 1 there.
        string clamp = Node("35");
        Assert.Equal("MaskComposite", ClassType("35"));
        Assert.Contains("\"add\"", clamp);
        Assert.Contains("\"20\"", clamp);                                   // clamped against the RAW pad mask

        // Every mask consumer takes the SAME softened+clamped mask: the ControlNet apply, SetLatentNoiseMask and the
        // composite. Splitting any of them off fails: a raw mask to the ControlNet dirties the seam; a raw mask to the
        // composite hard-switches on pixels the ControlNet is blind to, so the extension doesn't line up.
        Assert.Contains("\"35\"", Node("108"));  // ControlNet gets the SOFTENED mask
        Assert.Contains("\"35\"", Node("31"));   // latent noise mask: same softened mask
        Assert.Contains("\"35\"", Node("126")); // composite: same softened mask

        // The sampler goes through SetLatentNoiseMask — the exposure anchor. Without it (template outpaint branch,
        // VAEEncode straight in) the ControlNet anchors structure but not tone, and the measured side panels come
        // out ~15 RGB brighter than the frame they extend (the "color balance" halo).
        Assert.Contains("\"31\"", Node("3"));
    }

    [Fact]
    public void QwenImageInpaint_leaves_a_source_under_the_ceiling_at_native_resolution()
    {
        // 1216x832 is under the 1536 ceiling — nothing may be resized. Guards the standing rule that we never
        // silently change a user's resolution, and the fact that Comfy's own node would have UPSCALED this to 1536.
        string json = BuildJson("qwen-image-inpaint", EditMasked);
        // ImageScale is the only node that resizes; MaskToImage is NOT a proxy for scaling — the mask blur
        // round-trips through IMAGE too, so it is present either way.
        Assert.DoesNotContain("\"ImageScale\"", json);
        Assert.DoesNotContain("ImageScaleToMaxDimension", json);
    }

    [Fact]
    public void QwenImageInpaint_scales_canvas_and_mask_together_when_over_the_ceiling()
    {
        WorkflowInputs big = new()
        {
            Positive = "make it red",
            SourceImageName = "src.png",
            MaskImageName = "mask.png",
            SourceWidth = 4000,
            SourceHeight = 3000,
        };
        string json = BuildJson("qwen-image-inpaint", big);
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
        (WorkflowCatalog? catalog, WorkflowRegistry? registry) = Build();
        WorkflowConfiguration? cfg = catalog.FindConfig("qwen-image-outpaint");
        Assert.NotNull(cfg);
        IWorkflow? wf = registry.Find(cfg.WorkflowName);
        Assert.NotNull(wf);
        Dictionary<string, object?> v = new(StringComparer.OrdinalIgnoreCase);
        foreach (ParamSpec s in wf.Schema)
        {
            if (s.Default is not null)
            {
                v[s.Key] = s.Default;
            }
        }

        foreach (KeyValuePair<string, ConfigParam> kv in cfg.Params)
        {
            v[kv.Key] = kv.Value.Value;
        }

        v["pad_left"] = 600;
        v["pad_right"] = 600;
        WorkflowInputs inputs = new() { Positive = "wider", SourceImageName = "src.png", SourceWidth = 1216, SourceHeight = 832 };
        string json = JsonSerializer.Serialize(wf.Build(v, catalog.Resolve(cfg), inputs));
        // 1216 + 600 + 600 = 2416 wide > 1536, so it scales even though the SOURCE alone was under the ceiling.
        Assert.Contains("\"ImageScale\"", json);
        Assert.Contains("\"width\":1536", json);
    }

    [Fact]
    public void HunyuanImage21_builds_with_sd3_sampling_and_image_latent()
    {
        string json = BuildJson("hunyuanimage21", Gen);
        Assert.Contains("\"EmptyHunyuanImageLatent\"", json);
        Assert.Contains("\"ModelSamplingSD3\"", json);
        Assert.Contains("hunyuanimage21-distilled.safetensors", json);
        Assert.Contains("hunyuan-image-2-1-vae.safetensors", json);
        Assert.Contains("\"type\":\"hunyuan_image\"", json);
    }

    [Fact]
    public void HunyuanVideo15_i2v_sr_appends_super_resolution_pass()
    {
        string json = BuildJson("hunyuanvideo15-i2v-sr", Edit);
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
        string json = BuildJson("hunyuanvideo15-i2v", Edit);
        Assert.DoesNotContain("HunyuanVideo15SuperResolution", json);
        Assert.DoesNotContain("LatentUpscaleModelLoader", json);
    }

    [Fact]
    public void PonyV6_builds_a_checkpoint_clipskip_graph()
    {
        string json = BuildJson("pony-v6", Gen);
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
        string json = BuildJson("z-image-turbo", Gen);
        Assert.Contains("\"UNETLoader\"", json);
        Assert.Contains("\"EmptySD3LatentImage\"", json);
        Assert.Contains("\"steps\":8", json);
    }

    [Fact]
    public void Sd35Medium_builds_an_sd3_latent_graph()
    {
        string json = BuildJson("sd35-medium", Gen);
        Assert.Contains("\"CheckpointLoaderSimple\"", json);
        Assert.Contains("\"EmptySD3LatentImage\"", json);
    }

    [Fact]
    public void Flux1Dev_applies_flux_guidance()
    {
        string json = BuildJson("flux1-dev", Gen);
        Assert.Contains("\"UNETLoader\"", json);
        Assert.Contains("\"FluxGuidance\"", json);
        Assert.Contains("\"guidance\":3.5", json);
    }

    [Fact]
    public void FluxKontext_builds_a_reference_latent_edit_graph()
    {
        string json = BuildJson("flux1-kontext", Edit);
        Assert.Contains("\"ReferenceLatent\"", json);
        Assert.Contains("\"FluxGuidance\"", json);
        Assert.Contains("\"SaveImage\"", json);
    }

    [Fact]
    public void WanI2V_builds_a_video_graph()
    {
        string json = BuildJson("wan22-ti2v-5b", Edit);
        Assert.Contains("\"Wan22ImageToVideoLatent\"", json);
        Assert.Contains("\"SaveAnimatedWEBP\"", json);
    }

    [Fact]
    public void Ltxv_builds_a_video_graph()
    {
        string json = BuildJson("ltxv-i2v", Edit);
        Assert.Contains("\"LTXVImgToVideo\"", json);
        Assert.Contains("\"SaveAnimatedWEBP\"", json);
    }

    [Fact]
    public void QwenImageEdit_builds_a_qwen_edit_graph()
    {
        string json = BuildJson("qwen-image-edit", Edit);
        Assert.Contains("\"TextEncodeQwenImageEditPlus\"", json);
        Assert.Contains("\"SaveImage\"", json);
    }

    [Fact]
    public void QwenImageEdit_without_a_mask_is_unchanged()
    {
        // Default path (all mask_*_pct=0 from the schema defaults): the sampler reads the source latent directly and
        // SaveImage consumes the raw VAEDecode — no reframe nodes at all.
        string json = BuildJson("qwen-image-edit", Edit);
        Assert.DoesNotContain("\"ImageCompositeMasked\"", json);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        Assert.Equal("14", root.GetProperty("3").GetProperty("inputs").GetProperty("latent_image")[0].GetString());
        Assert.Equal("8", root.GetProperty("9").GetProperty("inputs").GetProperty("images")[0].GetString());
    }

    [Theory]                                                     // L    R    T    B    → rect x, y, w, h  (src 1216×832)
    [InlineData(0, 0, 50, 0, 0, 416, 1216, 416)]           // lower half
    [InlineData(0, 0, 34, 0, 0, 282, 1216, 550)]           // lower ⅔ — the crouch case
    [InlineData(25, 25, 0, 0, 304, 0, 608, 832)]           // centre column
    [InlineData(0, 0, 0, 50, 0, 0, 1216, 416)]           // upper half
    public void QwenImageEdit_mask_pct_samples_the_rect_and_composites_it_back(int l, int r, int t, int b, int x, int y, int w, int h)
    {
        // Each mask_*_pct blocks dim·pct/100 px on that side; what's left is the drawing rect. The sampler runs on a
        // latent shaped like the RECT (stride-aligned), and the decode is pasted back onto a white full-size canvas at
        // the rect's offset — so the model fills a correctly-shaped frame rather than being clipped by a mask.
        (WorkflowCatalog? catalog, WorkflowRegistry? registry) = Build();
        WorkflowConfiguration? cfg = catalog.FindConfig("qwen-image-edit");
        Assert.NotNull(cfg);
        IWorkflow? wf = registry.Find(cfg.WorkflowName);
        Assert.NotNull(wf);
        Dictionary<string, object?> v = new(StringComparer.OrdinalIgnoreCase);
        foreach (ParamSpec s in wf.Schema)
        {
            if (s.Default is not null)
            {
                v[s.Key] = s.Default;
            }
        }

        foreach (KeyValuePair<string, ConfigParam> kv in cfg.Params)
        {
            v[kv.Key] = kv.Value.Value;
        }
        // Mirrors ComfyClient.MergeParamsDict: this machine's settings sit over the shipped configuration.
        foreach (KeyValuePair<string, JsonElement> kv in catalog.ParamOverridesFor(cfg.Id))
        {
            v[kv.Key] = kv.Value;
        }

        v["mask_left_pct"] = l;
        v["mask_right_pct"] = r;
        v["mask_top_pct"] = t;
        v["mask_bottom_pct"] = b;
        ComfyWorkflowGraph graph = wf.Build(v, catalog.Resolve(cfg), Edit);   // Edit = 1216×832

        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(graph));
        JsonElement root = doc.RootElement;

        // The sampled canvas is the rect, rounded DOWN to the 16px latent stride.
        JsonElement seed = root.GetProperty("80");
        Assert.Equal("EmptyImage", seed.GetProperty("class_type").GetString());
        Assert.Equal(w - (w % 16), seed.GetProperty("inputs").GetProperty("width").GetInt32());
        Assert.Equal(h - (h % 16), seed.GetProperty("inputs").GetProperty("height").GetInt32());
        Assert.Equal("81", root.GetProperty("3").GetProperty("inputs").GetProperty("latent_image")[0].GetString());

        // Stride rounding undone, then pasted at the rect offset onto a full-size white canvas.
        JsonElement back = root.GetProperty("82");
        Assert.Equal(w, back.GetProperty("inputs").GetProperty("width").GetInt32());
        Assert.Equal(h, back.GetProperty("inputs").GetProperty("height").GetInt32());
        JsonElement canvas = root.GetProperty("83");
        Assert.Equal(1216, canvas.GetProperty("inputs").GetProperty("width").GetInt32());
        Assert.Equal(832, canvas.GetProperty("inputs").GetProperty("height").GetInt32());
        Assert.Equal(16777215, canvas.GetProperty("inputs").GetProperty("color").GetInt32());   // white margin
        JsonElement comp = root.GetProperty("84");
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
    public void QwenImageEdit_rejects_a_degenerate_mask(int l, int r, int t, int b)
    {
        Dictionary<string, object?> v = QwenEditMask(out IWorkflow wf, out WorkflowConfiguration cfg, out WorkflowCatalog catalog);
        v["mask_left_pct"] = l;
        v["mask_right_pct"] = r;
        v["mask_top_pct"] = t;
        v["mask_bottom_pct"] = b;
        // In-range percentages whose geometry collapses to no region: the workflow's own degenerate-mask guard refuses.
        _ = Assert.ThrowsAny<ArgumentException>(() => wf.Build(v, catalog.Resolve(cfg), Edit));
    }

    [Fact]
    public void QwenImageEdit_rejects_an_out_of_range_mask_margin()
    {
        Dictionary<string, object?> v = QwenEditMask(out IWorkflow wf, out WorkflowConfiguration cfg, out WorkflowCatalog catalog);
        v["mask_top_pct"] = 120;   // past the margin's declared [Range] — refused at the ParamsCodec boundary
        _ = Assert.Throws<RenderValidationException>(() => wf.Build(v, catalog.Resolve(cfg), Edit));
    }

    /// <summary>The qwen-image-edit merged param bag (schema defaults + config + machine overrides), mirroring
    /// <c>ComfyClient.MergeParamsDict</c>, for the mask-margin refusal tests to overlay a bad margin onto.</summary>
    private static Dictionary<string, object?> QwenEditMask(out IWorkflow wf, out WorkflowConfiguration cfg, out WorkflowCatalog catalog)
    {
        (WorkflowCatalog? cat, WorkflowRegistry? registry) = Build();
        catalog = cat;
        WorkflowConfiguration? found = catalog.FindConfig("qwen-image-edit");
        Assert.NotNull(found);
        cfg = found;
        IWorkflow? found2 = registry.Find(cfg.WorkflowName);
        Assert.NotNull(found2);
        wf = found2;
        Dictionary<string, object?> v = new(StringComparer.OrdinalIgnoreCase);
        foreach (ParamSpec s in wf.Schema)
        {
            if (s.Default is not null)
            {
                v[s.Key] = s.Default;
            }
        }

        foreach (KeyValuePair<string, ConfigParam> kv in cfg.Params)
        {
            v[kv.Key] = kv.Value.Value;
        }

        foreach (KeyValuePair<string, JsonElement> kv in catalog.ParamOverridesFor(cfg.Id))
        {
            v[kv.Key] = kv.Value;
        }

        return v;
    }

    [Fact]
    public void AnimateDiffI2V_builds_sparsectrl_ipadapter_graph()
    {
        string ltng = BuildJson("animatediff-lightning-i2v", Edit);
        Assert.Contains("IPAdapterUnifiedLoader", ltng);
        Assert.Contains("\"IPAdapter\"", ltng);               // the apply node (distinct from the loader)
        Assert.Contains("ACN_SparseCtrlLoaderAdvanced", ltng);
        Assert.Contains("ControlNetApplyAdvanced", ltng);
        Assert.Contains("\"SaveAnimatedWEBP\"", ltng);
        Assert.DoesNotContain("LoraLoaderModelOnly", ltng);   // Lightning has no LCM LoRA

        string lcm = BuildJson("animatelcm-i2v", Edit);
        Assert.Contains("LoraLoaderModelOnly", lcm);          // AnimateLCM applies the LCM LoRA
        Assert.Contains("\"sampler_name\":\"lcm\"", lcm);     // and samples with lcm
    }

    [Fact]
    public void HiDream_builds_a_quad_clip_sd3_graph()
    {
        string json = BuildJson("hidream-full", Gen);
        Assert.Contains("\"QuadrupleCLIPLoader\"", json);
        Assert.Contains("\"ModelSamplingSD3\"", json);
        Assert.Contains("\"EmptySD3LatentImage\"", json);
        Assert.Contains("\"shift\":3", json);              // Full flow-shift
        Assert.Contains("\"clip_name4\"", json);           // the llama encoder slot
    }

    [Fact]
    public void HiDreamDev_uses_its_own_shift_and_sampler()
    {
        string json = BuildJson("hidream-dev", Gen);
        Assert.Contains("\"shift\":6", json);              // Dev flow-shift (per-config, flows through MergeParams)
        Assert.Contains("\"sampler_name\":\"lcm\"", json);
        Assert.Contains("\"steps\":28", json);
    }

    [Fact]
    public void Sd35LargeTriple_builds_a_checkpoint_triple_clip_graph()
    {
        string json = BuildJson("sd35-large-bf16", Gen);
        Assert.Contains("\"CheckpointLoaderSimple\"", json);
        Assert.Contains("\"TripleCLIPLoader\"", json);
        Assert.Contains("\"EmptySD3LatentImage\"", json);
        Assert.DoesNotContain("\"ModelSamplingSD3\"", json);   // official sd3 t2i wires checkpoint MODEL straight in
    }

    [Fact]
    public void Chroma_builds_a_t5_only_auraflow_graph()
    {
        string json = BuildJson("chroma1-hd", Gen);
        Assert.Contains("\"CLIPLoader\"", json);
        Assert.Contains("\"T5TokenizerOptions\"", json);
        Assert.Contains("\"ModelSamplingAuraFlow\"", json);
        Assert.Contains("\"EmptySD3LatentImage\"", json);
    }

    [Fact]
    public void QwenImageBase_builds_on_the_generic_pipeline()
    {
        string json = BuildJson("qwen-image", Gen);
        Assert.Contains("\"UNETLoader\"", json);
        Assert.Contains("\"ModelSamplingAuraFlow\"", json);   // auraflow 3.1
        Assert.Contains("\"EmptySD3LatentImage\"", json);
    }

    [Fact]
    public void Flux2Dev_applies_flux_guidance()
    {
        string json = BuildJson("flux2-dev", Gen);
        Assert.Contains("\"UNETLoader\"", json);
        Assert.Contains("\"FluxGuidance\"", json);
        Assert.Contains("\"EmptyFlux2LatentImage\"", json);
    }

    /// <summary>
    /// No configuration is another one wearing a different name. Precision and its VRAM floor are the user's choice at
    /// bind time, not a reason for a separate workflow, so a pair that agrees on graph, requirements and parameters is
    /// a duplicate, not a variant.
    /// </summary>
    [Fact]
    public void No_configuration_is_another_one_under_a_different_name()
    {
        (WorkflowCatalog? catalog, WorkflowRegistry _) = Build();

        List<string> duplicates = [.. catalog.AllConfigs()
            .GroupBy(c => string.Join("|",
                c.WorkflowName,
                string.Join(",", c.Requirements.All().OrderBy(x => x, StringComparer.Ordinal)),
                string.Join(",", c.Params.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                                         .Select(kv => kv.Key + "=" + kv.Value.Value))))
            .Where(g => g.Count() > 1)
            .Select(g => string.Join(" == ", g.Select(c => c.Id)))];

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
        (WorkflowCatalog? catalog, WorkflowRegistry? registry) = Build();
        catalog.SetBindings(catalog.AllRequirements().ToDictionary(r => r.Id, r => r.Id + extension));
        WorkflowConfiguration? cfg = catalog.FindConfig(configId);
        Assert.NotNull(cfg);
        IWorkflow? wf = registry.Find(cfg.WorkflowName);
        Assert.NotNull(wf);
        return JsonSerializer.Serialize(wf.Build(Merge(catalog, wf, cfg), catalog.Resolve(cfg), Gen));
    }

    /// <summary>
    /// A machine setting reaches the graph. A stored override that nothing reads would accept every size or step change
    /// a user makes and silently ignore it, so this asserts the whole path: stored value, through the merge, into the
    /// emitted node.
    /// </summary>
    [Fact]
    public void A_machine_setting_overrides_the_shipped_one()
    {
        (WorkflowCatalog? catalog, WorkflowRegistry? registry) = Build();
        WorkflowConfiguration? cfg = catalog.FindConfig("flux1-dev");
        Assert.NotNull(cfg);
        IWorkflow? wf = registry.Find(cfg.WorkflowName);
        Assert.NotNull(wf);

        string shipped = JsonSerializer.Serialize(wf.Build(Merge(catalog, wf, cfg), catalog.Resolve(cfg), Gen));
        Assert.Contains("\"steps\":28", shipped);

        catalog.SetParamOverrides(new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["flux1-dev"] = new Dictionary<string, string> { ["param.steps"] = "12" },
        });

        string overridden = JsonSerializer.Serialize(wf.Build(Merge(catalog, wf, cfg), catalog.Resolve(cfg), Gen));
        Assert.Contains("\"steps\":12", overridden);
        Assert.DoesNotContain("\"steps\":28", overridden);
    }

    [Fact]
    public void WanA14b_i2v_builds_a_two_expert_moe_video_graph()
    {
        string json = BuildJson("wan22-i2v-a14b", Edit);
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
        WorkflowInputs inputs = new() { Positive = "make it red", SourceImageName = "src.png", EndImageName = "forgemcp_edit_last.png", SourceWidth = 1216, SourceHeight = 832 };
        string json = BuildJson("wan22-i2v-a14b", inputs);
        Assert.Contains("\"WanFirstLastFrameToVideo\"", json);
        Assert.DoesNotContain("\"WanImageToVideo\"", json);
        Assert.Contains("forgemcp_edit_last.png", json);
        Assert.Contains("\"end_image\"", json);

        // The end frame (no padding) passes through the SAME ImageScaleToTotalPixels as the start frame (node 11),
        // to the same pixel budget/rounding — so a loop (end == start) reaches the node at identical dims and holds
        // still instead of the node cropping a raw end frame. end_image is wired to that scale node (76), not raw.
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement end = doc.RootElement.GetProperty("14").GetProperty("inputs").GetProperty("end_image");
        Assert.Equal("76", end[0].GetString());
        JsonElement endScale = doc.RootElement.GetProperty("76");
        Assert.Equal("ImageScaleToTotalPixels", endScale.GetProperty("class_type").GetString());
        Assert.Equal("12", endScale.GetProperty("inputs").GetProperty("image")[0].GetString());
        JsonElement startScale = doc.RootElement.GetProperty("11").GetProperty("inputs");
        Assert.Equal(startScale.GetProperty("megapixels").GetDouble(), endScale.GetProperty("inputs").GetProperty("megapixels").GetDouble());
        Assert.Equal(startScale.GetProperty("resolution_steps").GetInt32(), endScale.GetProperty("inputs").GetProperty("resolution_steps").GetInt32());
    }

    [Theory]
    //          L,   R,   T,   B  → canvasW, canvasH, offsetX, offsetY   (source Edit = 1216×832)
    [InlineData(200, 0, 0, 0, 3648, 832, 2432, 0)]     // left:   whitespace left,   char flush right
    [InlineData(100, 100, 0, 0, 3648, 832, 1216, 0)]     // center: whitespace split,  char centered
    [InlineData(0, 200, 0, 0, 3648, 832, 0, 0)]     // right:  whitespace right,  char flush left
    [InlineData(0, 0, 100, 0, 1216, 1664, 0, 832)]   // top:    whitespace top,    char flush bottom
    [InlineData(0, 0, 0, 100, 1216, 1664, 0, 0)]     // bottom: whitespace bottom, char flush top
    public void WanA14b_i2v_pad_pct_builds_the_expected_canvas(int l, int r, int t, int b, int w, int h, int x, int y)
    {
        // Each pad_*_pct adds dim·pct/100 px on that side; the source is composited onto the enlarged white canvas at
        // the top-left additions, and THAT feeds the total-pixel scale instead of the raw LoadImage.
        (WorkflowCatalog? catalog, WorkflowRegistry? registry) = Build();
        WorkflowConfiguration? cfg = catalog.FindConfig("wan22-i2v-a14b");
        Assert.NotNull(cfg);
        IWorkflow? wf = registry.Find(cfg.WorkflowName);
        Assert.NotNull(wf);
        Dictionary<string, object?> v = new(StringComparer.OrdinalIgnoreCase);
        foreach (ParamSpec s in wf.Schema)
        {
            if (s.Default is not null)
            {
                v[s.Key] = s.Default;
            }
        }

        foreach (KeyValuePair<string, ConfigParam> kv in cfg.Params)
        {
            v[kv.Key] = kv.Value.Value;
        }
        // Mirrors ComfyClient.MergeParamsDict: this machine's settings sit over the shipped configuration.
        foreach (KeyValuePair<string, JsonElement> kv in catalog.ParamOverridesFor(cfg.Id))
        {
            v[kv.Key] = kv.Value;
        }

        v["pad_left_pct"] = l;
        v["pad_right_pct"] = r;
        v["pad_top_pct"] = t;
        v["pad_bottom_pct"] = b;
        ComfyWorkflowGraph graph = wf.Build(v, catalog.Resolve(cfg), Edit);   // Edit = 1216×832

        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(graph));
        JsonElement root = doc.RootElement;
        JsonElement canvas = root.GetProperty("71");
        Assert.Equal("EmptyImage", canvas.GetProperty("class_type").GetString());
        Assert.Equal(w, canvas.GetProperty("inputs").GetProperty("width").GetInt32());
        Assert.Equal(h, canvas.GetProperty("inputs").GetProperty("height").GetInt32());
        Assert.Equal(16777215, canvas.GetProperty("inputs").GetProperty("color").GetInt32());   // white
        JsonElement comp = root.GetProperty("73");
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
        string json = BuildJson("wan22-i2v-a14b", Edit);
        Assert.DoesNotContain("\"EmptyImage\"", json);
        Assert.DoesNotContain("\"ImageCompositeMasked\"", json);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement scaleImg = doc.RootElement.GetProperty("11").GetProperty("inputs").GetProperty("image");
        Assert.Equal("10", scaleImg[0].GetString());   // scale consumes LoadImage directly
    }

    [Fact]
    public void WanA14b_t2v_builds_an_empty_latent_moe_graph()
    {
        string json = BuildJson("wan22-t2v-a14b", Gen);
        Assert.Contains("\"EmptyHunyuanLatentVideo\"", json);
        Assert.Contains("\"KSamplerAdvanced\"", json);
        Assert.Contains("wan2-2-t2v-low-noise-14b.safetensors", json);   // low-noise expert via unet_low (int8 ConvRot)
        Assert.Contains("\"UNETLoader\"", json);                            // int8 .safetensors -> UNETLoader, not GGUF
        Assert.DoesNotContain("\"WanImageToVideo\"", json);   // t2v has no source image
    }

    [Fact]
    public void HunyuanVideo15_t2v_builds_a_cfgguider_video_graph()
    {
        string json = BuildJson("hunyuanvideo15-t2v", Gen);
        Assert.Contains("\"EmptyHunyuanVideo15Latent\"", json);
        Assert.Contains("\"hunyuan_video_15\"", json);
        Assert.Contains("\"CFGGuider\"", json);
        Assert.Contains("\"SamplerCustomAdvanced\"", json);
        Assert.Contains("\"SaveAnimatedWEBP\"", json);
    }

    [Fact]
    public void HunyuanVideo_t2v_builds_a_fluxguidance_basicguider_graph()
    {
        string json = BuildJson("hunyuanvideo-t2v", Gen);
        Assert.Contains("\"UNETLoader\"", json);
        Assert.Contains("\"EmptyHunyuanLatentVideo\"", json);
        Assert.Contains("\"FluxGuidance\"", json);
        Assert.Contains("\"BasicGuider\"", json);
        Assert.Contains("\"VAEDecodeTiled\"", json);
    }

    [Fact]
    public void Ltx23_i2v_reuses_the_ltx2_graph_with_23_files()
    {
        string json = BuildJson("ltx23-i2v", Edit);
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
        (WorkflowCatalog? catalog, WorkflowRegistry? registry) = Build();
        WorkflowConfiguration? cfg = catalog.FindConfig("krea2-turbo");
        Assert.NotNull(cfg);
        IWorkflow? wf = registry.Find(cfg.WorkflowName);
        Assert.NotNull(wf);
        Dictionary<string, object?> v = new(StringComparer.OrdinalIgnoreCase);
        foreach (ParamSpec s in wf.Schema)
        {
            if (s.Default is not null)
            {
                v[s.Key] = s.Default;
            }
        }

        foreach (KeyValuePair<string, ConfigParam> kv in cfg.Params)
        {
            v[kv.Key] = kv.Value.Value;
        }
        // Mirrors ComfyClient.MergeParamsDict: this machine's settings sit over the shipped configuration.
        foreach (KeyValuePair<string, JsonElement> kv in catalog.ParamOverridesFor(cfg.Id))
        {
            v[kv.Key] = kv.Value;
        }

        v["rebalance_multiplier"] = 1.0;
        v["per_layer_weights"] = "1.0,1.0,1.0,1.0,1.0,1.0,1.0,1.0,1.0,1.0,1.0,1.0";
        string json = JsonSerializer.Serialize(wf.Build(v, catalog.Resolve(cfg), Gen));
        Assert.Contains("\"type\":\"krea2\"", json);
        Assert.Contains("krea2-turbo.safetensors", json);
        Assert.DoesNotContain("ConditioningKrea2Rebalance", json);
        using JsonDocument doc = JsonDocument.Parse(json);
        // The sampler reads the positive text-encode (node 6) directly.
        Assert.Equal("6", doc.RootElement.GetProperty("3").GetProperty("inputs").GetProperty("positive")[0].GetString());
    }

    [Fact]
    public void Krea2_turbo_config_bakes_the_uncensor_rebalance_by_default()
    {
        // The krea2-turbo config bakes multiplier 4.0 + the uncensor weights (exposed:false), so a plain build with
        // merged config defaults (no overrides) splices the rebalance node between the encode and the sampler.
        string json = BuildJson("krea2-turbo", Gen);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement node = doc.RootElement.GetProperty("13");
        Assert.Equal("ConditioningKrea2Rebalance", node.GetProperty("class_type").GetString());
        Assert.Equal(4.0, node.GetProperty("inputs").GetProperty("multiplier").GetDouble());
        Assert.Equal("1.0,1.0,1.0,1.0,1.0,1.0,1.0,2.5,5.0,1.1,4.0,1.0",
            node.GetProperty("inputs").GetProperty("per_layer_weights").GetString());
        Assert.Equal("13", doc.RootElement.GetProperty("3").GetProperty("inputs").GetProperty("positive")[0].GetString());
    }

    [Fact]
    public void Krea2_with_rebalance_splices_the_node_between_encode_and_sampler()
    {
        (WorkflowCatalog? catalog, WorkflowRegistry? registry) = Build();
        WorkflowConfiguration? cfg = catalog.FindConfig("krea2-turbo");
        Assert.NotNull(cfg);
        IWorkflow? wf = registry.Find(cfg.WorkflowName);
        Assert.NotNull(wf);
        Dictionary<string, object?> v = new(StringComparer.OrdinalIgnoreCase);
        foreach (ParamSpec s in wf.Schema)
        {
            if (s.Default is not null)
            {
                v[s.Key] = s.Default;
            }
        }

        foreach (KeyValuePair<string, ConfigParam> kv in cfg.Params)
        {
            v[kv.Key] = kv.Value.Value;
        }
        // Mirrors ComfyClient.MergeParamsDict: this machine's settings sit over the shipped configuration.
        foreach (KeyValuePair<string, JsonElement> kv in catalog.ParamOverridesFor(cfg.Id))
        {
            v[kv.Key] = kv.Value;
        }

        v["rebalance_multiplier"] = 2.0;
        v["per_layer_weights"] = "1.0,1.0,1.0,1.0,1.0,1.0,1.0,2.5,5.0,1.1,4.0,1.0";
        ComfyWorkflowGraph graph = wf.Build(v, catalog.Resolve(cfg), Gen);

        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(graph));
        JsonElement root = doc.RootElement;
        JsonElement node = root.GetProperty("13");
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
        (WorkflowCatalog? catalog, WorkflowRegistry? registry) = Build();
        WorkflowConfiguration? cfg = catalog.FindConfig("krea2");
        Assert.NotNull(cfg);
        IWorkflow? wf = registry.Find(cfg.WorkflowName);
        Assert.NotNull(wf);
        Dictionary<string, object?> v = new(StringComparer.OrdinalIgnoreCase);
        foreach (ParamSpec s in wf.Schema)
        {
            if (s.Default is not null)
            {
                v[s.Key] = s.Default;
            }
        }

        foreach (KeyValuePair<string, ConfigParam> kv in cfg.Params)
        {
            v[kv.Key] = kv.Value.Value;
        }
        // Mirrors ComfyClient.MergeParamsDict: this machine's settings sit over the shipped configuration.
        foreach (KeyValuePair<string, JsonElement> kv in catalog.ParamOverridesFor(cfg.Id))
        {
            v[kv.Key] = kv.Value;
        }

        v["rebalance_multiplier"] = 1.0;   // force neutral multiplier (the config now bakes 4.0) to isolate weights-only
        v["per_layer_weights"] = "1.0,1.0,1.0,1.0,1.0,1.0,1.0,2.5,5.0,1.1,4.0,1.0";
        string json = JsonSerializer.Serialize(wf.Build(v, catalog.Resolve(cfg), Gen));
        Assert.Contains("ConditioningKrea2Rebalance", json);
        Assert.Contains("\"multiplier\":1", json);
    }

    [Fact]
    public void Krea2Refine_builds_base_then_turbo_polish_chain()
    {
        string json = BuildJson("krea2-refine", Gen);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        // Two UNet loaders: RAW base in node 4, Turbo refiner (motion_model slot) in node 40. The RAW base resolves to
        // the int8 quant — it's the registered file that's actually on disk (the fp8 one is registered but not
        // downloaded, and the requirement presence-gates on what's there).
        Assert.Equal("krea2-raw.safetensors", root.GetProperty("4").GetProperty("inputs").GetProperty("unet_name").GetString());
        Assert.Equal("krea2-turbo.safetensors", root.GetProperty("40").GetProperty("inputs").GetProperty("unet_name").GetString());
        // Baked uncensor rebalance is spliced (node 13).
        Assert.Equal("ConditioningKrea2Rebalance", root.GetProperty("13").GetProperty("class_type").GetString());
        // Stage 1 base sampler (node 3): full denoise at base cfg 4, base model (node 4), from the empty latent (node 5).
        JsonElement s1 = root.GetProperty("3").GetProperty("inputs");
        Assert.Equal(1.0, s1.GetProperty("denoise").GetDouble());
        Assert.Equal(4.0, s1.GetProperty("cfg").GetDouble());
        Assert.Equal("4", s1.GetProperty("model")[0].GetString());
        Assert.Equal("13", s1.GetProperty("positive")[0].GetString());
        Assert.Equal("5", s1.GetProperty("latent_image")[0].GetString());
        // Stage 2 Turbo polish (node 30): partial denoise over the base latent (node 3), cfg 1, Turbo model (node 40).
        JsonElement s2 = root.GetProperty("30").GetProperty("inputs");
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
        (WorkflowCatalog? catalog, WorkflowRegistry? registry) = Build();
        WorkflowConfiguration? cfg = catalog.FindConfig("pony-v6");
        Assert.NotNull(cfg);
        IWorkflow? wf = registry.Find(cfg.WorkflowName);
        Assert.NotNull(wf);
        Dictionary<string, object?> v = new(StringComparer.OrdinalIgnoreCase);
        foreach (ParamSpec s in wf.Schema)
        {
            if (s.Default is not null)
            {
                v[s.Key] = s.Default;
            }
        }

        foreach (KeyValuePair<string, ConfigParam> kv in cfg.Params)
        {
            v[kv.Key] = kv.Value.Value;
        }
        // Mirrors ComfyClient.MergeParamsDict: this machine's settings sit over the shipped configuration.
        foreach (KeyValuePair<string, JsonElement> kv in catalog.ParamOverridesFor(cfg.Id))
        {
            v[kv.Key] = kv.Value;
        }

        v["steps"] = 42;   // an override
        string json = JsonSerializer.Serialize(wf.Build(v, catalog.Resolve(cfg), Gen));
        Assert.Contains("\"steps\":42", json);
    }

    [Fact]
    public void Qwen_pixelizer_with_reference_and_snap_references_the_scale_node_not_an_inline_dict()
    {
        // The QIE pixelizer's reference>0 branch VAE-encodes a snapped source. The FixedScale must be its OWN node and
        // REFERENCED — passing the node dict inline as `pixels` hands the encoder a dict ('dict' object has no
        // attribute 'shape'). Shared by pixelize-qwen/-longcat/-longcat-turbo/-firered.
        ResolvedRequirements req = new()
        {
            Checkpoint = "qwen.gguf",
            TextEncoders = ["te.gguf"],
            Vae = "vae.safetensors",
            Resolution = new ModelResolution { MinW = 928, MinH = 928, MaxW = 1664, MaxH = 1664, Step = 16 },
        };
        QwenPixelizeWorkflow wf = new();
        Dictionary<string, object?> v = new(StringComparer.OrdinalIgnoreCase)
        {
            // Schema no longer carries defaults (Phase B), so the test supplies every param the graph reads.
            ["clip_type"] = "qwen_image",
            ["dual"] = false,
            ["steps"] = 20,
            ["cfg"] = 4.0,
            ["sampler"] = "euler",
            ["scheduler"] = "simple",
            ["shift"] = 3.1,
            ["style_prompt"] = "Convert to pixel art, flat colors, clean crisp pixels, limited palette",
            ["out_scale"] = 3,
            ["palette"] = "adaptive",
            ["proj_method"] = "median",
            ["final_method"] = "median",
            ["w_start"] = 0.5,
            ["w_end"] = 1.0,
            ["start_percent"] = 0.0,
            ["end_percent"] = 1.0,
            ["project_every"] = 1,
            ["width"] = 0,
            ["height"] = 0,
            ["reference"] = 80,
            ["virtual_resolution"] = 256,
            ["snap_resolution"] = true,
            ["loader"] = "unet_gguf",
            ["grid_w"] = 384,
            ["grid_h"] = 256                 // grid_w/h carry no schema default
        };
        WorkflowInputs inputs = new() { SourceImageName = "src.png", SourceWidth = 1216, SourceHeight = 832 };
        ComfyWorkflowGraph graph = wf.Build(v, req, inputs);

        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(graph));
        JsonElement root = doc.RootElement;
        Assert.True(root.TryGetProperty("25", out JsonElement n25));                       // FixedScale exists as its own node
        Assert.Equal("ImageScale", n25.GetProperty("class_type").GetString());
        JsonElement pixels = root.GetProperty("21").GetProperty("inputs").GetProperty("pixels");
        Assert.Equal(JsonValueKind.Array, pixels.ValueKind);                        // a [id, idx] ref, not an inline node
        Assert.Equal("25", pixels[0].GetString());
    }
}