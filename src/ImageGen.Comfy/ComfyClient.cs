using ImageGen.Application.Media;
using ImageGen.Application.Rendering;
using ImageGen.Application.Snapshots;
using ImageGen.Application.Workflows;
using ImageGen.Comfy.Snapshots;
using ImageGen.Domain;
using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;

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
    private readonly ISnapshot<ComfyFilesByKind> _presentFiles;
    private readonly ILogger<ComfyClient> _logger;
    private readonly string _clientId = Guid.NewGuid().ToString("N");
    private static readonly JsonSerializerOptions LogJson = new() { WriteIndented = true };

    /// <summary>
    /// Guards the live HttpClient and the endpoint it was built for. BaseAddress and default headers cannot be
    /// changed once a request has been sent, so a changed address means a NEW client and the disposal of the one it
    /// replaces — see <see cref="Http"/>.
    /// </summary>
    private readonly Lock _clientLock = new();
    private string _baseUrl = "";
    private string _gateToken = "";

    private volatile bool _vramProbed;
    private long _vramMb;

    /// <summary>ComfyUI API JSON field names / response keys, read via TryGetProperty/GetProperty.</summary>
    private static class Field
    {
        public const string Input = "input";
        public const string Required = "required";
        public const string Options = "options";
        public const string Outputs = "outputs";
        public const string Images = "images";
        public const string Palette = "palette";
        public const string Frequencies = "frequencies";
        public const string LosslessFrames = "lossless_frames";
        public const string Status = "status";
        public const string StatusStr = "status_str";
        public const string Error = "error";
        public const string Filename = "filename";
        public const string Subfolder = "subfolder";
        public const string Type = "type";
        public const string Messages = "messages";
        public const string ExecutionError = "execution_error";
        public const string NodeType = "node_type";
        public const string NodeId = "node_id";
        public const string ExceptionType = "exception_type";
        public const string ExceptionMessage = "exception_message";
        public const string QueueRunning = "queue_running";
        public const string QueuePending = "queue_pending";
        public const string PromptId = "prompt_id";
        public const string Name = "name";
        public const string Devices = "devices";
        public const string VramTotal = "vram_total";
    }

    /// <summary>ComfyUI HTTP endpoint paths (relative to the client's BaseAddress).</summary>
    private static class Endpoint
    {
        public const string FolderPaths = "internal/folder_paths";
        public const string SystemStats = "system_stats";
        public const string Queue = "queue";
        public const string Interrupt = "interrupt";
        public const string Free = "free";
        public const string Prompt = "prompt";
        public const string UploadImage = "upload/image";
    }

    /// <summary>Operation labels surfaced in EnsureOk failure messages.</summary>
    private static class Op
    {
        public const string GetSystemStats = "GET system_stats";
        public const string PostInterrupt = "POST interrupt";
        public const string PostFree = "POST free";
        public const string PostPrompt = "POST prompt";
        public const string PostUploadImage = "POST upload/image";
    }

    /// <summary>URL schemes for deriving the websocket address from the HTTP base url.</summary>
    private static class Scheme
    {
        public const string Https = "https://";
        public const string Wss = "wss://";
        public const string Http = "http://";
        public const string Ws = "ws://";
    }

    /// <summary>ComfyUI /upload/image multipart form field names and values.</summary>
    private static class UploadForm
    {
        public const string ImageField = "image";
        public const string OverwriteField = "overwrite";
        public const string TypeField = "type";
        public const string OverwriteValue = "true";
        public const string InputTypeValue = "input";
    }

    /// <summary>Fixed per-role upload filenames for edit source / mask / last-frame / source-video.</summary>
    private static class UploadName
    {
        public const string EditSource = "forgemcp_edit_src.png";
        public const string EditMask = "forgemcp_edit_mask.png";
        public const string EditLast = "forgemcp_edit_last.png";
        public const string EditSourceVideo = "forgemcp_edit_src.mp4";
    }

    /// <summary>The custom-node gate header ComfyUI's imagegen_gate reads.</summary>
    private static class Header
    {
        public const string GateToken = "X-ImageGen-Token";
    }

    /// <summary>MIME types for bytes uploaded to ComfyUI.</summary>
    private static class Mime
    {
        public const string Png = "image/png";
        public const string Mp4 = "video/mp4";
    }

    /// <summary>Separators for user-facing text.</summary>
    private static class Separator
    {
        public const string Notice = "\n";
    }

    /// <summary>ComfyUI model folder names.</summary>
    private static class Folder
    {
        /// <summary>The ComfyUI model folder that holds the SeedVR2 upscaler weights.</summary>
        public const string SeedVr2 = "seedvr2";
    }

    /// <summary>Construct the ComfyUI adapter.</summary>
    public ComfyClient(IHttpClientFactory httpFactory, IComfyEndpoint endpoint, WorkflowCatalog catalog, WorkflowRegistry registry, IMediaProcessor media, ISnapshot<ComfyFilesByKind> presentFiles, ILogger<ComfyClient> logger)
    {
        _httpFactory = httpFactory;
        _endpoint = endpoint;
        _catalog = catalog;
        _registry = registry;
        _media = media;
        _presentFiles = presentFiles;
        _logger = logger;
    }

    /// <summary>Where the renderer is, right now, without a trailing slash.</summary>
    private string BaseUrl
    {
        get
        {
            _ = Http;
            return _baseUrl;
        }
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
            string? url = _endpoint.BaseUrl;
            string token = _endpoint.GateToken;
            if (string.IsNullOrWhiteSpace(url))
            {
                throw new InvalidOperationException(
                    "The renderer's address is not configured. Set it on the settings page (ComfyUI:BaseUrl).");
            }

            url = url.TrimEnd('/');

            lock (_clientLock)
            {
                if (field is not null && url == _baseUrl && token == _gateToken)
                {
                    return field;
                }

                HttpClient? replaced = field;
                HttpClient http = _httpFactory.CreateClient();
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
                http.DefaultRequestHeaders.Add(Header.GateToken, token.Length == 0 ? ComfyGateDefaults.GateToken : token);

                field = http;
                _baseUrl = url;
                _gateToken = token;
                replaced?.Dispose();
                return field;
            }
        }
    }

    #region machine probes

    /// <summary>
    /// Which loader node/input each slot kind draws its file list from. Matching and binding are scoped by kind, so
    /// a checkpoint slot is never offered a VAE — merging the lists into one flat set would let a VAE and
    /// a checkpoint sharing a filename satisfy each other's presence check.
    ///
    /// <para>A kind may draw from SEVERAL nodes, and a diffusion slot draws from both the safetensors and the GGUF
    /// loader. Quantisation is a property of the file on the disk, not of the slot: offering only one of the two
    /// lists would force the catalogue to carry a second slot — and then a second workflow — per precision.
    /// Which node ends up in the graph is decided from the bound filename (<see cref="ComfyGraph.DiffusionLoader"/>),
    /// exactly as the text encoders below have always done across their four loaders.</para>
    /// </summary>
    private static readonly (RequirementKind Kind, string Node, string Input)[] LoaderInputs =
    [
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
        // DreamOmni2's self-contained pipeline picks its diffusers base (the FLUX.1-Kontext family) through this
        // combo input, which lists the diffusers directories on disk — so a diffusers slot draws exactly that set.
        // The third element is the NODE's input NAME (its own contract), a literal exactly like "ckpt_name"/"model"
        // above — deliberately not WorkflowParamKeys.BaseModel, the app param key it happens to share a spelling with.
        (RequirementKind.Diffusers, ComfyNodeTypes.RunningHubDreamOmni2EditPipeline, "base_model"),
    ];

    /// <summary>
    /// What each slot kind can be filled with on this machine. A kind absent from the result has no loader in this
    /// ComfyUI build, which is itself the gate for the custom-node-dependent workflows.
    /// </summary>
    public async Task<IReadOnlyDictionary<RequirementKind, IReadOnlyList<string>>> GetPresentFilesByKindAsync(
        CancellationToken ct = default)
    {
        Dictionary<RequirementKind, List<string>> byKind = [];
        foreach (IGrouping<RequirementKind, (RequirementKind Kind, string Node, string Input)> group in LoaderInputs.GroupBy(l => l.Kind))
        {
            HashSet<string> files = await ReadLoaderFilesAsync([.. group.Select(g => (g.Node, g.Input))], ct);
            if (files.Count == 0)
            {
                continue;
            }

            // Some packs publish the models they SUPPORT rather than the files present. SeedVR2 ships a registry
            // of ten builds with their hashes and offers all ten whether or not any are downloaded. Binding one
            // of those makes a slot read satisfied and its workflows
            // read ready, and the failure only arrives at render — or, worse, the pack fetches the model itself.
            // ComfyUI can say what is actually in the folder, so where a kind names one, the offer is narrowed
            // to the intersection.
            if (FolderForKind.TryGetValue(group.Key, out string? folder))
            {
                HashSet<string> onDisk = await ReadFolderFilesAsync(folder, ct);
                if (onDisk.Count > 0)
                {
                    files.IntersectWith(onDisk);
                }
            }

            if (files.Count == 0)
            {
                continue;
            }

            if (!byKind.TryGetValue(group.Key, out List<string>? list))
            {
                byKind[group.Key] = list = [];
            }

            list.AddRange(files);
        }

        if (byKind.Count == 0)
        {
            throw new HttpRequestException("ComfyUI returned no models — is it running with the model paths configured?");
        }

        // A HuggingFace-sharded model lists one file per shard, none loadable alone; collapse each set to the single
        // folder/index entry a loader actually consumes before the picker ever sees the list (issue #184).
        return byKind.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<string>)[.. HuggingFaceShards.Collapse(kv.Value).Distinct(StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>ComfyUI's on-disk model roots by category, from <c>/internal/folder_paths</c> — e.g. "loras",
    /// "checkpoints", "diffusion_models" → the absolute directories it searches (the first is primary; extra roots come
    /// from <c>extra_model_paths.yaml</c>). Used to locate a file on THIS disk for header inspection. Empty when the
    /// endpoint is absent (older build) or the renderer is another machine, which the caller reads as "cannot resolve".</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetFolderPathsAsync(CancellationToken ct = default)
    {
        Dictionary<string, IReadOnlyList<string>> result = new(StringComparer.OrdinalIgnoreCase);
        using HttpResponseMessage resp = await Http.GetAsync(Endpoint.FolderPaths, ct);
        if (!resp.IsSuccessStatusCode)
        {
            return result;
        }

        using JsonDocument doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            List<string> paths = [];
            foreach (JsonElement e in prop.Value.EnumerateArray())
            {
                if (e.GetString() is { Length: > 0 } p)
                {
                    paths.Add(p);
                }
            }

            if (paths.Count > 0)
            {
                result[prop.Name] = paths;
            }
        }

        return result;
    }

    /// <summary>
    /// Read the filename choices from each (node, input-key) pair's object_info. A node absent from this build, or
    /// carrying no such input, is a normal outcome and is SKIPPED — every one of those is detected by asking rather
    /// than by catching, so no exception has to be interpreted.
    /// <para>Nothing here is swallowed. Running under a blanket catch would let a dropped connection or a
    /// response ComfyUI had garbled read as "that loader offers no files" — and since the catalog presence-gates on
    /// exactly these names, every configuration behind that loader would quietly disappear from the model picker, with
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
        [RequirementKind.SeedVr2] = Folder.SeedVr2,
    };

    /// <summary>
    /// What is actually in a ComfyUI model folder. Empty when the build has no such endpoint or no such folder,
    /// which is treated as "cannot narrow" rather than "nothing is there" — losing a real file would hide a
    /// workflow, and that is the worse mistake of the two.
    /// </summary>
    private async Task<HashSet<string>> ReadFolderFilesAsync(string folder, CancellationToken ct)
    {
        HashSet<string> files = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            using HttpResponseMessage resp = await Http.GetAsync($"models/{folder}", ct);
            if (!resp.IsSuccessStatusCode)
            {
                return files;
            }

            using JsonDocument doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return files;
            }

            foreach (JsonElement e in doc.RootElement.EnumerateArray())
            {
                if (e.GetString() is { Length: > 0 } f)
                {
                    _ = files.Add(f);
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            // A real transport/parse failure — NOT the "older build / folder absent" case, which is the non-2xx return
            // above and never reaches here. The kind's list is left un-narrowed rather than failing the whole probe, but
            // the failure is logged: a narrowing that silently stopped (letting a not-present model look bindable) must
            // be diagnosable, not invisible. Cancellation is no longer caught here, so it propagates as it should.
            _logger.LogWarning(ex, "ComfyUI models/{Folder} could not be read; that kind's list is not narrowed to on-disk files.", folder);
        }

        return files;
    }

    private async Task<HashSet<string>> ReadLoaderFilesAsync((string node, string key)[] pairs, CancellationToken ct)
    {
        HashSet<string> files = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string? node, string? key) in pairs)
        {
            using HttpResponseMessage resp = await Http.GetAsync($"object_info/{node}", ct);
            if (!resp.IsSuccessStatusCode)
            {
                continue;                                  // node not in this build
            }

            using JsonDocument doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty(node, out JsonElement ne))
            {
                continue;           // ditto
            }

            if (!ne.TryGetProperty(Field.Input, out JsonElement input)
                || !input.TryGetProperty(Field.Required, out JsonElement required)
                || !required.TryGetProperty(key, out JsonElement keyEl))
            {
                continue;             // node has no such input
            }

            foreach (JsonElement n in ComboOptions(keyEl))
            {
                if (n.GetString() is { Length: > 0 } f)
                {
                    _ = files.Add(f);
                }
            }
        }

        return files;
    }

    /// <summary>
    /// Which of <paramref name="nodes"/> this ComfyUI has registered.
    ///
    /// <para>Presence-gating is otherwise entirely file-based, and that only works for a pack whose nodes LOAD
    /// something: if a loader is gone its filenames are gone, and every configuration behind it disappears. A pack
    /// whose node loads nothing — <c>AnimaLLLiteApply</c> patches a model it is handed — contributes no filenames,
    /// so nothing about it can be inferred from the file lists and a workflow needing it would look perfectly ready
    /// right up until submit fails on an unregistered node.</para>
    /// </summary>
    public async Task<IReadOnlySet<string>> GetPresentNodesAsync(IEnumerable<string> nodes, CancellationToken ct = default)
    {
        HashSet<string> present = new(StringComparer.Ordinal);
        foreach (string? node in nodes.Distinct(StringComparer.Ordinal))
        {
            using HttpResponseMessage resp = await Http.GetAsync($"object_info/{node}", ct);
            if (!resp.IsSuccessStatusCode)
            {
                continue;                                  // not in this build
            }

            using JsonDocument doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            // ComfyUI answers 200 with {} for a node it does not know, so the body is the actual test.
            if (doc.RootElement.TryGetProperty(node, out _))
            {
                _ = present.Add(node);
            }
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
        if (keyEl.ValueKind != JsonValueKind.Array || keyEl.GetArrayLength() == 0)
        {
            return [];
        }

        if (keyEl[0].ValueKind == JsonValueKind.Array)
        {
            return keyEl[0].EnumerateArray();
        }

        if (keyEl.GetArrayLength() > 1 && keyEl[1].ValueKind == JsonValueKind.Object
            && keyEl[1].TryGetProperty(Field.Options, out JsonElement opts) && opts.ValueKind == JsonValueKind.Array)
        {
            return opts.EnumerateArray();
        }

        return [];
    }

    /// <summary>
    /// Total VRAM (MB) from ComfyUI's /system_stats (max across devices). Stable for the process — cached once on
    /// success.
    /// <para>Null means one specific thing: this backend answered, and its answer does not report device VRAM. That
    /// is a real state (an older build) and the catalog reads it as "cannot VRAM-gate", offering everything. A
    /// transport or parse FAILURE is not that state and does not borrow its return value — it throws. Collapsing
    /// the two would turn a hiccup into a silent claim that the box's VRAM is unknown, so configurations this 24 GB
    /// card cannot load would be listed as available and fail later at the GPU instead of being filtered out here.</para>
    /// </summary>
    public async Task<long?> GetTotalVramMbAsync(CancellationToken ct = default)
    {
        if (_vramProbed)
        {
            return _vramMb;
        }

        using HttpResponseMessage resp = await Http.GetAsync(Endpoint.SystemStats, ct);
        await EnsureOk(resp, Op.GetSystemStats);
        using JsonDocument doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty(Field.Devices, out JsonElement devs) || devs.ValueKind != JsonValueKind.Array || devs.GetArrayLength() == 0)
        {
            return null;
        }

        long maxTotal = 0;
        foreach (JsonElement d in devs.EnumerateArray())
        {
            if (d.TryGetProperty(Field.VramTotal, out JsonElement v) && v.ValueKind == JsonValueKind.Number)
            {
                maxTotal = Math.Max(maxTotal, v.GetInt64());
            }
        }

        if (maxTotal <= 0)
        {
            return null;
        }

        _vramMb = maxTotal / (1024 * 1024);
        _vramProbed = true;
        return _vramMb;
    }

    #endregion

    #region async surface: submit (build workflow + POST /prompt, return prompt_id)

    /// <summary>Resolve the configuration + workflow, build the generate graph, POST it to <c>/prompt</c> under this
    /// client's <c>client_id</c>, and return the <c>prompt_id</c> WITHOUT polling.</summary>
    public async Task<SubmitResult> SubmitGenerateAsync(string prompt, string? negativePrompt, string? configId, string? aspect,
        IReadOnlyDictionary<string, JsonElement>? overrides, IReadOnlyList<LoraSelection>? loras, CancellationToken ct)
    {
        (WorkflowConfiguration? cfg, IWorkflow? wf) = ResolveGenerate(configId);
        Dictionary<string, object?> dict = MergeParamsDict(wf, cfg, overrides);
        ResolvedRequirements resolved = _catalog.Resolve(cfg);
        _ = NormalizeAndValidate(wf, cfg, dict,
            new NormalizeContext { Requirements = resolved, AtSubmit = true });   // submit pass (no source image for generate)
        SubmissionCommon common = ParamsCodec.Deserialize<SubmissionCommon>(dict);
        (string? pos, string? neg) = ApplyGenPromptRules(common, prompt, negativePrompt);
        pos = PromptTemplates.Render(common.PromptTemplate, pos, cfg.Id);
        IReadOnlyList<LoraSelection> loraStack = await ValidateLorasAsync(loras, ct);
        string normAspect = ComfyGraph.NormalizeAspect(aspect);
        WorkflowInputs inputs = new() { Positive = pos, Negative = neg, Aspect = normAspect, Loras = loraStack };
        // The RESOLVED render size (exactly what Build sizes the latent to) via the coupled W/H/M snap (#186): when a
        // megapixels control is exposed the size is the W/H ratio scaled to that budget and ordinarily clamped to the
        // envelope; otherwise it's the aspect-map/flat-W/H size. The workflow setting is the sole escape hatch for an
        // image generator; video generation retains its trained size contract.
        ModelResolution? env = resolved.Resolution ?? wf.ResolutionEnvelope;
        bool allowUntrainedResolution = wf.Media == WorkflowMedia.Image && resolved.AllowUntrainedResolution;
        (int ew, int eh) = RenderSizing.Resolve(
            common.Dims(normAspect), common.Megapixels, env, allowUntrainedResolution);
        ResolutionGuard.EnsureAllowed(env, ew, eh, allowUntrainedResolution);
        ComfyWorkflowGraph graph = wf.Build(dict, resolved, inputs);
        // ETA signature: the same resolved render size + the EtaVariable time drivers, from the merged/normalized
        // values the graph was built from.
        EtaSignature eta = new(ew, eh, EtaInt(wf, common.Steps, WorkflowParamKeys.Steps), EtaInt(wf, common.Length, WorkflowParamKeys.Length));
        return new SubmitResult(await SubmitAsync(graph, ct), eta, pos, RenderModelManifestBuilder.Build(dict, resolved),
            Dimensions(wf, null, null, ew, eh));
    }

    /// <summary>The value of an EtaVariable-marked int param (a render-time driver) for the ETA signature, or null when
    /// this workflow does NOT declare the key a time driver — so an unmarked workflow contributes no param signature and
    /// its ETA falls back to the flat per-model average. <paramref name="value"/> is the typed value off
    /// <see cref="SubmissionCommon"/> (null → 0, matching the old absent-key default when the key IS a driver).</summary>
    private static int? EtaInt(IWorkflow wf, int? value, string key) =>
        wf.Schema.Any(s => s.Key == key && s.EtaVariable) ? (value ?? 0) : null;

    /// <inheritdoc/>
    public void InvalidatePresentFiles() => _presentFiles.Invalidate();

    /// <summary>Validate a user LoRA stack against the LoRAs ComfyUI actually offers, failing fast on any unknown name
    /// (never silently dropping one). Skips the backend probe entirely when the stack is empty — the common case.</summary>
    private async Task<IReadOnlyList<LoraSelection>> ValidateLorasAsync(IReadOnlyList<LoraSelection>? loras, CancellationToken ct)
    {
        if (loras is not { Count: > 0 })
        {
            return [];
        }

        // Read the cached capability sweep, not a live probe: the snapshot is flushed on restart/patch/refresh and by
        // the directory watcher (#198), so the LoRA list here is current without a per-submit ComfyUI round trip. An
        // API caller naming a LoRA newer than the last rebuild gets the fail-fast refusal below until a flush lands
        // (the UI picker offers names from this same snapshot, so it cannot hit that).
        ComfyFilesByKind present = await _presentFiles.GetAsync(ct);
        HashSet<string> available = new(present.ForKind(RequirementKind.Lora), StringComparer.OrdinalIgnoreCase);
        foreach (LoraSelection lo in loras)
        {
            if (string.IsNullOrWhiteSpace(lo.Name) || !available.Contains(lo.Name))
            {
                throw new RenderValidationException($"Unknown LoRA '{lo.Name}' — it is not available on this machine.");
            }
        }

        return loras;
    }

    /// <summary>Upload the source PNG (and any references) to ComfyUI's input folder, build the configuration's edit
    /// graph, POST it to <c>/prompt</c>, and return the <c>prompt_id</c> WITHOUT polling.</summary>
    public async Task<SubmitResult> SubmitEditAsync(byte[] sourcePng, string instruction, string? negativePrompt, string? configId,
        IReadOnlyList<ReferenceUpload>? references, IReadOnlyDictionary<string, JsonElement>? overrides, byte[]? maskPng = null,
        byte[]? lastFramePng = null, CancellationToken ct = default)
    {
        if (sourcePng is null || sourcePng.Length == 0)
        {
            throw new RenderValidationException("A source image is required for editing.");
        }

        (WorkflowConfiguration? cfg, IWorkflow? wf) = ResolveEdit(configId);
        // An empty instruction is valid: pixel-quantize ignores the prompt entirely, the pixelize workflows fall
        // back to their own style_prompt, and editors with a non-blank conditioning default handle it too.

        // Video-to-video: the source is a CLIP, not a still. Upload it as a real video
        // file ComfyUI's LoadVideo can decode — transcoding our animated-webp clips to mp4 first (PyAV/ffmpeg can't
        // multi-frame-decode animated webp) and passing mp4/webm uploads through as-is — and build with SourceVideoName.
        if (wf.SourceMedia == WorkflowMedia.Video)
        {
            string videoName = await UploadSourceVideoAsync(sourcePng, ct);
            Dictionary<string, object?> dict0 = MergeParamsDict(wf, cfg, overrides);
            ResolvedRequirements resolved0 = _catalog.Resolve(cfg);
            _ = NormalizeAndValidate(wf, cfg, dict0,
                new NormalizeContext { Requirements = resolved0, AtSubmit = true });
            SubmissionCommon common0 = ParamsCodec.Deserialize<SubmissionCommon>(dict0);
            string renderedInstruction0 = PromptTemplates.Render(common0.PromptTemplate, instruction, cfg.Id);
            WorkflowInputs inputs0 = new() { Positive = renderedInstruction0, SourceVideoName = videoName };
            // V2V: the source clip's pixel size isn't known here (LoadVideo decodes it in ComfyUI), so resolution is
            // left unset; the frame count still drives the time.
            EtaSignature eta0 = new(0, 0, EtaInt(wf, common0.Steps, WorkflowParamKeys.Steps), EtaInt(wf, common0.Length, WorkflowParamKeys.Length));
            return new SubmitResult(await SubmitAsync(wf.Build(dict0, resolved0, inputs0), ct), eta0, renderedInstruction0,
                RenderModelManifestBuilder.Build(dict0, resolved0), Dimensions(wf, null, null, null, null));
        }

        // Distinct filename per role — a fixed name for every upload would make source and references clobber each
        // other in ComfyUI's input folder (overwrite=true). Role-indexed names keep them separate; the job queue
        // serializes ComfyUI work so these fixed names can't race.
        string uploadName = await UploadImageAsync(sourcePng, UploadName.EditSource, ct);
        List<ReferenceInput> refInputs = [];
        if (references is { Count: > 0 })
        {
            int ri = 0;
            foreach (ReferenceUpload r in references)
            {
                if (r.Bytes is { Length: > 0 })
                {
                    // Role-indexed filename per reference, extension matched to its media family (ComfyUI keys the
                    // decode off the extension — an audio clip uploaded as .png would fail to decode) and uploaded WITH
                    // its real content type, not a forced image/png. The kind is carried through so the workflow routes
                    // each reference to the graph input for its family.
                    string name = await UploadFileAsync(r.Bytes, $"forgemcp_edit_ref{ri++}{ReferenceKinds.Extension(r.Kind)}", r.ContentType, ct);
                    refInputs.Add(new ReferenceInput(name, r.Kind));
                }
            }
        }
        // Inpaint: a SEPARATE white-on-black mask image (the source stays pristine — baking the mask into the source's
        // alpha would let PNG premultiplication zero the masked RGB, blacking out the region the model must preserve).
        string? maskName = maskPng is { Length: > 0 } ? await UploadImageAsync(maskPng, UploadName.EditMask, ct) : null;
        // i2v first/last-frame: the last frame the clip should end on, uploaded under its own role name so it never
        // collides with the source/refs/mask. Consumed via WorkflowInputs.EndImageName by workflows that support it.
        string? lastName = lastFramePng is { Length: > 0 } ? await UploadImageAsync(lastFramePng, UploadName.EditLast, ct) : null;
        Dictionary<string, object?> dict = MergeParamsDict(wf, cfg, overrides);
        ImageDimensions srcDim = _media.Identify(sourcePng);   // source dims drive the render-resolution snap (no UI width/height)
        (int srcW, int srcH) = (srcDim.Width, srcDim.Height);
        ResolvedRequirements resolved = _catalog.Resolve(cfg);
        // Submit-pass normalization: snap the render resolution onto a clean ×VRES multiple (deliberate, no notice) now,
        // so Build reads the cached size rather than recomputing it. Source dims + the model's envelope live here.
        _ = NormalizeAndValidate(wf, cfg, dict,
            new NormalizeContext { SourceWidth = srcW, SourceHeight = srcH, Requirements = resolved, AtSubmit = true });
        SubmissionCommon common = ParamsCodec.Deserialize<SubmissionCommon>(dict);
        if (common.SnapResolution)
        {
            _logger.LogInformation("Edit '{Config}': snap_resolution ON, source {W}x{H} — render size snapped to a clean integer ×VRES multiple (or the request fails if it can't).", configId, srcW, srcH);
        }

        string renderedInstruction = PromptTemplates.Render(common.PromptTemplate, instruction, cfg.Id);
        double? editMegapixels = EditQuality.Resolve(wf, dict);
        WorkflowInputs inputs = new() { Positive = renderedInstruction, Negative = negativePrompt, SourceImageName = uploadName, SourceWidth = srcW, SourceHeight = srcH, EditMegapixels = editMegapixels, References = refInputs, MaskImageName = maskName, EndImageName = lastName };
        // ETA signature: the resolution the workflow ACTUALLY renders at (a budget-scaling editor pins the source to a
        // fixed ~MP size, so recording raw srcW/srcH would credit a large upload work it never does), plus the
        // EtaVariable time drivers — Frames (length) dominates for i2v.
        (int etaW, int etaH) = wf.EtaRenderSize(dict, resolved, srcW, srcH, editMegapixels);
        // Raw-source editors have no graph-level sizing safety net: enforce the model's full declared envelope before
        // posting. Explicit normalizers accept arbitrary uploads and validate their own aspect-preserving working
        // canvas; generation-only minimum-side rectangles must not reject those source images.
        ModelResolution? env = resolved.Resolution ?? wf.ResolutionEnvelope;
        ResolutionGuard.EnsureEditWithin(env, etaW, etaH, wf.NormalizesSourceResolution);
        ComfyWorkflowGraph graph = wf.Build(dict, resolved, inputs);
        EtaSignature eta = new(etaW, etaH, EtaInt(wf, common.Steps, WorkflowParamKeys.Steps), EtaInt(wf, common.Length, WorkflowParamKeys.Length));
        return new SubmitResult(await SubmitAsync(graph, ct), eta, renderedInstruction,
            RenderModelManifestBuilder.Build(dict, resolved), Dimensions(wf, srcW, srcH, etaW, etaH));
    }

    private static RenderDimensions Dimensions(IWorkflow wf, int? inputWidth, int? inputHeight, int? workingWidth, int? workingHeight) => new()
    {
        Policy = wf.OutputSizePolicy,
        Input = inputWidth > 0 && inputHeight > 0 ? new PixelDimensions(inputWidth.Value, inputHeight.Value) : null,
        Working = workingWidth > 0 && workingHeight > 0 ? new PixelDimensions(workingWidth.Value, workingHeight.Value) : null,
    };

    private (WorkflowConfiguration cfg, IWorkflow wf) ResolveGenerate(string? configId)
    {
        if (string.IsNullOrWhiteSpace(configId))
        {
            throw new RenderValidationException("A workflow configuration is required. Call list_models and pass one.");
        }

        WorkflowConfiguration cfg = _catalog.FindConfig(configId)
                  ?? throw new RenderValidationException($"Unknown workflow configuration '{configId}'. Call list_models for valid ids.");
        IWorkflow wf = _registry.Find(cfg.WorkflowName)
                 ?? throw new RenderValidationException($"Workflow '{cfg.WorkflowName}' for configuration '{configId}' is not registered.");
        if (wf.Kind != WorkflowKind.Generate)
        {
            throw new RenderValidationException($"Configuration '{configId}' is an edit workflow, not a generate one.");
        }

        return (cfg, wf);
    }

    private (WorkflowConfiguration cfg, IWorkflow wf) ResolveEdit(string? configId)
    {
        if (string.IsNullOrWhiteSpace(configId))
        {
            throw new RenderValidationException("An edit workflow configuration is required (one whose can_edit is true).");
        }

        WorkflowConfiguration cfg = _catalog.FindConfig(configId)
                  ?? throw new RenderValidationException($"Unknown workflow configuration '{configId}'. Call list_models for valid ids.");
        IWorkflow wf = _registry.Find(cfg.WorkflowName)
                 ?? throw new RenderValidationException($"Workflow '{cfg.WorkflowName}' for configuration '{configId}' is not registered.");
        if (wf.Kind == WorkflowKind.Generate)
        {
            throw new RenderValidationException($"Configuration '{configId}' is not an editing configuration. Pick one whose can_edit is true.");
        }

        return (cfg, wf);
    }

    /// <summary>Overlay the configuration's settings layer (then any request overrides) on the workflow's schema
    /// defaults into a mutable bag (so a normalization pass can clamp values before it's deserialized to a typed DTO).</summary>
    private Dictionary<string, object?> MergeParamsDict(IWorkflow wf, WorkflowConfiguration cfg, IReadOnlyDictionary<string, JsonElement>? overrides)
    {
        Dictionary<string, object?> v = new(StringComparer.OrdinalIgnoreCase);
        foreach (ParamSpec spec in wf.Schema)
        {
            if (spec.Default is not null)
            {
                v[spec.Key] = spec.Default;
            }
        }

        foreach (KeyValuePair<string, ConfigParam> kv in cfg.Params)
        {
            v[kv.Key] = kv.Value.Value;
        }
        // Then THIS MACHINE's overrides, from the workflow's settings page: the render size for each aspect, the
        // step count, whatever the configuration exposes. Between the shipped configuration and the request,
        // because that is what they are — the operator's answer for this box, which a caller may still override
        // per generation unless the parameter is locked.
        foreach (KeyValuePair<string, JsonElement> kv in _catalog.ParamOverridesFor(cfg.Id))
        {
            v[kv.Key] = kv.Value;
        }
        // A locked param is a baked value the caller cannot override — its request value is dropped so the
        // configuration's setting is enforced on every generation. Exposed AND hidden params both overlay: visibility
        // is a UI concern (a hidden param is merely un-surfaced by default), lockability is this gate.
        if (overrides is not null)
        {
            foreach (KeyValuePair<string, JsonElement> kv in overrides)
            {
                if (!(cfg.Params.TryGetValue(kv.Key, out ConfigParam? cp) && cp.Visibility == ParamVisibility.Locked))
                {
                    v[kv.Key] = kv.Value;
                }
            }

            // An explicit width+height (the composer's Custom size) supersedes the configuration's aspect map for THIS
            // request: drop the aspect entry so Dims() resolves to the flat width/height just supplied instead of the
            // aspect the request nominally names. Scoped to a request that sent BOTH sides — a normal aspect submission,
            // which carries no width/height, is untouched. An unsupported custom size was snapped at enqueue
            // (NormalizeForQueue, #212) and is re-checked here on the render path (ResolutionGuard), so this can't
            // smuggle an unsupported size through.
            if (overrides.ContainsKey(WorkflowParamKeys.Width) && overrides.ContainsKey(WorkflowParamKeys.Height))
            {
                _ = v.Remove(WorkflowParamKeys.Aspect);

                // With the per-workflow untrained-resolution policy enabled, typed Custom W/H are authoritative.
                // The composer's two-decimal megapixel value can otherwise rescale an odd/off-grid pair. Drop that
                // budget only for a genuine Custom pair; clicked aspect dimensions keep their ordinary behavior.
                IReadOnlyDictionary<string, JsonElement> machine = _catalog.ParamOverridesFor(cfg.Id);
                if (wf.Kind == WorkflowKind.Generate
                    && wf.Media == WorkflowMedia.Image
                    && WorkflowResolutionPolicy.IsEnabled(machine)
                    && RequestSize.TryExplicit(overrides, out int customW, out int customH)
                    && !RequestSize.MatchesAspectDims(cfg, machine, customW, customH))
                {
                    _ = v.Remove(WorkflowParamKeys.Megapixels);
                }
            }
        }

        // A parameter declared IsModelRef holds a SLOT ID; substitute the file this machine has bound to it. Carrying
        // filenames directly here would put a second set of one person's filenames in the configurations, outside the
        // binding system and beyond a user's reach. One implementation, shared with the test suite's merge — see
        // WorkflowCatalog.ResolveModelRefs.
        _catalog.ResolveModelRefs(wf, cfg.Id, v);
        return v;
    }

    /// <summary>Run the workflow's normalizer, then enforce the range selected on this workflow's settings page.
    /// Normalization comes first because video duration is submitted in seconds and becomes the configured frame
    /// parameter here; validating the pre-normalized bag would miss that user-facing alias.</summary>
    private List<string> NormalizeAndValidate(
        IWorkflow workflow,
        WorkflowConfiguration config,
        Dictionary<string, object?> values,
        NormalizeContext context)
    {
        IReadOnlyDictionary<string, JsonElement> machine = _catalog.ParamOverridesFor(config.Id);
        bool frameCountPolicyApplies = workflow.Media == WorkflowMedia.Video
            && config.Params.ContainsKey(WorkflowParamKeys.Length);
        bool allowUntrainedFrameCounts = frameCountPolicyApplies
            && WorkflowFrameCountPolicy.IsEnabled(machine);
        NormalizeContext effectiveContext = new()
        {
            SourceWidth = context.SourceWidth,
            SourceHeight = context.SourceHeight,
            Requirements = context.Requirements,
            AtSubmit = context.AtSubmit,
            AllowUntrainedFrameCounts = allowUntrainedFrameCounts,
        };
        List<string> notices = [.. workflow.Normalize(values, effectiveContext)];
        WorkflowRangeOverridePolicy.Validate(
            config, machine, values, allowUntrainedFrameCounts, frameCountPolicyApplies);
        if (allowUntrainedFrameCounts
            && WorkflowFrameCountPolicy.WarningFor(config, workflow.FrameRule, values) is { } warning)
        {
            notices.Add(warning);
        }

        return notices;
    }

    /// <summary>Pre-queue parameter normalization (synchronous; NO ComfyUI call). Resolves the config + workflow,
    /// overlays params, lets the workflow's <see cref="IWorkflow.Normalize"/> snap any out-of-range input (today:
    /// a stepped frame count) onto a model-valid value, and snaps an unsupported custom render size onto the model's
    /// envelope (#212). Returns the corrected override set (the changed keys folded
    /// back in, so the worker's <see cref="MergeParams"/> picks them up) plus a single user-facing notice (newline-
    /// joined if several). The <see cref="JobQueue"/> calls this as it creates each slot — so the corrected value is
    /// what gets built and the notice is on the slot before its placeholder card renders. Returns the overrides
    /// unchanged with a null notice when nothing changed, and no-ops on an unknown/mis-kinded config (the worker
    /// surfaces that real error at submit, not here).</summary>
    public QueueNormalizationResult NormalizeForQueue(
        string? configId, RenderKind kind, IReadOnlyDictionary<string, JsonElement>? overrides)
    {
        WorkflowConfiguration cfg;
        IWorkflow wf;
        try
        {
            (cfg, wf) = kind == RenderKind.Edit ? ResolveEdit(configId) : ResolveGenerate(configId);
        }
        catch (RenderValidationException)
        {
            return new QueueNormalizationResult(null, null);
        }

        Dictionary<string, object?> merged = MergeParamsDict(wf, cfg, overrides);
        Dictionary<string, object?> before = new(merged, StringComparer.OrdinalIgnoreCase);
        List<string> notices = [.. NormalizeAndValidate(wf, cfg, merged, NormalizeContext.Empty)];   // enqueue pass: params only (frame snap), no source/requirements

        ResolvedRequirements resolved = _catalog.Resolve(cfg);
        ModelResolution? envelope = resolved.Resolution ?? wf.ResolutionEnvelope;
        IReadOnlyDictionary<string, JsonElement> machine = _catalog.ParamOverridesFor(cfg.Id);
        if (kind == RenderKind.Generate && wf.Media == WorkflowMedia.Image
            && RequestSize.TryExplicit(overrides, out int reqW, out int reqH))
        {
            ResolutionGuard.EnsurePositive(reqW, reqH);
            if (resolved.AllowUntrainedResolution)
            {
                if (WorkflowResolutionPolicy.WarningFor(envelope, reqW, reqH) is { } warning)
                {
                    notices.Add(warning);
                }
            }
            // #212: a genuine CUSTOM size ordinarily snaps to the nearest trained size. In multi-model fan-out,
            // each disabled workflow normalizes its own slot while an opted-in workflow keeps the exact pair above.
            else if (!RequestSize.MatchesAspectDims(cfg, machine, reqW, reqH)
                && ResolutionGuard.SnapToSupported(envelope, reqW, reqH) is { } snap)
            {
                merged[WorkflowParamKeys.Width] = snap.W;
                merged[WorkflowParamKeys.Height] = snap.H;
                // Left at the original area, megapixels would rescale the corrected pair back toward the bad size.
                if (overrides is not null && overrides.ContainsKey(WorkflowParamKeys.Megapixels))
                {
                    merged[WorkflowParamKeys.Megapixels] = Math.Round(snap.W * (double)snap.H / (1024 * 1024), 2);
                }

                notices.Add(snap.Notice);
            }
        }

        if (notices.Count == 0)
        {
            return new QueueNormalizationResult(null, null);   // nothing changed → caller keeps the original overrides
        }

        Dictionary<string, JsonElement> outv = overrides is null
            ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(overrides, StringComparer.Ordinal);
        foreach (string k in merged.Keys)
        {
            if (!before.TryGetValue(k, out object? ov) || !Equals(ov, merged[k]))
            {
                outv[k] = JsonSerializer.SerializeToElement(merged[k]);
            }
        }

        return new QueueNormalizationResult(outv, string.Join(Separator.Notice, notices));
    }

    /// Generation prompt rules: prepend the model's required tag prefix,
    /// and suppress the negative for distilled models (cfg<=1) or models declaring no negative support.
    private static (string pos, string? neg) ApplyGenPromptRules(SubmissionCommon p, string prompt, string? negative)
    {
        string prefix = string.IsNullOrWhiteSpace(p.RequiredPrefix) ? "" : p.RequiredPrefix.TrimEnd().TrimEnd(',').TrimEnd() + ", ";
        string pos = prefix + prompt;
        // Negative = the model's default (config `negative`, else the shared DefaultNegative) with the user's UI
        // negative APPENDED — never replaced. Suppressed entirely for distilled (cfg<=1) or negative-less models.
        bool negOk = (p.Cfg ?? 7) > 1 && p.NegativeSupported;
        string neg = negOk ? ComfyGraph.ComposeNegative(p.Negative, negative) : "";
        return (pos, neg);
    }

    #endregion

    #region HTTP plumbing

    /// <summary>One non-looping <c>/history/{promptId}</c> check. Transport and HTTP availability failures are explicit
    /// retryable outcomes; a prompt execution error remains a terminal validation exception.</summary>
    public async Task<RenderPollResult> PollResultAsync(string promptId, CancellationToken ct = default)
    {
        try
        {
            using HttpResponseMessage hresp = await Http.GetAsync($"history/{promptId}", ct);
            if (!hresp.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "ComfyUI GET history/{PromptId} answered {Status}; history is unavailable and the prompt remains nonterminal.",
                    promptId, (int)hresp.StatusCode);
                return RenderPollResult.Unavailable();
            }

            using JsonDocument hdoc = await JsonDocument.ParseAsync(await hresp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (!hdoc.RootElement.TryGetProperty(promptId, out JsonElement entry))
            {
                return RenderPollResult.NotReady();
            }

            if (entry.TryGetProperty(Field.Status, out JsonElement status) &&
                status.TryGetProperty(Field.StatusStr, out JsonElement ss) && ss.GetString() == Field.Error)
            {
                throw new RenderValidationException(DescribeComfyError(status, promptId));
            }

            if (!entry.TryGetProperty(Field.Outputs, out JsonElement outputs))
            {
                return RenderPollResult.NotReady();
            }

            // Scan ALL output nodes: the produced clip/image is the first node carrying `images` (SaveAnimatedWEBP /
            // SaveImage). The pixel-quantize (fp) node additionally surfaces its derived `palette` (inline #RRGGBB array)
            // and native-res `lossless_frames` (saved-PNG refs) under distinct keys — collect those too so Forge can
            // persist them next to the produced image. Distinct keys => no collision with the result image.
            JsonElement? resultImg = null;
            string? paletteJson = null;
            string? frequenciesJson = null;
            List<byte[]>? losslessFrames = null;
            foreach (JsonProperty node in outputs.EnumerateObject())
            {
                JsonElement v = node.Value;
                if (resultImg is null && v.TryGetProperty(Field.Images, out JsonElement images) && images.GetArrayLength() > 0)
                {
                    resultImg = images[0];
                }

                if (paletteJson is null && v.TryGetProperty(Field.Palette, out JsonElement pal) && pal.ValueKind == JsonValueKind.Array)
                {
                    paletteJson = pal.GetRawText();
                }
                // The fp quantize also surfaces its pooled label frequencies (floats, indexed by palette order) — the
                // second global a single-frame replay needs to reproduce the batch's rarity weighting exactly.
                if (frequenciesJson is null && v.TryGetProperty(Field.Frequencies, out JsonElement fq) && fq.ValueKind == JsonValueKind.Array)
                {
                    frequenciesJson = fq.GetRawText();
                }

                if (losslessFrames is null && v.TryGetProperty(Field.LosslessFrames, out JsonElement lf)
                    && lf.ValueKind == JsonValueKind.Array && lf.GetArrayLength() > 0)
                {
                    losslessFrames = new List<byte[]>(lf.GetArrayLength());
                    foreach (JsonElement fr in lf.EnumerateArray())
                    {
                        losslessFrames.Add(await Http.GetByteArrayAsync(ViewUrl(fr), ct));
                    }
                }
            }

            if (resultImg is { } img)
            {
                string file = img.GetProperty(Field.Filename).GetString()
                    ?? throw new JsonException("ComfyUI history image has a null 'filename'.");
                string sub = img.TryGetProperty(Field.Subfolder, out JsonElement sf) ? sf.GetString() ?? "" : "";
                string type = img.TryGetProperty(Field.Type, out JsonElement t) ? t.GetString() ?? "output" : "output";
                byte[] bytes = await Http.GetByteArrayAsync(ViewUrl(file, sub, type), ct);
                return RenderPollResult.Ready(new GeneratedImage(bytes, string.Empty, file, sub, type, paletteJson, losslessFrames, frequenciesJson));
            }

            return RenderPollResult.NotReady();
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex,
                "ComfyUI GET history/{PromptId} failed; history is unavailable and the prompt remains nonterminal.", promptId);
            return RenderPollResult.Unavailable();
        }
    }

    /// <summary>Build a ComfyUI <c>/view</c> url for a saved-output ref (filename/subfolder/type).</summary>
    private static string ViewUrl(JsonElement fileRef) => ViewUrl(
        fileRef.GetProperty(Field.Filename).GetString() ?? throw new JsonException("ComfyUI output ref has a null 'filename'."),
        fileRef.TryGetProperty(Field.Subfolder, out JsonElement s) ? s.GetString() ?? "" : "",
        fileRef.TryGetProperty(Field.Type, out JsonElement t) ? t.GetString() ?? "output" : "output");

    private static string ViewUrl(string file, string sub, string type) =>
        $"view?filename={Uri.EscapeDataString(file)}&subfolder={Uri.EscapeDataString(sub)}&type={type}";

    /// <summary>ComfyUI reports a failed prompt in history <c>status.messages</c> as an
    /// <c>["execution_error", {...}]</c> pair carrying the failing node and the Python exception. Surface the full
    /// exception (type + message) in the thrown message — which the job queue stores verbatim into the slot error and
    /// shows in the UI — and log the full Python traceback server-side (it's too long for the UI line).</summary>
    private string DescribeComfyError(JsonElement status, string promptId)
    {
        if (status.TryGetProperty(Field.Messages, out JsonElement msgs) && msgs.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement m in msgs.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Array || m.GetArrayLength() < 2 || m[0].GetString() != Field.ExecutionError)
                {
                    continue;
                }

                JsonElement p = m[1];
                string? Get(string k)
                {
                    return p.TryGetProperty(k, out JsonElement v) ? v.GetString() : null;
                }

                string? node = Get(Field.NodeType);
                string? nid = Get(Field.NodeId);
                string where = node is null ? "" : $" in {node}{(nid is null ? "" : $" (node {nid})")}";

                _logger.LogError("ComfyUI execution_error (prompt {PromptId}){Where}: {Type}", promptId, where, Get(Field.ExceptionType));

                return $"ComfyUI error{where}: {Get(Field.ExceptionType)}: {Get(Field.ExceptionMessage)}";
            }
        }

        return "ComfyUI reported an error but included no execution_error detail.";
    }

    /// <summary>What ComfyUI currently holds, from <c>GET /queue</c>, with its two lists kept APART:
    /// <c>queue_running</c> is what the GPU is executing, <c>queue_pending</c> is what waits behind it. The split is
    /// the evidence a slot's "running" status is built on — merging them means calling a prompt
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
            using HttpResponseMessage resp = await Http.GetAsync(Endpoint.Queue, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("ComfyUI GET queue answered {Status}; treating the backend queue as unknown.", (int)resp.StatusCode);
                return null;
            }

            using JsonDocument doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return new BackendQueue(PromptIdsIn(doc.RootElement, Field.QueueRunning),
                                    PromptIdsIn(doc.RootElement, Field.QueuePending));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Null here is the ANSWER, not a swallowed failure: "unknown" is a real third state that the poller is
            // built around, and throwing instead would fail a live slot over one blocked socket. The reason is logged:
            // discarding the exception entirely would leave a backend that has been answering garbage for an hour
            // indistinguishable from one that is merely busy, in the logs and everywhere else.
            _logger.LogWarning(ex, "ComfyUI GET queue failed; treating the backend queue as unknown (no prompt is presumed lost).");
            return null;
        }
    }

    /// <summary>The prompt ids in one of <c>/queue</c>'s lists. Each entry is
    /// <c>[number, prompt_id, prompt, extra_data, outputs]</c>; the prompt id is element 1.</summary>
    private static HashSet<string> PromptIdsIn(JsonElement root, string key)
    {
        HashSet<string> ids = new(StringComparer.Ordinal);
        if (!root.TryGetProperty(key, out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return ids;
        }

        foreach (JsonElement entry in arr.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Array && entry.GetArrayLength() > 1
                && entry[1].ValueKind == JsonValueKind.String && entry[1].GetString() is { Length: > 0 } pid)
            {
                _ = ids.Add(pid);
            }
        }

        return ids;
    }

    /// <summary>POST <c>/interrupt</c> (empty body) to cancel the currently-running prompt. THROWS on a transport or
    /// HTTP failure, like every other call here: an interrupt that did not land means the GPU is still rendering work
    /// somebody cancelled, which is exactly the thing an operator needs told. Swallowing the failure would make a
    /// backend that has stopped honouring interrupts look identical to one that is cancelling promptly. Callers
    /// decide whether a failed interrupt should fail their own operation; none of them may discard the reason.</summary>
    public async Task InterruptAsync(CancellationToken ct = default)
    {
        using HttpResponseMessage resp = await Http.PostAsync(Endpoint.Interrupt, new ByteArrayContent([]), ct);
        await EnsureOk(resp, Op.PostInterrupt);
    }

    /// <summary>POST <c>/free</c> asking ComfyUI to unload every loaded model and release its cached VRAM. ComfyUI
    /// takes these as queue FLAGS consumed by its worker BETWEEN prompts, so this never interrupts a render already in
    /// flight — an idle backend frees immediately, a busy one frees when the running prompt finishes. Failures are NOT
    /// swallowed: this is user-initiated, so the caller reports what ComfyUI actually said.</summary>
    public async Task FreeMemoryAsync(CancellationToken ct = default)
    {
        using HttpResponseMessage resp = await Http.PostAsJsonAsync(Endpoint.Free, new { unload_models = true, free_memory = true }, ct);
        await EnsureOk(resp, Op.PostFree);
    }

    /// <summary>POST a built workflow to /prompt under this client's client_id; return the prompt_id (no polling).</summary>
    private async Task<string> SubmitAsync(ComfyWorkflowGraph workflow, CancellationToken ct)
    {
        // The submitted graph is NOT logged. The graph embeds the user's prompt and negative in plaintext, so logging
        // it — even behind an "off by default" toggle, to inspect exactly what reached the model (artist tags,
        // prefixes, paren escaping, weights) — would leave prompts one config flip away from being written to disk
        // permanently. The prompt that produced any image is recoverable from the per-user ENCRYPTED log
        // (Logging:AuditUserPrompts), which is the channel that exists for this; the app log gets nothing.
        using HttpResponseMessage submit = await Http.PostAsJsonAsync(Endpoint.Prompt, new { prompt = workflow, client_id = _clientId }, ct);
        await EnsureOk(submit, Op.PostPrompt);
        using JsonDocument sdoc = await JsonDocument.ParseAsync(await submit.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return sdoc.RootElement.GetProperty(Field.PromptId).GetString()
            ?? throw new JsonException("ComfyUI /prompt response has a null 'prompt_id'.");
    }

    /// <summary>Fetch raw bytes for a legacy image id (a ComfyUI view-ref minted before DB storage) by proxying
    /// <c>/view</c>. A 404/410 is definitive; transport, timeout, and other HTTP failures are retryable inability to answer.</summary>
    public async Task<LegacyImageFetchResult> FetchLegacyImageAsync(string imageId, CancellationToken ct)
    {
        (string? file, string? sub, string? type) = DecodeId(imageId);
        try
        {
            using HttpResponseMessage resp = await Http.GetAsync(ViewUrl(file, sub, type), ct);
            if (resp.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
            {
                return LegacyImageFetchResult.NotFound();
            }

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "ComfyUI legacy image lookup for {ImageId} answered {Status}; treating the renderer as unavailable.",
                    imageId, (int)resp.StatusCode);
                return LegacyImageFetchResult.Unavailable();
            }

            return LegacyImageFetchResult.Found(await resp.Content.ReadAsByteArrayAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex,
                "ComfyUI legacy image lookup for {ImageId} failed; treating the renderer as unavailable.", imageId);
            return LegacyImageFetchResult.Unavailable();
        }
    }

    /// <summary>Decode an image id into a ComfyUI view-ref (filename/subfolder/type). A bare id (no ':') is an output
    /// image; otherwise it is "type:subfolder:filename" split on the first two colons (the filename may contain ':').</summary>
    private static (string filename, string subfolder, string type) DecodeId(string id)
    {
        int firstColon = id.IndexOf(':');
        if (firstColon < 0)
        {
            return (id, "", "output");
        }

        string type = id[..firstColon];
        string rest = id[(firstColon + 1)..];
        int secondColon = rest.IndexOf(':');
        if (secondColon < 0)
        {
            return (rest, "", type);
        }

        return (rest[(secondColon + 1)..], rest[..secondColon], type);
    }

    /// <summary>Connect to ComfyUI's <c>/ws</c> under this client's id and return the open socket, so the API can proxy
    /// progress/preview frames. The upstream carries every client's progress; the caller filters to the owner.</summary>
    public async Task<WebSocket> ConnectProgressSocketAsync(CancellationToken ct)
    {
        string wsUrl = BaseUrl.Replace(Scheme.Https, Scheme.Wss).Replace(Scheme.Http, Scheme.Ws) + "/ws?clientId=" + _clientId;
        ClientWebSocket socket = new();
        await socket.ConnectAsync(new Uri(wsUrl), ct);
        return socket;
    }

    /// <summary>Upload PNG bytes to ComfyUI's input folder (POST /upload/image, multipart); returns the stored filename.</summary>
    private Task<string> UploadImageAsync(byte[] png, string filename, CancellationToken ct) =>
        UploadFileAsync(png, filename, Mime.Png, ct);

    /// <summary>Upload a file (image OR video) to ComfyUI's input folder. ComfyUI's /upload/image route writes whatever
    /// file it's given to the input dir verbatim, so a video posted here lands where <c>LoadVideo</c> can list it; the
    /// stored name (returned) is what the graph references.</summary>
    private async Task<string> UploadFileAsync(byte[] bytes, string filename, string contentType, CancellationToken ct)
    {
        using MultipartFormDataContent form = new();
        ByteArrayContent file = new(bytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        form.Add(file, UploadForm.ImageField, filename);
        form.Add(new StringContent(UploadForm.OverwriteValue), UploadForm.OverwriteField);
        form.Add(new StringContent(UploadForm.InputTypeValue), UploadForm.TypeField);
        using HttpResponseMessage resp = await Http.PostAsync(Endpoint.UploadImage, form, ct);
        await EnsureOk(resp, Op.PostUploadImage);
        using JsonDocument doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc.RootElement.GetProperty(Field.Name).GetString()
            ?? throw new JsonException("ComfyUI upload response has a null 'name'.");
    }

    /// <summary>Upload a video-to-video source CLIP to ComfyUI's input folder so <c>LoadVideo</c> can decode it. Our
    /// generated clips are animated WEBP, which ffmpeg/PyAV only single-frame-decode, so those are transcoded to mp4
    /// first (reusing <see cref="ForgeVideo.WebpToMp4Async"/>); an upload that is already a real container (mp4/webm)
    /// is sent through unchanged. Returns the stored filename. Throws if the bytes aren't a clip we can ingest.</summary>
    private async Task<string> UploadSourceVideoAsync(byte[] bytes, CancellationToken ct)
    {
        if (_media.IsAnimatedWebp(bytes))
        {
            byte[] mp4 = await _media.WebpToMp4Async(bytes, null, ct);
            return await UploadFileAsync(mp4, UploadName.EditSourceVideo, Mime.Mp4, ct);
        }

        (string? ext, string? mime) = DetectVideoContainer(bytes)
            ?? throw new RenderValidationException("The source isn't a video clip this editor can read (expected an animated WEBP, MP4, or WEBM).");
        return await UploadFileAsync(bytes, "forgemcp_edit_src." + ext, mime, ct);
    }

    /// <summary>Sniff a real video container from its header — MP4/MOV (an <c>ftyp</c> box) or Matroska/WEBM (the EBML
    /// magic). Returns (extension, mime) or null when it isn't one of those (e.g. an animated webp, handled separately,
    /// or a still image). Header-only; no decode.</summary>
    private static (string ext, string mime)? DetectVideoContainer(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 12 && b[4] == 'f' && b[5] == 't' && b[6] == 'y' && b[7] == 'p')
        {
            return ("mp4", "video/mp4");
        }

        if (b.Length >= 4 && b[0] == 0x1A && b[1] == 0x45 && b[2] == 0xDF && b[3] == 0xA3)
        {
            return ("webm", "video/webm");
        }

        return null;
    }

    private static async Task EnsureOk(HttpResponseMessage resp, string what)
    {
        if (resp.IsSuccessStatusCode)
        {
            return;
        }

        string body = await resp.Content.ReadAsStringAsync();
        throw new HttpRequestException($"ComfyUI {what} failed: {(int)resp.StatusCode} {resp.ReasonPhrase}\n{body}");
    }

    #endregion
}
