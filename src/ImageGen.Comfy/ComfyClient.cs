using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using ImageGen.Application.Media;
using ImageGen.Application.Rendering;
using Microsoft.Extensions.Logging;

namespace ImageGen.Comfy;

/// <summary>
/// Client over ComfyUI's API and the render backend adapter (<see cref="IComfyClient"/>). A generate/edit request
/// names a <see cref="WorkflowConfiguration"/>; this resolves it to its <see cref="IWorkflow"/> (via the
/// <see cref="WorkflowRegistry"/>), merges the configuration's parameter settings layer over the workflow's defaults,
/// resolves its requirement links to concrete filenames, and asks the workflow to build the ComfyUI graph. It then
/// POSTs the graph to <c>/prompt</c> and polls <c>/history</c>. This client owns only the HTTP plumbing + the machine
/// probes (loadable files, total VRAM); every graph topology lives in its own workflow class, and imaging goes
/// through <see cref="IMediaProcessor"/>.
/// </summary>
public sealed class ComfyClient : IComfyClient
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IComfyEndpoint _endpoint;
    private readonly WorkflowCatalog _catalog;
    private readonly WorkflowRegistry _registry;
    private readonly IMediaProcessor _media;
    private readonly ILogger<ComfyClient> _logger;
    private readonly string _clientId = Guid.NewGuid().ToString("N");
    private static readonly JsonSerializerOptions LogJson = new() { WriteIndented = true };

    /// <summary>
    /// Guards the live HttpClient and the endpoint it was built for. BaseAddress and default headers cannot be
    /// changed once a request has been sent, so a changed address means a NEW client and the disposal of the one it
    /// replaces — see <see cref="Http"/>.
    /// </summary>
    private readonly Lock _clientLock = new();
    private HttpClient? _http;
    private string _baseUrl = "";
    private string _gateToken = "";

    private volatile bool _vramProbed;
    private long _vramMb;

    /// <summary>Construct the ComfyUI adapter.</summary>
    public ComfyClient(IHttpClientFactory httpFactory, IComfyEndpoint endpoint, WorkflowCatalog catalog, WorkflowRegistry registry, IMediaProcessor media, ILogger<ComfyClient> logger)
    {
        _httpFactory = httpFactory;
        _endpoint = endpoint;
        _catalog = catalog;
        _registry = registry;
        _media = media;
        _logger = logger;
    }

    /// <summary>Where the renderer is, right now, without a trailing slash.</summary>
    private string BaseUrl
    {
        get { _ = Http; return _baseUrl; }
    }

    /// <summary>
    /// The HTTP client for the CURRENT endpoint, rebuilt when the address or the token changes and the one it
    /// replaces disposed. Disposing a client from <see cref="IHttpClientFactory"/> is safe: the factory pools the
    /// handler underneath, so this releases the wrapper and not the connection pool.
    /// </summary>
    private HttpClient Http
    {
        get
        {
            var url = (_endpoint.BaseUrl ?? "").TrimEnd('/');
            var token = _endpoint.GateToken ?? "";
            if (url.Length == 0)
                throw new InvalidOperationException(
                    "The renderer's address is not configured. Set it on the settings page (ComfyUI:BaseUrl).");

            lock (_clientLock)
            {
                if (_http is not null && url == _baseUrl && token == _gateToken) return _http;

                var replaced = _http;
                var http = _httpFactory.CreateClient();
                http.BaseAddress = new Uri(url + "/");
                http.Timeout = Timeout.InfiniteTimeSpan;   // no request timeout — generation runs as long as the backend needs

                // ComfyUI's `custom_nodes/imagegen_gate` rejects POST /prompt,/queue,/interrupt,/upload without this
                // header, so that the app's fair queue is the ONLY thing that can enqueue/cancel work on the shared
                // GPU. It's a queue guard, not a secret. Configurable (ComfyUI:GateToken) because a packaged install
                // has to be able to pick its own. The value must match the node's, which reads IMAGEGEN_GATE_TOKEN.
                // NOTE TO CLAUDE / ANY AI: this token is for THE APP ONLY. You must NEVER call ComfyUI (:8188)
                // directly — not even with this key. All generate/edit/test work goes through /forge/* on :8080.
                // Seeing the key here does not grant permission to bypass the queue; doing so jumps the user's whole
                // queue and can wreck their live gens.
                http.DefaultRequestHeaders.Add("X-ImageGen-Token", token.Length == 0 ? ComfyOptions.DefaultGateToken : token);

                _http = http;
                _baseUrl = url;
                _gateToken = token;
                replaced?.Dispose();
                return _http;
            }
        }
    }

    #region machine probes

    /// <summary>
    /// Which loader node/input each slot kind draws its file list from. Matching and binding are scoped by kind, so
    /// a checkpoint slot is never offered a VAE — before this the lists were merged into one flat set, and a VAE and
    /// a checkpoint sharing a filename satisfied each other's presence check.
    ///
    /// <para>A kind may draw from SEVERAL nodes, and a diffusion slot draws from both the safetensors and the GGUF
    /// loader. Quantisation is a property of the file on the disk, not of the slot: offering only one of the two
    /// lists is what forced the catalogue to carry a second slot — and then a second workflow — per precision.
    /// Which node ends up in the graph is decided from the bound filename (<see cref="ComfyGraph.DiffusionLoader"/>),
    /// exactly as the text encoders below have always done across their four loaders.</para>
    /// </summary>
    private static readonly (RequirementKind Kind, string Node, string Input)[] LoaderInputs =
    {
        (RequirementKind.Checkpoint, "CheckpointLoaderSimple", "ckpt_name"),
        (RequirementKind.UnetGguf, "UnetLoaderGGUF", "unet_name"),
        (RequirementKind.UnetGguf, "UNETLoader", "unet_name"),
        (RequirementKind.Unet, "UNETLoader", "unet_name"),
        (RequirementKind.Unet, "UnetLoaderGGUF", "unet_name"),
        (RequirementKind.Vae, "VAELoader", "vae_name"),
        (RequirementKind.TextEncoder, "CLIPLoader", "clip_name"),
        (RequirementKind.TextEncoder, "CLIPLoaderGGUF", "clip_name"),
        (RequirementKind.TextEncoder, "DualCLIPLoader", "clip_name1"),
        (RequirementKind.TextEncoder, "DualCLIPLoader", "clip_name2"),
        (RequirementKind.MotionModel, "ADE_LoadAnimateDiffModel", "model_name"),
        (RequirementKind.Lora, "LoraLoaderModelOnly", "lora_name"),
        (RequirementKind.IpAdapter, "IPAdapterModelLoader", "ipadapter_file"),
        (RequirementKind.ClipVision, "CLIPVisionLoader", "clip_name"),
        (RequirementKind.ControlNet, "ControlNetLoader", "control_net_name"),
        (RequirementKind.LatentUpscaleModel, "LatentUpscaleModelLoader", "model_name"),
        (RequirementKind.UpscaleModel, "UpscaleModelLoader", "model_name"),
        // Both SeedVR2 loaders read the pack's own folder, so they are two inputs onto one file list rather
        // than two different sets — the DiT and the VAE genuinely live together.
        (RequirementKind.SeedVr2, "SeedVR2LoadDiTModel", "model"),
        (RequirementKind.SeedVr2, "SeedVR2LoadVAEModel", "model"),
    };

    /// <summary>
    /// What each slot kind can be filled with on this machine. A kind absent from the result has no loader in this
    /// ComfyUI build, which is itself the gate for the custom-node-dependent workflows.
    /// </summary>
    public async Task<IReadOnlyDictionary<RequirementKind, IReadOnlyList<string>>> GetPresentFilesByKindAsync(
        CancellationToken ct = default)
    {
        var byKind = new Dictionary<RequirementKind, List<string>>();
        foreach (var group in LoaderInputs.GroupBy(l => l.Kind))
        {
            var files = await ReadLoaderFilesAsync(group.Select(g => (g.Node, g.Input)).ToArray(), ct);
            if (files.Count == 0) continue;

            // Some packs publish the models they SUPPORT rather than the files present. SeedVR2 ships a registry
            // of ten builds with their hashes and offers all ten whether or not any are downloaded. Binding one
            // of those makes a slot read satisfied and its workflows
            // read ready, and the failure only arrives at render — or, worse, the pack fetches the model itself.
            // ComfyUI can say what is actually in the folder, so where a kind names one, the offer is narrowed
            // to the intersection.
            if (FolderForKind.TryGetValue(group.Key, out var folder))
            {
                var onDisk = await ReadFolderFilesAsync(folder, ct);
                if (onDisk.Count > 0) files.IntersectWith(onDisk);
            }

            if (files.Count == 0) continue;
            if (!byKind.TryGetValue(group.Key, out var list)) byKind[group.Key] = list = new List<string>();
            list.AddRange(files);
        }

        if (byKind.Count == 0)
            throw new HttpRequestException("ComfyUI returned no models — is it running with the model paths configured?");

        return byKind.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<string>)kv.Value.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>Every loadable filename across the loaders a workflow might use, for presence-gating a configuration.</summary>
    public async Task<IReadOnlySet<string>> GetPresentFilesAsync(CancellationToken ct = default)
    {
        var files = await ReadLoaderFilesAsync(new[]
        {
            ("CheckpointLoaderSimple", "ckpt_name"),
            ("UnetLoaderGGUF", "unet_name"),
            ("UNETLoader", "unet_name"),
            ("VAELoader", "vae_name"),
            ("CLIPLoader", "clip_name"),
            ("CLIPLoaderGGUF", "clip_name"),
            ("DualCLIPLoader", "clip_name1"),
            ("DualCLIPLoader", "clip_name2"),
            ("ADE_LoadAnimateDiffModel", "model_name"),
            ("LoraLoaderModelOnly", "lora_name"),
            ("IPAdapterModelLoader", "ipadapter_file"),
            ("CLIPVisionLoader", "clip_name"),
            ("ControlNetLoader", "control_net_name"),
            // HunyuanVideo 1.5 super-resolution latent upsampler (own folder, own loader node).
            ("LatentUpscaleModelLoader", "model_name"),
            // ESRGAN-family image upscalers (models/upscale_models), for the Upscale editors.
            ("UpscaleModelLoader", "model_name"),
            // SeedVR2 diffusion upscaler — numz custom-node loaders. Absent unless the node pack is installed, which
            // is exactly the gate we want. NOTE: these nodes list their whole model registry whether or not the
            // weights are on disk (the pack downloads on first use), so this gates on the PACK, not the weights.
            ("SeedVR2LoadDiTModel", "model"),
            ("SeedVR2LoadVAEModel", "model"),
        }, ct);
        if (files.Count == 0)
            throw new HttpRequestException("ComfyUI returned no models — is it running with the model paths configured?");
        return files;
    }

    /// <summary>
    /// Read the filename choices from each (node, input-key) pair's object_info. A node absent from this build, or
    /// carrying no such input, is a normal outcome and is SKIPPED — every one of those is detected by asking rather
    /// than by catching, so no exception has to be interpreted.
    /// <para>Nothing here is swallowed. This once ran under a blanket catch, which meant a dropped connection or a
    /// response ComfyUI had garbled read as "that loader offers no files" — and since the catalog presence-gates on
    /// exactly these names, every configuration behind that loader quietly disappeared from the model picker, with
    /// the whole set reported as the misleading "ComfyUI returned no models". A transport or parse failure is a real
    /// error about a backend we already know is up, so it propagates and the caller answers 502 with the cause.</para>
    /// </summary>
    /// <summary>
    /// Kinds whose loader advertises a capability list rather than a directory listing, and the ComfyUI folder
    /// that holds the real files. Only these are narrowed: a loader that already enumerates its folder needs no
    /// second opinion, and asking for one would only add a request per kind.
    /// </summary>
    private static readonly Dictionary<RequirementKind, string> FolderForKind = new()
    {
        [RequirementKind.SeedVr2] = "seedvr2",
    };

    /// <summary>
    /// What is actually in a ComfyUI model folder. Empty when the build has no such endpoint or no such folder,
    /// which is treated as "cannot narrow" rather than "nothing is there" — losing a real file would hide a
    /// workflow, and that is the worse mistake of the two.
    /// </summary>
    private async Task<HashSet<string>> ReadFolderFilesAsync(string folder, CancellationToken ct)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var resp = await Http.GetAsync($"models/{folder}", ct);
            if (!resp.IsSuccessStatusCode) return files;
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return files;
            foreach (var e in doc.RootElement.EnumerateArray())
                if (e.GetString() is { Length: > 0 } f) files.Add(f);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            // Older build, or the folder is not registered. Narrowing is an improvement, not a requirement.
        }
        return files;
    }

    private async Task<HashSet<string>> ReadLoaderFilesAsync((string node, string key)[] pairs, CancellationToken ct)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (node, key) in pairs)
        {
            using var resp = await Http.GetAsync($"object_info/{node}", ct);
            if (!resp.IsSuccessStatusCode) continue;                                  // node not in this build
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty(node, out var ne)) continue;           // ditto
            if (!ne.TryGetProperty("input", out var input)
                || !input.TryGetProperty("required", out var required)
                || !required.TryGetProperty(key, out var keyEl)) continue;             // node has no such input
            foreach (var n in ComboOptions(keyEl))
                if (n.GetString() is { Length: > 0 } f) files.Add(f);
        }
        return files;
    }

    /// <summary>
    /// Which of <paramref name="nodes"/> this ComfyUI has registered.
    ///
    /// <para>Presence-gating is otherwise entirely file-based, and that only works for a pack whose nodes LOAD
    /// something: if a loader is gone its filenames are gone, and every configuration behind it disappears. A pack
    /// whose node loads nothing — <c>AnimaLLLiteApply</c> patches a model it is handed — contributes no filenames,
    /// so nothing about it can be inferred from the file lists and a workflow needing it looked perfectly ready
    /// right up until submit failed on an unregistered node.</para>
    /// </summary>
    public async Task<IReadOnlySet<string>> GetPresentNodesAsync(IEnumerable<string> nodes, CancellationToken ct = default)
    {
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes.Distinct(StringComparer.Ordinal))
        {
            using var resp = await Http.GetAsync($"object_info/{node}", ct);
            if (!resp.IsSuccessStatusCode) continue;                                  // not in this build
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            // ComfyUI answers 200 with {} for a node it does not know, so the body is the actual test.
            if (doc.RootElement.TryGetProperty(node, out _)) present.Add(node);
        }
        return present;
    }

    /// <summary>
    /// The choice list of a combo input, across BOTH object_info schema shapes ComfyUI now emits side by side:
    /// the classic <c>["a.safetensors", "b.safetensors"]</c> at slot 0, and the V3-node form
    /// <c>"COMBO", {"options": [...]}</c> that puts the names under slot 1 (UpscaleModelLoader and every other
    /// migrated node). Reading slot 0 blindly throws on the V3 form, which the caller would swallow as "no files"
    /// — silently hiding every configuration gated on that loader. Anything else yields nothing.
    /// </summary>
    internal static IEnumerable<JsonElement> ComboOptions(JsonElement keyEl)
    {
        if (keyEl.ValueKind != JsonValueKind.Array || keyEl.GetArrayLength() == 0) return Array.Empty<JsonElement>();
        if (keyEl[0].ValueKind == JsonValueKind.Array) return keyEl[0].EnumerateArray();
        if (keyEl.GetArrayLength() > 1 && keyEl[1].ValueKind == JsonValueKind.Object
            && keyEl[1].TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
            return opts.EnumerateArray();
        return Array.Empty<JsonElement>();
    }

    /// <summary>
    /// Total VRAM (MB) from ComfyUI's /system_stats (max across devices). Stable for the process — cached once on
    /// success.
    /// <para>Null means one specific thing: this backend answered, and its answer does not report device VRAM. That
    /// is a real state (an older build) and the catalog reads it as "cannot VRAM-gate", offering everything. A
    /// transport or parse FAILURE is not that state and no longer borrows its return value — it throws. Collapsing
    /// the two turned a hiccup into a silent claim that the box's VRAM is unknown, so configurations this 24 GB card
    /// cannot load were listed as available and failed later at the GPU instead of being filtered out here.</para>
    /// </summary>
    public async Task<long?> GetTotalVramMbAsync(CancellationToken ct = default)
    {
        if (_vramProbed) return _vramMb;
        using var resp = await Http.GetAsync("system_stats", ct);
        await EnsureOk(resp, "GET system_stats");
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("devices", out var devs) || devs.ValueKind != JsonValueKind.Array || devs.GetArrayLength() == 0)
            return null;
        long maxTotal = 0;
        foreach (var d in devs.EnumerateArray())
            if (d.TryGetProperty("vram_total", out var v) && v.ValueKind == JsonValueKind.Number)
                maxTotal = Math.Max(maxTotal, v.GetInt64());
        if (maxTotal <= 0) return null;
        _vramMb = maxTotal / (1024 * 1024);
        _vramProbed = true;
        return _vramMb;
    }

    #endregion

    #region async surface: submit (build workflow + POST /prompt, return prompt_id)

    /// <summary>Resolve the configuration + workflow, build the generate graph, POST it to <c>/prompt</c> under this
    /// client's <c>client_id</c>, and return the <c>prompt_id</c> WITHOUT polling.</summary>
    public async Task<string> SubmitGenerateAsync(string prompt, string? negativePrompt, string? configId, string? aspect,
        IReadOnlyDictionary<string, JsonElement>? overrides, CancellationToken ct)
    {
        var (cfg, wf) = ResolveGenerate(configId);
        var dict = MergeParamsDict(wf, cfg, overrides);
        var resolved = _catalog.Resolve(cfg);
        wf.Normalize(dict, new NormalizeContext { Requirements = resolved, AtSubmit = true });   // submit pass (no source image for generate)
        var values = new ParamValues(dict);
        var (pos, neg) = ApplyGenPromptRules(values, prompt ?? "", negativePrompt);
        var inputs = new WorkflowInputs { Positive = pos, Negative = neg, Aspect = ComfyGraph.NormalizeAspect(aspect) };
        var graph = wf.Build(values, resolved, inputs);
        return await SubmitAsync(graph, ct);
    }

    /// <summary>Upload the source PNG (and any references) to ComfyUI's input folder, build the configuration's edit
    /// graph, POST it to <c>/prompt</c>, and return the <c>prompt_id</c> WITHOUT polling.</summary>
    public async Task<string> SubmitEditAsync(byte[] sourcePng, string instruction, string? negativePrompt, string? configId,
        IReadOnlyList<byte[]>? references, IReadOnlyDictionary<string, JsonElement>? overrides, byte[]? maskPng = null,
        byte[]? lastFramePng = null, CancellationToken ct = default)
    {
        if (sourcePng is null || sourcePng.Length == 0)
            throw new RenderValidationException("A source image is required for editing.");
        var (cfg, wf) = ResolveEdit(configId);
        // An empty instruction is valid: pixel-quantize ignores the prompt entirely, the pixelize workflows fall
        // back to their own style_prompt, and editors with a non-blank conditioning default handle it too.

        // Video-to-video (the pixel-quantize V2V pass): the source is a CLIP, not a still. Upload it as a real video
        // file ComfyUI's LoadVideo can decode — transcoding our animated-webp clips to mp4 first (PyAV/ffmpeg can't
        // multi-frame-decode animated webp) and passing mp4/webm uploads through as-is — and build with SourceVideoName.
        if (wf.SourceMedia == WorkflowMedia.Video)
        {
            var videoName = await UploadSourceVideoAsync(sourcePng, ct);
            var dict0 = MergeParamsDict(wf, cfg, overrides);
            var resolved0 = _catalog.Resolve(cfg);
            wf.Normalize(dict0, new NormalizeContext { Requirements = resolved0, AtSubmit = true });
            var values0 = new ParamValues(dict0);
            var inputs0 = new WorkflowInputs { Positive = instruction, SourceVideoName = videoName };
            return await SubmitAsync(wf.Build(values0, resolved0, inputs0), ct);
        }

        // Distinct filename per role — a fixed name for every upload made source and references clobber each other
        // in ComfyUI's input folder (overwrite=true). Role-indexed names keep them separate; the job queue
        // serializes ComfyUI work so these fixed names can't race.
        var uploadName = await UploadImageAsync(sourcePng, "forgemcp_edit_src.png", ct);
        var refNames = new List<string>();
        if (references is { Count: > 0 })
        {
            int ri = 0;
            foreach (var r in references)
                if (r is { Length: > 0 }) refNames.Add(await UploadImageAsync(r, $"forgemcp_edit_ref{ri++}.png", ct));
        }
        // Inpaint: a SEPARATE white-on-black mask image (the source stays pristine — baking the mask into the source's
        // alpha would let PNG premultiplication zero the masked RGB, blacking out the region the model must preserve).
        string? maskName = maskPng is { Length: > 0 } ? await UploadImageAsync(maskPng, "forgemcp_edit_mask.png", ct) : null;
        // i2v first/last-frame: the last frame the clip should end on, uploaded under its own role name so it never
        // collides with the source/refs/mask. Consumed via WorkflowInputs.EndImageName by workflows that support it.
        string? lastName = lastFramePng is { Length: > 0 } ? await UploadImageAsync(lastFramePng, "forgemcp_edit_last.png", ct) : null;
        var dict = MergeParamsDict(wf, cfg, overrides);
        var srcDim = _media.Identify(sourcePng);   // source dims drive the render-resolution snap (no UI width/height)
        var (srcW, srcH) = (srcDim.Width, srcDim.Height);
        var resolved = _catalog.Resolve(cfg);
        // Submit-pass normalization: snap the render resolution onto a clean ×VRES multiple (deliberate, no notice) now,
        // so Build reads the cached size rather than recomputing it. Source dims + the model's envelope live here.
        wf.Normalize(dict, new NormalizeContext { SourceWidth = srcW, SourceHeight = srcH, Requirements = resolved, AtSubmit = true });
        var values = new ParamValues(dict);
        if (values.Bool("snap_resolution", false))
            _logger.LogInformation("Edit '{Config}': snap_resolution ON, source {W}x{H} — render size snapped to a clean integer ×VRES multiple (or the request fails if it can't).", configId, srcW, srcH);
        var inputs = new WorkflowInputs { Positive = instruction, Negative = negativePrompt, SourceImageName = uploadName, SourceWidth = srcW, SourceHeight = srcH, ReferenceImageNames = refNames, MaskImageName = maskName, EndImageName = lastName };
        var graph = wf.Build(values, resolved, inputs);
        return await SubmitAsync(graph, ct);
    }

    private (WorkflowConfiguration cfg, IWorkflow wf) ResolveGenerate(string? configId)
    {
        if (string.IsNullOrWhiteSpace(configId))
            throw new RenderValidationException("A workflow configuration is required. Call list_models and pass one.");
        var cfg = _catalog.FindConfig(configId)
                  ?? throw new RenderValidationException($"Unknown workflow configuration '{configId}'. Call list_models for valid ids.");
        var wf = _registry.Find(cfg.WorkflowName)
                 ?? throw new RenderValidationException($"Workflow '{cfg.WorkflowName}' for configuration '{configId}' is not registered.");
        if (wf.Kind != WorkflowKind.Generate)
            throw new RenderValidationException($"Configuration '{configId}' is an edit workflow, not a generate one.");
        return (cfg, wf);
    }

    private (WorkflowConfiguration cfg, IWorkflow wf) ResolveEdit(string? configId)
    {
        if (string.IsNullOrWhiteSpace(configId))
            throw new RenderValidationException("An edit workflow configuration is required (one whose can_edit is true).");
        var cfg = _catalog.FindConfig(configId)
                  ?? throw new RenderValidationException($"Unknown workflow configuration '{configId}'. Call list_models for valid ids.");
        var wf = _registry.Find(cfg.WorkflowName)
                 ?? throw new RenderValidationException($"Workflow '{cfg.WorkflowName}' for configuration '{configId}' is not registered.");
        if (wf.Kind != WorkflowKind.Edit)
            throw new RenderValidationException($"Configuration '{configId}' is not an editing configuration. Pick one whose can_edit is true.");
        return (cfg, wf);
    }

    /// <summary>Overlay the configuration's settings layer (then any request overrides) on the workflow's schema
    /// defaults into a mutable bag (so a normalization pass can clamp values before it's frozen into <see cref="ParamValues"/>).</summary>
    private Dictionary<string, object?> MergeParamsDict(IWorkflow wf, WorkflowConfiguration cfg, IReadOnlyDictionary<string, JsonElement>? overrides)
    {
        var v = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in wf.Schema) if (spec.Default is not null) v[spec.Key] = spec.Default;
        foreach (var kv in cfg.Params) v[kv.Key] = kv.Value.Value;
        // Then THIS MACHINE's overrides, from the workflow's settings page: the render size for each aspect, the
        // step count, whatever the configuration exposes. Between the shipped configuration and the request,
        // because that is what they are — the operator's answer for this box, which a caller may still override
        // per generation unless the parameter is locked.
        foreach (var kv in _catalog.ParamOverridesFor(cfg.Id)) v[kv.Key] = kv.Value;
        // A locked param (object-form "exposed": false) is a baked value the caller cannot override — its request
        // value is dropped so the configuration's setting is enforced on every generation. All other keys overlay.
        if (overrides is not null)
            foreach (var kv in overrides)
                if (!(cfg.Params.TryGetValue(kv.Key, out var cp) && cp.Locked))
                    v[kv.Key] = kv.Value;

        // A parameter declared IsModelRef holds a SLOT ID; substitute the file this machine has bound to it. These
        // parameters used to carry filenames directly, which put a second set of one person's filenames in the
        // configurations, outside the binding system and beyond a user's reach. One implementation, shared with the
        // test suite's merge — see WorkflowCatalog.ResolveModelRefs.
        _catalog.ResolveModelRefs(wf, cfg.Id, v);
        return v;
    }

    /// <summary>Pre-queue parameter normalization (synchronous; NO ComfyUI call). Resolves the config + workflow,
    /// overlays params, and lets the workflow's <see cref="IWorkflow.Normalize"/> snap any out-of-range input (today:
    /// a stepped frame count) onto a model-valid value. Returns the corrected override set (the changed keys folded
    /// back in, so the worker's <see cref="MergeParams"/> picks them up) plus a single user-facing notice (newline-
    /// joined if several). The <see cref="JobQueue"/> calls this as it creates each slot — so the corrected value is
    /// what gets built and the notice is on the slot before its placeholder card renders. Returns the overrides
    /// unchanged with a null notice when nothing changed, and no-ops on an unknown/mis-kinded config (the worker
    /// surfaces that real error at submit, not here).</summary>
    public QueueNormalizationResult NormalizeForQueue(
        string? configId, RenderKind kind, IReadOnlyDictionary<string, JsonElement>? overrides)
    {
        WorkflowConfiguration cfg; IWorkflow wf;
        try { (cfg, wf) = kind == RenderKind.Edit ? ResolveEdit(configId) : ResolveGenerate(configId); }
        catch (RenderValidationException) { return new QueueNormalizationResult(null, null); }

        var merged = MergeParamsDict(wf, cfg, overrides);
        var before = new Dictionary<string, object?>(merged, StringComparer.OrdinalIgnoreCase);
        var notices = wf.Normalize(merged, NormalizeContext.Empty);   // enqueue pass: params only (frame snap), no source/requirements
        if (notices.Count == 0) return new QueueNormalizationResult(null, null);   // nothing changed → caller keeps the original overrides

        var outv = overrides is null
            ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(overrides, StringComparer.Ordinal);
        foreach (var k in merged.Keys)
            if (!before.TryGetValue(k, out var ov) || !Equals(ov, merged[k]))
                outv[k] = JsonSerializer.SerializeToElement(merged[k]);
        return new QueueNormalizationResult(outv, string.Join("\n", notices));
    }

    /// Generation prompt rules (lifted from the old BuildGenerateWorkflow): prepend the model's required tag prefix,
    /// and suppress the negative for distilled models (cfg<=1) or models declaring no negative support.
    private static (string pos, string? neg) ApplyGenPromptRules(ParamValues p, string prompt, string? negative)
    {
        var rp = p.Str("required_prefix");
        var prefix = string.IsNullOrWhiteSpace(rp) ? "" : rp!.TrimEnd().TrimEnd(',').TrimEnd() + ", ";
        var pos = prefix + prompt;
        // Negative = the model's default (config `negative`, else the shared DefaultNegative) with the user's UI
        // negative APPENDED — never replaced. Suppressed entirely for distilled (cfg<=1) or negative-less models.
        var negOk = p.Dbl("cfg", 7) > 1 && p.Bool("negative_supported", true);
        var neg = negOk ? ComfyGraph.ComposeNegative(p.Str("negative"), negative) : "";
        return (pos, neg);
    }

    #endregion

    #region HTTP plumbing

    /// <summary>One non-looping <c>/history/{promptId}</c> check. Returns the produced image if ready, throws if
    /// ComfyUI reported an error, or returns null if not present yet (caller should poll again).</summary>
    public async Task<GeneratedImage?> PollResultAsync(string promptId, CancellationToken ct = default)
    {
        using var hresp = await Http.GetAsync($"history/{promptId}", ct);
        if (!hresp.IsSuccessStatusCode) return null;
        using var hdoc = await JsonDocument.ParseAsync(await hresp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!hdoc.RootElement.TryGetProperty(promptId, out var entry)) return null;

        if (entry.TryGetProperty("status", out var status) &&
            status.TryGetProperty("status_str", out var ss) && ss.GetString() == "error")
            throw new RenderValidationException(DescribeComfyError(status, promptId));

        if (!entry.TryGetProperty("outputs", out var outputs)) return null;

        // Scan ALL output nodes: the produced clip/image is the first node carrying `images` (SaveAnimatedWEBP /
        // SaveImage). The pixel-quantize (fp) node additionally surfaces its derived `palette` (inline #RRGGBB array)
        // and native-res `lossless_frames` (saved-PNG refs) under distinct keys — collect those too so Forge can
        // persist them next to the produced image. Distinct keys => no collision with the result image.
        JsonElement? resultImg = null;
        string? paletteJson = null;
        string? frequenciesJson = null;
        List<byte[]>? losslessFrames = null;
        foreach (var node in outputs.EnumerateObject())
        {
            var v = node.Value;
            if (resultImg is null && v.TryGetProperty("images", out var images) && images.GetArrayLength() > 0)
                resultImg = images[0];
            if (paletteJson is null && v.TryGetProperty("palette", out var pal) && pal.ValueKind == JsonValueKind.Array)
                paletteJson = pal.GetRawText();
            // The fp quantize also surfaces its pooled label frequencies (floats, indexed by palette order) — the
            // second global a single-frame replay needs to reproduce the batch's rarity weighting exactly.
            if (frequenciesJson is null && v.TryGetProperty("frequencies", out var fq) && fq.ValueKind == JsonValueKind.Array)
                frequenciesJson = fq.GetRawText();
            if (losslessFrames is null && v.TryGetProperty("lossless_frames", out var lf)
                && lf.ValueKind == JsonValueKind.Array && lf.GetArrayLength() > 0)
            {
                losslessFrames = new List<byte[]>(lf.GetArrayLength());
                foreach (var fr in lf.EnumerateArray())
                    losslessFrames.Add(await Http.GetByteArrayAsync(ViewUrl(fr), ct));
            }
        }
        if (resultImg is { } img)
        {
            var file = img.GetProperty("filename").GetString()!;
            var sub = img.TryGetProperty("subfolder", out var sf) ? sf.GetString() ?? "" : "";
            var type = img.TryGetProperty("type", out var t) ? t.GetString() ?? "output" : "output";
            var bytes = await Http.GetByteArrayAsync(ViewUrl(file, sub, type), ct);
            return new GeneratedImage(bytes, "", file, sub, type, paletteJson, losslessFrames, frequenciesJson);
        }
        return null;
    }

    /// <summary>Build a ComfyUI <c>/view</c> url for a saved-output ref (filename/subfolder/type).</summary>
    private static string ViewUrl(JsonElement fileRef) => ViewUrl(
        fileRef.GetProperty("filename").GetString()!,
        fileRef.TryGetProperty("subfolder", out var s) ? s.GetString() ?? "" : "",
        fileRef.TryGetProperty("type", out var t) ? t.GetString() ?? "output" : "output");

    private static string ViewUrl(string file, string sub, string type) =>
        $"view?filename={Uri.EscapeDataString(file)}&subfolder={Uri.EscapeDataString(sub)}&type={type}";

    /// <summary>ComfyUI reports a failed prompt in history <c>status.messages</c> as an
    /// <c>["execution_error", {...}]</c> pair carrying the failing node and the Python exception. Surface the full
    /// exception (type + message) in the thrown message — which the job queue stores verbatim into the slot error and
    /// shows in the UI — and log the full Python traceback server-side (it's too long for the UI line).</summary>
    private string DescribeComfyError(JsonElement status, string promptId)
    {
        if (status.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
            foreach (var m in msgs.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Array || m.GetArrayLength() < 2 || m[0].GetString() != "execution_error")
                    continue;
                var p = m[1];
                string? Get(string k) => p.TryGetProperty(k, out var v) ? v.GetString() : null;
                var node = Get("node_type"); var nid = Get("node_id");
                var where = node is null ? "" : $" in {node}{(nid is null ? "" : $" (node {nid})")}";

                _logger.LogError("ComfyUI execution_error (prompt {PromptId}){Where}: {Type}", promptId, where, Get("exception_type"));

                return $"ComfyUI error{where}: {Get("exception_type")}: {Get("exception_message")}";
            }
        return "ComfyUI reported an error but included no execution_error detail.";
    }

    /// <summary>What ComfyUI currently holds, from <c>GET /queue</c>, with its two lists kept APART:
    /// <c>queue_running</c> is what the GPU is executing, <c>queue_pending</c> is what waits behind it. The split is
    /// the evidence a slot's "running" status is built on — merging them (as this once did) means calling a prompt
    /// that is still queued behind someone else's render "being generated".
    /// <para>The union answers the other question: a submitted prompt in neither list nor <c>/history</c> is one
    /// ComfyUI has LOST (it restarted/crashed) — the liveness signal for failing an orphaned slot instead of polling
    /// it forever. Returns <c>null</c> when ComfyUI is unreachable/malformed, which is distinct from an empty queue
    /// (ComfyUI up and idle): the caller must NOT read "unreachable" as "your prompt is gone", so a vanish is only
    /// acted on when this returns non-null and neither list holds the prompt.</para></summary>
    public async Task<BackendQueue?> GetQueueAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await Http.GetAsync("queue", ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("ComfyUI GET queue answered {Status}; treating the backend queue as unknown.", (int)resp.StatusCode);
                return null;
            }
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return new BackendQueue(PromptIdsIn(doc.RootElement, "queue_running"),
                                    PromptIdsIn(doc.RootElement, "queue_pending"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Null here is the ANSWER, not a swallowed failure: "unknown" is a real third state that the poller is
            // built around, and throwing instead would fail a live slot over one blocked socket. What was missing is
            // the reason — this used to discard the exception entirely, so a backend that had been answering garbage
            // for an hour was indistinguishable from one that was merely busy, in the logs and everywhere else.
            _logger.LogWarning(ex, "ComfyUI GET queue failed; treating the backend queue as unknown (no prompt is presumed lost).");
            return null;
        }
    }

    /// <summary>The prompt ids in one of <c>/queue</c>'s lists. Each entry is
    /// <c>[number, prompt_id, prompt, extra_data, outputs]</c>; the prompt id is element 1.</summary>
    private static IReadOnlySet<string> PromptIdsIn(JsonElement root, string key)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (!root.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array) return ids;
        foreach (var entry in arr.EnumerateArray())
            if (entry.ValueKind == JsonValueKind.Array && entry.GetArrayLength() > 1
                && entry[1].ValueKind == JsonValueKind.String && entry[1].GetString() is { Length: > 0 } pid)
                ids.Add(pid);
        return ids;
    }

    /// <summary>POST <c>/interrupt</c> (empty body) to cancel the currently-running prompt. THROWS on a transport or
    /// HTTP failure, like every other call here: an interrupt that did not land means the GPU is still rendering work
    /// somebody cancelled, which is exactly the thing an operator needs told. This used to swallow every failure —
    /// and each of its callers wrapped it in a second empty catch — so a backend that had stopped honouring
    /// interrupts looked identical to one that was cancelling promptly. Callers decide whether a failed interrupt
    /// should fail their own operation; none of them may discard the reason.</summary>
    public async Task InterruptAsync(CancellationToken ct = default)
    {
        using var resp = await Http.PostAsync("interrupt", new ByteArrayContent(Array.Empty<byte>()), ct);
        await EnsureOk(resp, "POST interrupt");
    }

    /// <summary>POST <c>/free</c> asking ComfyUI to unload every loaded model and release its cached VRAM. ComfyUI
    /// takes these as queue FLAGS consumed by its worker BETWEEN prompts, so this never interrupts a render already in
    /// flight — an idle backend frees immediately, a busy one frees when the running prompt finishes. Failures are NOT
    /// swallowed: this is user-initiated, so the caller reports what ComfyUI actually said.</summary>
    public async Task FreeMemoryAsync(CancellationToken ct = default)
    {
        using var resp = await Http.PostAsJsonAsync("free", new { unload_models = true, free_memory = true }, ct);
        await EnsureOk(resp, "POST free");
    }

    /// <summary>POST a built workflow to /prompt under this client's client_id; return the prompt_id (no polling).</summary>
    private async Task<string> SubmitAsync(Dictionary<string, object> workflow, CancellationToken ct)
    {
        // The submitted graph is NOT logged. It used to be, behind Logging:LogPrompts, to inspect exactly what reached
        // the model (artist tags, prefixes, paren escaping, weights) — but the graph embeds the user's prompt and
        // negative in plaintext, so "off by default" meant prompts were one config toggle away from being written to
        // disk permanently. The prompt that produced any image is recoverable from the per-user ENCRYPTED log
        // (Logging:AuditUserPrompts), which is the channel that exists for this; the app log gets nothing.
        using var submit = await Http.PostAsJsonAsync("prompt", new { prompt = workflow, client_id = _clientId }, ct);
        await EnsureOk(submit, "POST prompt");
        using var sdoc = await JsonDocument.ParseAsync(await submit.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return sdoc.RootElement.GetProperty("prompt_id").GetString()!;
    }

    /// <summary>Fetch raw bytes for a legacy image id (a ComfyUI view-ref minted before DB storage) by proxying
    /// <c>/view</c>. Throws when the backend doesn't have it.</summary>
    public async Task<byte[]> FetchLegacyImageAsync(string imageId, CancellationToken ct)
    {
        var (file, sub, type) = DecodeId(imageId);
        return await Http.GetByteArrayAsync(ViewUrl(file, sub, type), ct);
    }

    /// <summary>Decode an image id into a ComfyUI view-ref (filename/subfolder/type). A bare id (no ':') is an output
    /// image; otherwise it is "type:subfolder:filename" split on the first two colons (the filename may contain ':').</summary>
    private static (string filename, string subfolder, string type) DecodeId(string id)
    {
        var firstColon = id.IndexOf(':');
        if (firstColon < 0) return (id, "", "output");
        var type = id[..firstColon];
        var rest = id[(firstColon + 1)..];
        var secondColon = rest.IndexOf(':');
        if (secondColon < 0) return (rest, "", type);
        return (rest[(secondColon + 1)..], rest[..secondColon], type);
    }

    /// <summary>Connect to ComfyUI's <c>/ws</c> under this client's id and return the open socket, so the API can proxy
    /// progress/preview frames. The upstream carries every client's progress; the caller filters to the owner.</summary>
    public async Task<WebSocket> ConnectProgressSocketAsync(CancellationToken ct)
    {
        var wsUrl = BaseUrl.Replace("https://", "wss://").Replace("http://", "ws://") + "/ws?clientId=" + _clientId;
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(wsUrl), ct);
        return socket;
    }

    /// <summary>Upload PNG bytes to ComfyUI's input folder (POST /upload/image, multipart); returns the stored filename.</summary>
    private Task<string> UploadImageAsync(byte[] png, string filename, CancellationToken ct) =>
        UploadFileAsync(png, filename, "image/png", ct);

    /// <summary>Upload a file (image OR video) to ComfyUI's input folder. ComfyUI's /upload/image route writes whatever
    /// file it's given to the input dir verbatim, so a video posted here lands where <c>LoadVideo</c> can list it; the
    /// stored name (returned) is what the graph references.</summary>
    private async Task<string> UploadFileAsync(byte[] bytes, string filename, string contentType, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        form.Add(file, "image", filename);
        form.Add(new StringContent("true"), "overwrite");
        form.Add(new StringContent("input"), "type");
        using var resp = await Http.PostAsync("upload/image", form, ct);
        await EnsureOk(resp, "POST upload/image");
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc.RootElement.GetProperty("name").GetString()!;
    }

    /// <summary>Upload a video-to-video source CLIP to ComfyUI's input folder so <c>LoadVideo</c> can decode it. Our
    /// generated clips are animated WEBP, which ffmpeg/PyAV only single-frame-decode, so those are transcoded to mp4
    /// first (reusing <see cref="ForgeVideo.WebpToMp4Async"/>); an upload that is already a real container (mp4/webm)
    /// is sent through unchanged. Returns the stored filename. Throws if the bytes aren't a clip we can ingest.</summary>
    private async Task<string> UploadSourceVideoAsync(byte[] bytes, CancellationToken ct)
    {
        if (_media.IsAnimatedWebp(bytes))
        {
            var mp4 = await _media.WebpToMp4Async(bytes, null, ct);
            return await UploadFileAsync(mp4, "forgemcp_edit_src.mp4", "video/mp4", ct);
        }
        var (ext, mime) = DetectVideoContainer(bytes)
            ?? throw new RenderValidationException("The source isn't a video clip this editor can read (expected an animated WEBP, MP4, or WEBM).");
        return await UploadFileAsync(bytes, "forgemcp_edit_src." + ext, mime, ct);
    }

    /// <summary>Sniff a real video container from its header — MP4/MOV (an <c>ftyp</c> box) or Matroska/WEBM (the EBML
    /// magic). Returns (extension, mime) or null when it isn't one of those (e.g. an animated webp, handled separately,
    /// or a still image). Header-only; no decode.</summary>
    private static (string ext, string mime)? DetectVideoContainer(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 12 && b[4] == 'f' && b[5] == 't' && b[6] == 'y' && b[7] == 'p') return ("mp4", "video/mp4");
        if (b.Length >= 4 && b[0] == 0x1A && b[1] == 0x45 && b[2] == 0xDF && b[3] == 0xA3) return ("webm", "video/webm");
        return null;
    }

    private static async Task EnsureOk(HttpResponseMessage resp, string what)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync();
        throw new HttpRequestException($"ComfyUI {what} failed: {(int)resp.StatusCode} {resp.ReasonPhrase}\n{body}");
    }

    #endregion
}
