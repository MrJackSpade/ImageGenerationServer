using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ImageGen.Api.Auth;
using ImageGen.Api.Contracts;
using ImageGen.Application.Civitai;
using ImageGen.Application.Images;
using ImageGen.Application.Media;
using ImageGen.Application.Platform;
using ImageGen.Application.Rendering;
using ImageGen.Application.Services;
using ImageGen.Application.Tags;
using ImageGen.Application.Workflows;
using ImageGen.Domain;
using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace ImageGen.Api.Endpoints;

/// <summary>
/// The render (ex-Forge) HTTP surface: workflows/prompting, generate/edit/enqueue + job views, image serving, tags,
/// and the live-progress WebSocket. Mounted under a /forge route group (with auth + UseWebSockets); same-origin.
/// Depends only on Application ports/services + Domain repositories.
/// </summary>
public static class ForgeApi
{
    /// <summary>Origin image URLs are built from (same-origin under the merged app).</summary>
    public const string PublicBase = "/forge";

    /// <summary>Bounds for the `w` (width) parameter on the image and mp4 endpoints. Out-of-range is REFUSED, not
    /// clamped: the response carries `Cache-Control: immutable, max-age=1y` under a key built from the width, so a
    /// silently clamped request would teach the browser to keep the wrong-sized image for a year with nothing in the
    /// response saying the size had been changed.</summary>
    private const int MinWidth = 64;
    private const int MaxWidth = 1024;

    /// <summary>The route path templates for every /forge endpoint, relative to the group's prefix.</summary>
    private static class Routes
    {
        /// <summary><c>GET /healthz</c> — anonymous liveness probe.</summary>
        public const string Healthz = "/healthz";
        /// <summary><c>GET /workflows</c> — the workflow configurations this box can run for the caller.</summary>
        public const string Workflows = "/workflows";
        /// <summary><c>GET /catalog/status</c> — everything this machine can and cannot run, and why.</summary>
        public const string CatalogStatus = "/catalog/status";
        /// <summary><c>GET /loras</c> — the composer's LoRA picker data.</summary>
        public const string Loras = "/loras";
        /// <summary><c>GET /loras/manage</c> — the LoRA manager page's data.</summary>
        public const string LorasManage = "/loras/manage";
        /// <summary><c>POST /loras/meta</c> — the cached-meta poll for LoRA cards still populating.</summary>
        public const string LorasMeta = "/loras/meta";
        /// <summary><c>POST /loras/refresh</c> — forget cached CivitAI data and re-queue a re-fetch.</summary>
        public const string LorasRefresh = "/loras/refresh";
        /// <summary><c>PUT /catalog/binding</c> — point a model slot at a file, or clear it.</summary>
        public const string CatalogBinding = "/catalog/binding";
        /// <summary><c>PUT /catalog/override</c> — set or remove one per-configuration override.</summary>
        public const string CatalogOverride = "/catalog/override";
        /// <summary><c>GET /catalog/config/{id}/settings</c> — a configuration's effective and shipped settings.</summary>
        public const string CatalogConfigSettings = "/catalog/config/{id}/settings";
        /// <summary><c>GET /prompting/{model}</c> — one configuration's prompting guide.</summary>
        public const string PromptingForModel = "/prompting/{model}";
        /// <summary><c>GET /prompting</c> — all configurations' prompting guides.</summary>
        public const string Prompting = "/prompting";
        /// <summary><c>POST /tags</c> — tag/artist autocomplete.</summary>
        public const string Tags = "/tags";
        /// <summary><c>GET /tags/status</c> — the tag catalogue's load state.</summary>
        public const string TagsStatus = "/tags/status";
        /// <summary><c>POST /generate</c> — enqueue a generation.</summary>
        public const string Generate = "/generate";
        /// <summary><c>POST /edit</c> — enqueue an edit.</summary>
        public const string Edit = "/edit";
        /// <summary><c>POST /enqueue</c> — enqueue a batch.</summary>
        public const string Enqueue = "/enqueue";
        /// <summary><c>GET /result/{id}</c> — poll one job in the legacy single-image shape.</summary>
        public const string Result = "/result/{id}";
        /// <summary><c>GET /jobs</c> — the caller's active jobs.</summary>
        public const string Jobs = "/jobs";
        /// <summary><c>GET /queue</c> — a cross-user page of the queue and history.</summary>
        public const string Queue = "/queue";
        /// <summary><c>GET /job/{id}</c> — one job (active or finalized) by id.</summary>
        public const string Job = "/job/{id}";
        /// <summary><c>POST /cancel/{id}</c> — cancel one job.</summary>
        public const string Cancel = "/cancel/{id}";
        /// <summary><c>POST /interrupt</c> — interrupt the running render.</summary>
        public const string Interrupt = "/interrupt";
        /// <summary><c>POST /cancel-all</c> — cancel every active job on the box.</summary>
        public const string CancelAll = "/cancel-all";
        /// <summary><c>POST /cancel-mine</c> — cancel the caller's active jobs.</summary>
        public const string CancelMine = "/cancel-mine";
        /// <summary><c>POST /requeue/{id}</c> — re-run the images a finished job never made.</summary>
        public const string Requeue = "/requeue/{id}";
        /// <summary><c>POST /free-vram</c> — drop the renderer's loaded models and cached VRAM.</summary>
        public const string FreeVram = "/free-vram";
        /// <summary><c>POST /upload</c> — hand over a render input (edit source, reference, mask, end frame).</summary>
        public const string Upload = "/upload";
        /// <summary><c>GET /image/{id}</c> — serve an image (optionally thumbnailed).</summary>
        public const string Image = "/image/{id}";
        /// <summary><c>GET /lora-preview</c> — a LoRA's cached CivitAI preview media.</summary>
        public const string LoraPreview = "/lora-preview";
        /// <summary><c>GET /image/{id}/info</c> — an image's pixel dimensions.</summary>
        public const string ImageInfo = "/image/{id}/info";
        /// <summary><c>GET /image/{id}/mp4</c> — an image/clip served as mp4.</summary>
        public const string ImageMp4 = "/image/{id}/mp4";
        /// <summary><c>GET /image/{id}/palette</c> — the quantize palette JSON.</summary>
        public const string ImagePalette = "/image/{id}/palette";
        /// <summary><c>GET /image/{id}/frequencies</c> — the quantize label-frequency JSON.</summary>
        public const string ImageFrequencies = "/image/{id}/frequencies";
        /// <summary><c>GET /image/{id}/params</c> — the generation request JSON (owner-checked).</summary>
        public const string ImageParams = "/image/{id}/params";
        /// <summary><c>GET /image/{id}/frames</c> — the lossless frames as a zip.</summary>
        public const string ImageFrames = "/image/{id}/frames";
        /// <summary><c>POST /media</c> — the media kind per image id, asked in bulk.</summary>
        public const string Media = "/media";
        /// <summary><c>GET /ws</c> — the live-progress WebSocket.</summary>
        public const string Ws = "/ws";
    }

    /// <summary>HTTP content-type tokens sniffed from bytes, matched, or written on responses.</summary>
    private static class ContentTypes
    {
        /// <summary>PNG image.</summary>
        public const string ImagePng = "image/png";
        /// <summary>WebP image (a still or an animated clip).</summary>
        public const string ImageWebp = "image/webp";
        /// <summary>MP4 video.</summary>
        public const string VideoMp4 = "video/mp4";
        /// <summary>JSON payload written by the pass-through <c>/palette</c>, <c>/frequencies</c>, <c>/params</c> endpoints.</summary>
        public const string ApplicationJson = "application/json";
        /// <summary>Zip archive of an image's lossless frames.</summary>
        public const string ApplicationZip = "application/zip";
        /// <summary>The content-type family prefix shared by every video clip.</summary>
        public const string VideoPrefix = "video/";
    }

    /// <summary>JSON property names read off the backend's progress frames.</summary>
    private static class JsonFields
    {
        /// <summary>The frame's payload object.</summary>
        public const string Data = "data";
        /// <summary>The backend prompt id carried inside <see cref="Data"/>.</summary>
        public const string PromptId = "prompt_id";
    }

    /// <summary>Keys the /forge auth filter stashes per-request values under in <c>HttpContext.Items</c>.</summary>
    private static class RequestItems
    {
        /// <summary>The authenticated user that owns this request's jobs.</summary>
        public const string OwnerUserId = "ForgeOwnerUserId";
        /// <summary>The request scope ("api" for an API-key caller, else the browser UI).</summary>
        public const string Scope = "ForgeScope";
    }

    /// <summary>Multipart form field names the upload endpoint reads.</summary>
    private static class FormFields
    {
        /// <summary>The uploaded image file field.</summary>
        public const string Image = "image";
    }

    /// <summary>String tokens compared as status/kind/scope discriminators.</summary>
    private static class Discriminators
    {
        /// <summary>The <see cref="RequestItems.Scope"/> value marking an API-key caller.</summary>
        public const string ApiScope = "api";
        /// <summary>The tag-query kind selecting artist autocomplete.</summary>
        public const string Artist = "artist";
        /// <summary>The wire phase value for a job waiting off the GPU.</summary>
        public const string Queued = "queued";
    }

    /// <summary>Delimiters used when composing wire text.</summary>
    private static class Separators
    {
        /// <summary>Comma-space, joining CivitAI trained words into a prompt list.</summary>
        public const string CommaSpace = ", ";
    }

    /// <summary>WebSocket close-frame status descriptions sent downstream.</summary>
    private static class CloseReasons
    {
        /// <summary>Sent when the backend progress socket can't be opened for this client's /ws.</summary>
        public const string ProgressBackendUnavailable = "progress backend unavailable";
    }

    /// <summary>Map the render endpoints onto the /forge group.</summary>
    public static void MapForgeApi(this RouteGroupBuilder app)
    {
        MapWorkflows(app);
        MapTags(app);
        MapRender(app);
        MapImages(app);
        MapProgressSocket(app);
    }

    /// <summary>The authenticated user that owns this request's jobs (stashed by the /forge auth filter).</summary>
    private static long OwnerOf(HttpRequest r) =>
        (long)(r.HttpContext.Items[RequestItems.OwnerUserId] ?? throw new InvalidOperationException("ForgeOwnerUserId is not set on the request."));

    /// <summary>The subfolder a LoRA lives in — everything before the final path separator — or "" for a root-level
    /// file. ComfyUI reports names with the OS separator, so both '/' and '\' count, and the result is normalized to '/'.</summary>
    private static string LoraFolderOf(string name)
    {
        var idx = name.LastIndexOfAny(['/', '\\']);
        return idx <= 0 ? "" : name[..idx].Replace('\\', '/');
    }

    /// <summary>The trigger words that attach to the prompt for a LoRA: the user's override if set, else the CivitAI
    /// trained words joined into a comma list, else null (nothing to attach).</summary>
    private static string? EffectiveTriggers(
        string name, IReadOnlyDictionary<string, LoraMeta> meta, IReadOnlyDictionary<string, LoraUserSetting> settings)
    {
        if (settings.TryGetValue(name, out var us) && !string.IsNullOrWhiteSpace(us.TriggerWords))
            return us.TriggerWords;
        if (meta.TryGetValue(name, out var m) && m.TrainedWords.Count > 0)
            return DefaultTriggers(m);
        return null;
    }

    /// <summary>The CivitAI trained words as a clean comma list (trimmed, trailing commas stripped, blanks dropped).</summary>
    private static string DefaultTriggers(LoraMeta m) =>
        string.Join(Separators.CommaSpace, m.TrainedWords.Select(w => w.Trim().TrimEnd(',').Trim()).Where(w => w.Length > 0));

    /// <summary>A LoRA's fallback display label: the filename without its folder or a known model extension — the same
    /// thing the client's <c>label()</c> shows, used when CivitAI hasn't supplied a model name.</summary>
    private static string LoraLabelOf(string name)
    {
        var file = name[(name.LastIndexOfAny(['/', '\\']) + 1)..];
        foreach (var ext in new[] { ".safetensors", ".ckpt", ".pt", ".gguf" })
            if (file.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return file[..^ext.Length];
        return file;
    }

    /// <summary>The name to show for a LoRA: CivitAI's model name once known, else the filename label.</summary>
    private static string DisplayNameOf(string name, IReadOnlyDictionary<string, LoraMeta> meta) =>
        meta.TryGetValue(name, out var m) && !string.IsNullOrWhiteSpace(m.ModelName) ? m.ModelName : LoraLabelOf(name);

    /// <summary>Whether a LoRA is fully populated — nothing more will change on its card, so the client can stop
    /// polling. True when CivitAI is off (nothing to fetch), or a cache row exists AND either it promises no preview
    /// or the preview bytes are cached. The populator writes the row LAST, after caching the preview, so this flips
    /// true exactly when the whole card is ready.</summary>
    private static bool LoraReady(
        string name, bool civitaiEnabled,
        IReadOnlyDictionary<string, LoraMeta> meta, IReadOnlyDictionary<string, string> previewTypes) =>
        !civitaiEnabled
        || (meta.TryGetValue(name, out var m) && (string.IsNullOrEmpty(m.PreviewUrl) || previewTypes.ContainsKey(name)));

    /// <summary>Whether the cached preview for a LoRA is a video clip (mp4/webm) rather than an image — the client
    /// renders those in a &lt;video&gt; instead of an &lt;img&gt;.</summary>
    private static bool PreviewIsVideo(string name, IReadOnlyDictionary<string, string> previewTypes) =>
        previewTypes.TryGetValue(name, out var ct) && ct.StartsWith(ContentTypes.VideoPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The refusal for a submission the box has no memory for. 503 (not 4xx): the request is fine, the
    /// server is temporarily unable to take it — so a client knows to retry later rather than to fix its input. The
    /// body carries <c>error</c> like every other failure here, so existing callers surface it unchanged.</summary>
    private static IResult LowMemory(string message) =>
        Results.Json(new { error = message }, statusCode: StatusCodes.Status503ServiceUnavailable);

    /// <summary>
    /// Accept a submission, or refuse it cleanly when it cannot be written down.
    /// <para>The front door is the ONLY place an unreachable database is allowed to end anything. Refusing new work
    /// during an outage is fine — accepting it is not, because a job that exists only in memory renders and then
    /// vanishes with the process. Everything already accepted waits the outage out instead (see
    /// RenderOrchestrator.AwaitingDatabaseAsync). 503 says "come back shortly", which is exactly what this is,
    /// rather than the 500 an unhandled storage error would otherwise produce.</para>
    /// </summary>
    private static async Task<IResult> AcceptAsync(Func<Task<IResult>> submit)
    {
        try { return await submit(); }
        catch (RenderStorageException ex) { return LowMemory(ex.Message); }
    }

    private static string? UrlFor(string? imageId) =>
        string.IsNullOrEmpty(imageId) ? null : $"{PublicBase}/image/{Uri.EscapeDataString(imageId)}";

    /// <summary>
    /// Vet a request's optional generation mask at the boundary, so a bad name 400s here instead of failing a job that
    /// has already been queued. NULL is valid and means "not specified" (the owner's stored mask then applies); an
    /// EMPTY list is a valid choice meaning every switchable type is off, which is why the two cannot be conflated.
    /// </summary>
    private static bool ValidTagTypes(List<string>? requested, out string? error)
    {
        error = null;
        if (requested is null) return true;
        if (GenerationTagTypes.TryNormalize(requested, out _, out var reason)) return true;
        error = reason;
        return false;
    }

    #region workflows + prompting

    private static void MapWorkflows(RouteGroupBuilder app)
    {
        app.MapGet(Routes.Healthz, () => Results.Ok(new { ok = true })).AllowAnonymous();

        // Every workflow configuration the current machine can run, for the caller's scope. Eligibility + row shaping
        // live in the catalog adapter; a renderer that's unreachable surfaces as a 502.
        app.MapGet(Routes.Workflows, async (HttpContext http, IWorkflowCatalog catalog, UserService users, CancellationToken ct) =>
        {
            try
            {
                var list = await catalog.ListEligibleAsync(ct);
                // Hiding is a per-user choice, applied per surface: the browser UI gets the full list and the picker
                // drops the user's UI-hidden set client-side; an API-key caller gets its OWN api-hidden set removed
                // here, because the API has no client to do it. Nothing is hidden by default — the catalogue ships no
                // visibility flag; hiding happens only on the workflows page.
                if ((http.Items[RequestItems.Scope] as string) == Discriminators.ApiScope && http.User.GetUserId() is { } userId)
                {
                    var hidden = (await users.GetWorkflowPrefsAsync(userId, ct)).HiddenApi;
                    if (hidden.Count > 0)
                    {
                        var drop = hidden.ToHashSet(StringComparer.OrdinalIgnoreCase);
                        list = list.Where(w => !drop.Contains(w.Id)).ToList();
                    }
                }
                return Results.Ok(list);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Net.Sockets.SocketException)
            {
                return Results.Json(new { error = "The image renderer isn't reachable — is ComfyUI running?" }, statusCode: 502);
            }
        });

        // Everything this machine can and cannot run, and why. The picker deliberately lists only what is READY;
        // this is the surface that explains the difference.
        app.MapGet(Routes.CatalogStatus, async (IWorkflowCatalog catalog, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await catalog.GetStatusAsync(ct));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Net.Sockets.SocketException)
            {
                return Results.Json(new { error = "The image renderer isn't reachable — is ComfyUI running?" }, statusCode: 502);
            }
        });

        // The LoRA files this machine offers, for the composer's picker: each file's subfolder-qualified name, the
        // subfolder it lives in, and this user's chosen cover image (if any). An optional ?workflow= annotates each
        // with whether it will actually apply to that workflow's base model (and whether it affects CLIP).
        app.MapGet(Routes.Loras, async (HttpRequest http, string? workflow, IWorkflowCatalog catalog, LoraService loras,
            ILoraMetaRepository meta, ILoraPreviewRepository previews, ILoraUserSettingRepository userSettings,
            ICivitaiClient civitai, ILoraMetaPopulator populator, CancellationToken ct) =>
        {
            try
            {
                var userId = OwnerOf(http);
                // The picker is offered only for a single selected model, so compatibility is judged against that one.
                var entries = await catalog.ListLorasAsync(workflow, ct);
                var names = entries.Select(e => e.Name).ToList();
                var covers = await loras.GetCoversAsync(userId, names, ct);
                // Cached metadata + this user's overrides — never blocking. A file that isn't cached yet comes back as
                // a stub (ready:false), and Request() kicks its background population off; the client polls /loras/meta.
                var metaByName = await meta.GetManyAsync(names, ct);
                var previewTypes = await previews.GetContentTypesAsync(names, ct);
                var settingsByName = await userSettings.GetManyAsync(userId, names, ct);
                var enabled = civitai.IsEnabled();
                populator.Request(names);
                var rows = entries.Select(e => new
                {
                    name = e.Name,
                    folder = LoraFolderOf(e.Name),
                    displayName = DisplayNameOf(e.Name, metaByName),
                    cover = covers.GetValueOrDefault(e.Name),
                    hasPreview = previewTypes.ContainsKey(e.Name),
                    previewVideo = PreviewIsVideo(e.Name, previewTypes),
                    ready = LoraReady(e.Name, enabled, metaByName, previewTypes),
                    compatible = e.Compatible,
                    clipCapable = e.ClipCapable,
                    triggers = EffectiveTriggers(e.Name, metaByName, settingsByName),
                    autoAttach = !settingsByName.TryGetValue(e.Name, out var us) || us.AutoAttach,
                });
                return Results.Ok(rows);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Net.Sockets.SocketException)
            {
                return Results.Json(new { error = "The image renderer isn't reachable — is ComfyUI running?" }, statusCode: 502);
            }
        });

        // The LoRA manager page's data: every LoRA on this box with its cover / CivitAI preview, model name, and
        // trigger words. Like /loras this is NON-BLOCKING — it returns stubs at once and Request() populates in the
        // background; the client polls /loras/meta. Nothing here hashes or hits the network on the request thread.
        app.MapGet(Routes.LorasManage, async (HttpRequest http, IWorkflowCatalog catalog, ILoraMetaRepository meta,
            ILoraPreviewRepository previews, LoraService loras, ILoraUserSettingRepository userSettings,
            ICivitaiClient civitai, ILoraMetaPopulator populator, CancellationToken ct) =>
        {
            try
            {
                var userId = OwnerOf(http);
                var entries = await catalog.ListLorasAsync(null, ct);   // all LoRAs; compatibility isn't relevant here
                var names = entries.Select(e => e.Name).ToList();
                var metaByName = await meta.GetManyAsync(names, ct);
                var previewTypes = await previews.GetContentTypesAsync(names, ct);
                var settings = await userSettings.GetManyAsync(userId, names, ct);
                var covers = await loras.GetCoversAsync(userId, names, ct);
                var enabled = civitai.IsEnabled();
                populator.Request(names);
                var rows = entries.Select(e =>
                {
                    metaByName.TryGetValue(e.Name, out var m);
                    settings.TryGetValue(e.Name, out var us);
                    var def = m is { TrainedWords.Count: > 0 } ? DefaultTriggers(m) : "";
                    return new
                    {
                        name = e.Name,
                        folder = LoraFolderOf(e.Name),
                        displayName = DisplayNameOf(e.Name, metaByName),
                        cover = covers.GetValueOrDefault(e.Name),
                        hasPreview = previewTypes.ContainsKey(e.Name),
                        previewVideo = PreviewIsVideo(e.Name, previewTypes),
                        ready = LoraReady(e.Name, enabled, metaByName, previewTypes),
                        modelName = m?.ModelName,
                        defaultTriggers = def,
                        triggers = !string.IsNullOrWhiteSpace(us?.TriggerWords) ? us.TriggerWords : def,
                        hasOverride = !string.IsNullOrWhiteSpace(us?.TriggerWords),
                        autoAttach = us?.AutoAttach ?? true,
                    };
                }).ToList();
                return Results.Ok(new { civitaiEnabled = enabled, loras = rows });
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Net.Sockets.SocketException)
            {
                return Results.Json(new { error = "The image renderer isn't reachable — is ComfyUI running?" }, statusCode: 502);
            }
        });

        // The poll the picker/manager/composer hit while any LoRA they're showing is still populating. Returns the
        // current cached state for the named files (never blocking), (re)queues any not-yet-ready ones so a job resumes
        // after a restart, and reports whether any are still pending so the client knows to keep polling. POST, so a
        // long list of subfolder-qualified names travels in the body, not a capped URL (see MediaTypesRequest).
        app.MapPost(Routes.LorasMeta, async (HttpRequest http, LoraMetaQueryRequest body, ILoraMetaRepository meta,
            ILoraPreviewRepository previews, ILoraUserSettingRepository userSettings, ICivitaiClient civitai,
            ILoraMetaPopulator populator, CancellationToken ct) =>
        {
            var userId = OwnerOf(http);
            var names = (body.Names ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (names.Count == 0)
                return Results.Ok(new { items = Array.Empty<object>(), pending = false });

            var metaByName = await meta.GetManyAsync(names, ct);
            var previewTypes = await previews.GetContentTypesAsync(names, ct);
            var settingsByName = await userSettings.GetManyAsync(userId, names, ct);
            var enabled = civitai.IsEnabled();
            populator.Request(names);   // idempotent: resumes anything still pending, starts nothing already cached

            var items = names.Select(n => new
            {
                name = n,
                displayName = DisplayNameOf(n, metaByName),
                hasPreview = previewTypes.ContainsKey(n),
                previewVideo = PreviewIsVideo(n, previewTypes),
                ready = LoraReady(n, enabled, metaByName, previewTypes),
                triggers = EffectiveTriggers(n, metaByName, settingsByName),
                autoAttach = !settingsByName.TryGetValue(n, out var us) || us.AutoAttach,
            }).ToList();
            return Results.Ok(new { items, pending = items.Any(i => !i.ready) });
        });

        // Refresh: forget the cached CivitAI data (and preview bytes) for these files — or every LoRA on the box when
        // the list is empty — then re-queue them so the populator re-fetches. A no-op when CivitAI is off (there is
        // nothing to fetch, and wiping the cache would leave the cards blank). The client polls /loras/meta after.
        app.MapPost(Routes.LorasRefresh, async (LoraRefreshRequest body, IWorkflowCatalog catalog, ILoraMetaRepository meta,
            ILoraPreviewRepository previews, ICivitaiClient civitai, ILoraMetaPopulator populator, CancellationToken ct) =>
        {
            try
            {
                if (!civitai.IsEnabled())
                    return Results.Ok(new { refreshed = Array.Empty<string>() });

                var names = body.Names is { Count: > 0 }
                    ? body.Names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                    : (await catalog.ListLorasAsync(null, ct)).Select(e => e.Name).ToList();
                if (names.Count == 0)
                    return Results.Ok(new { refreshed = Array.Empty<string>() });

                await previews.DeleteAsync(names, ct);
                await meta.DeleteAsync(names, ct);
                populator.Request(names);
                return Results.Ok(new { refreshed = names });
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Net.Sockets.SocketException)
            {
                return Results.Json(new { error = "The image renderer isn't reachable — is ComfyUI running?" }, statusCode: 502);
            }
        });

        // Point a slot at a file on this machine, or clear it. A blank fileName clears, which is how a wrong
        // automatic guess is rejected.
        app.MapPut(Routes.CatalogBinding, async (BindingRequest body, IWorkflowCatalog catalog, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.SlotId))
                return Results.BadRequest(new { error = "slotId is required." });
            await catalog.SetBindingAsync(body.SlotId, body.FileName, ct);
            return Results.NoContent();
        });

        // One per-configuration override for this machine (vram.min, param.<key>, ...). A blank value
        // REMOVES the override and restores the shipped default.
        app.MapPut(Routes.CatalogOverride, async (OverrideRequest body, IWorkflowCatalog catalog, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.ConfigId) || string.IsNullOrWhiteSpace(body.Key))
                return Results.BadRequest(new { error = "configId and key are required." });
            try { await catalog.SetOverrideAsync(body.ConfigId, body.Key, body.Value, ct); }
            // A value the model does not support is the caller's mistake, not a server fault — answered with the
            // model's own numbers so the form can say what is allowed.
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            return Results.NoContent();
        });

        // What one workflow renders with on this machine, and what the catalogue shipped, so the settings page can
        // show both and offer a reset.
        app.MapGet(Routes.CatalogConfigSettings, (string id, IWorkflowCatalog catalog) =>
        {
            var settings = catalog.GetSettings(id);
            return settings is null
                ? Results.NotFound(new { error = $"No workflow '{id}'." })
                : Results.Ok(settings);
        });

        // One configuration's prompting guide (resolves {model} loosely, as generate accepts it).
        app.MapGet(Routes.PromptingForModel, (string model, IWorkflowCatalog catalog) =>
        {
            var guide = catalog.GetGuide(model);
            return guide is null
                ? Results.NotFound(new { error = $"No prompting guide for '{model}'. Call list_models for valid ids." })
                : Results.Ok(guide);
        });

        // All configurations' guides.
        app.MapGet(Routes.Prompting, (IWorkflowCatalog catalog) => Results.Ok(catalog.AllGuides()));
    }

    #endregion

    #region tags

    private static void MapTags(RouteGroupBuilder app)
    {
        // Tag/artist autocomplete: model-ranked ('#' tags with context) or count-ranked (fallback / '@' artists).
        //
        // POST, and deliberately not GET. `ctx` is the prompt being typed and `q` a tag fragment, and this fires on
        // every keystroke — as a query string that puts the prompt, keystroke by keystroke, into the browser's own
        // history and address-bar autocomplete, on the user's machine, where nothing server-side can ever clean it
        // up. It also reaches request logs, proxies and Referer headers. A body goes in none of those places.
        app.MapPost(Routes.Tags, async (TagQueryRequest req, ITagCatalog tags, ITagModelClient model) =>
        {
            var artist = string.Equals(req.Kind, Discriminators.Artist, StringComparison.OrdinalIgnoreCase);
            // A present limit outside [1,50] is refused, not clamped: silently returning 50 for a request of 1000 reads
            // to the caller as "that's all there is". An absent limit (null) legitimately means "use the default".
            var n = req.Limit ?? 10;
            if (n is < 1 or > 50) return Results.BadRequest(new { error = "limit must be between 1 and 50." });
            var frag = req.Q ?? "";
            var ctx = req.Ctx;

            if (!artist && !string.IsNullOrWhiteSpace(ctx) && model.Enabled)
            {
                var sug = await model.QueryAsync(ctx, frag, n, CancellationToken.None);
                if (sug is { Count: > 0 })
                    // .Take(n) is not redundant. `n` is this endpoint's stated limit and the fallback below honours
                    // it; without it the model-ranked path would return whatever the tag server sent, so the SAME
                    // request would yield 10 or up to 100 results depending on which branch ran -- invisible to the caller.
                    return Results.Ok(sug.Take(n).Select(s =>
                    {
                        var meta = tags.Lookup(s.Name);
                        return new { name = s.Name, p = (double?)s.P, lift = s.Lift,
                                     count = meta?.Count ?? 0, type = meta?.Type ?? 0 };
                    }));
            }

            return Results.Ok(tags.Query(frag, artist, n).Select(t => new
            {
                name = t.Name, p = (double?)null, lift = (double?)null, count = t.Count, type = t.Type
            }));
        });

        app.MapGet(Routes.TagsStatus, (ITagCatalog tags) =>
            Results.Ok(new { loaded = tags.Loaded, status = tags.Status, tags = tags.TagCount, artists = tags.ArtistCount }));
    }

    #endregion

    #region render (enqueue + job views)

    private static void MapRender(RouteGroupBuilder app)
    {
        app.MapPost(Routes.Generate, async (GenerateRequest req, HttpRequest http, RenderOrchestrator queue, SubmissionMemoryGate gate) =>
        {
            if (string.IsNullOrWhiteSpace(req.Workflow)) return Results.BadRequest(new { error = "A workflow is required." });
            if (!ValidTagTypes(req.TagTypes, out var maskError)) return Results.BadRequest(new { error = maskError });
            if (gate.Refusal() is { } full) return LowMemory(full);
            return await AcceptAsync(async () =>
            {
                var job = await queue.EnqueueJobAsync(OwnerOf(http), new[] { RenderItem.ForGenerate(req.ToSpec()) });
                return Results.Ok(new { jobId = job.JobId, promptId = job.JobId, total = job.Total, notice = job.Slots.FirstOrDefault()?.Notice });
            });
        });

        app.MapPost(Routes.Edit, async (EditRequest req, HttpRequest http, RenderOrchestrator queue, SubmissionMemoryGate gate) =>
        {
            if (string.IsNullOrWhiteSpace(req.Workflow)) return Results.BadRequest(new { error = "A workflow is required." });
            if (string.IsNullOrWhiteSpace(req.ImageId)) return Results.BadRequest(new { error = "A source image id is required." });
            if (gate.Refusal() is { } full) return LowMemory(full);
            return await AcceptAsync(async () =>
            {
                var job = await queue.EnqueueJobAsync(OwnerOf(http), new[] { RenderItem.ForEdit(req.ToSpec()) });
                return Results.Ok(new { jobId = job.JobId, promptId = job.JobId, total = job.Total, notice = job.Slots.FirstOrDefault()?.Notice });
            });
        });

        app.MapPost(Routes.Enqueue, async (EnqueueRequest req, HttpRequest http, RenderOrchestrator queue, SubmissionMemoryGate gate) =>
        {
            if (gate.Refusal() is { } full) return LowMemory(full);
            // An unknown type name is rejected for the WHOLE batch rather than dropped from one item: a dropped name
            // reads downstream as "the user switched that type off", which silently changes what gets generated.
            foreach (var it in req.Jobs ?? [])
                if (!ValidTagTypes(it.TagTypes, out var itemMaskError))
                    return Results.BadRequest(new { error = itemMaskError });
            var items = (req.Jobs ?? new List<EnqueueItem>()).Select(it => it.ToRenderItem()).OfType<RenderItem>().ToList();
            if (items.Count == 0) return Results.BadRequest(new { error = "No valid jobs in the batch." });
            return await AcceptAsync(async () =>
            {
                var job = await queue.EnqueueJobAsync(OwnerOf(http), items);
                return Results.Ok(new { jobId = job.JobId, total = job.Total });
            });
        });

        // POLL one job (legacy single-image shape). Memory first, DB fallback once finalized; unknown id → error.
        app.MapGet(Routes.Result, async (string id, RenderOrchestrator queue, IJobRepository jobs, CancellationToken ct) =>
        {
            var job = queue.Get(id);
            if (job is not null && job.Slots.Count > 0)
            {
                var s = job.Slots[0];
                return Results.Ok(LegacyResultSlot(RenderPhases.Of(s.State), s.ImageId, s.ExpectedGenSeconds, s.GenStartedAt,
                    s.Width, s.Height, s.IsEdit, s.Changed, s.ChangeScore, s.EffectivePrompt, s.Marks, s.Error, queue.JobsAhead(job), s.Notice));
            }
            var rec = await jobs.GetAsync(id, ct);
            if (rec is null || rec.Slots.Count == 0) return Results.Ok(new { status = "error", error = "unknown job id" });
            var sr = rec.Slots.OrderBy(x => x.SlotIndex).First();
            return Results.Ok(LegacyResultSlot(RenderPhases.Of(sr.State), sr.ImageId, sr.ExpectedGenSeconds,
                sr.GenStartedAtUtc is { } g ? new DateTimeOffset(DateTime.SpecifyKind(g, DateTimeKind.Utc)) : null,
                sr.Width ?? 0, sr.Height ?? 0, sr.IsEdit, sr.Changed, sr.ChangeScore, sr.EffectivePrompt, MarksMap(sr.Marks), sr.Error, 0));
        });

        // SYNC: this user's ACTIVE jobs only. A finalized job is not here — its absence is the client's reconcile cue.
        app.MapGet(Routes.Jobs, (HttpRequest http, RenderOrchestrator queue) =>
        {
            var owner = OwnerOf(http);
            return Results.Ok(new { jobs = queue.ActiveForOwner(owner).Select(j => JobViewOf(j, queue)) });
        });

        // Cross-user QUEUE + history: a page of every gen on this box, live rows overlaid with in-memory state.
        app.MapGet(Routes.Queue, async (HttpRequest http, RenderOrchestrator queue, IJobRepository jobs,
            IGenTimingRepository timings, int? page, int? pageSize, CancellationToken ct) =>
        {
            var me = OwnerOf(http);
            // Out-of-range page/size are refused, not clamped (a clamped page silently returns a different page than
            // asked for). Absent (null) means the default.
            var p = page ?? 1;
            var size = pageSize ?? 25;
            if (p < 1) return Results.BadRequest(new { error = "page must be >= 1." });
            if (size is < 1 or > 100) return Results.BadRequest(new { error = "pageSize must be between 1 and 100." });
            var pr = await jobs.ListPageAsync(Environment.MachineName, me, p, size, ct);
            var live = queue.AllActive().ToDictionary(j => j.JobId);
            // Same ordering as the DB page (unfinished first in SERVICE order, then finished newest-first), re-applied
            // here because the page has to be re-sorted anyway: CreatedAtUtc is millisecond-truncated, so a burst of
            // concurrently-submitted jobs (a multi-model fan-out) collides there and falls back to a random JobId
            // tie-break. For jobs still live in memory we hold the full-precision submission instant — the same
            // ordering JobsAhead is built on — so order by that; finalized rows keep the DB timestamp.
            //
            // Service order is ASCENDING for unfinished work and it is not cosmetic: the fair queue renders the oldest
            // queued job next, so ordering those newest-first would put the row that is actually on the GPU last, pages
            // away from the only page the client polls.
            DateTime SubmittedAt(JobRecord r) =>
                live.TryGetValue(r.JobId, out var lj) ? lj.CreatedAt.UtcDateTime : r.CreatedAtUtc;
            bool Unfinished(JobRecord r) => r.Status == JobStatus.Active || live.ContainsKey(r.JobId);

            var rows = pr.Items.Where(Unfinished).OrderBy(SubmittedAt)
                .Concat(pr.Items.Where(r => !Unfinished(r)).OrderByDescending(SubmittedAt))
                .Select(r => live.TryGetValue(r.JobId, out var lj) ? QueueRowOf(lj, queue, me) : CompletedQueueRowOf(r, me))
                .ToList();
            return Results.Ok(new
            {
                jobs = rows, page = p, pageSize = size, total = pr.Total,
                outstanding = await OutstandingViewAsync(queue, timings, me, ct),
            });
        });

        // LOOKUP a job by id (active or finalized) — the durable read for a job that vanished from /jobs. Owner-checked.
        app.MapGet(Routes.Job, async (string id, HttpRequest http, RenderOrchestrator queue, IJobRepository jobs, CancellationToken ct) =>
        {
            var owner = OwnerOf(http);
            var live = queue.Get(id);
            if (live is not null)
                return live.Owner == owner ? Results.Ok(JobViewOf(live, queue)) : Results.Unauthorized();
            var rec = await jobs.GetAsync(id, ct);
            if (rec is null) return Results.NotFound(new { error = "unknown job id" });
            return rec.UserId == owner ? Results.Ok(JobRecordView(rec)) : Results.Unauthorized();
        });

        // A live job is cancelled in memory. A job this instance owns whose row is still Active but which no worker
        // holds — stranded by a crash — has nothing rendering it, so its row is failed instead. Same answer either way:
        // the queue page offers Cancel on anything unfinished, and unfinished-and-unowned must not be a dead button.
        app.MapPost(Routes.Cancel, async (string id, RenderOrchestrator queue, CancellationToken ct) =>
            Results.Ok(new { ok = queue.Cancel(id) || await queue.CancelStrandedAsync(id, ct) }));
        app.MapPost(Routes.Interrupt, (RenderOrchestrator queue) => Results.Ok(new { ok = queue.CancelRunning() }));

        // Bulk cancel, in one call rather than a client loop over rendered rows: the queue page shows 25 of a list it
        // re-polls every 2s, so a loop would clear only the visible page and race the poll rebuilding it.
        //
        // AUTHORIZATION: /cancel-all is open to every signed-in user, exactly like the per-row /cancel/{id} it
        // batches. Cancelling other people's work is already possible today — the queue offers Cancel on every active
        // row regardless of owner — so this changes how many clicks it takes, not who can do it. There is no admin or
        // role concept anywhere in this app to gate it with, and inventing a half of one here would be worse than the
        // honest status quo. The client confirms first, since it is irreversible and can discard work that isn't
        // yours. /cancel-mine is scoped to the caller and needs no such warning.
        app.MapPost(Routes.CancelAll, async (RenderOrchestrator queue, CancellationToken ct) =>
            Results.Ok(new { cancelled = await queue.CancelAllAsync(null, ct) }));
        app.MapPost(Routes.CancelMine, async (HttpRequest http, RenderOrchestrator queue, CancellationToken ct) =>
            Results.Ok(new { cancelled = await queue.CancelAllAsync(OwnerOf(http), ct) }));

        // Re-run the images a finished job never made, as a NEW job. Owner-checked (unlike /cancel/{id}): this
        // CREATES work under an owner, and the scheduler is fair round-robin per owner, so an unchecked requeue would
        // let one user push work into another's queue share. Goes through the same submission gate as /generate —
        // requeued work is work, and a box too low on memory to accept a generation is too low to accept this.
        app.MapPost(Routes.Requeue, async (string id, HttpRequest http, RenderOrchestrator queue,
            SubmissionMemoryGate gate, CancellationToken ct) =>
        {
            if (gate.Refusal() is { } full) return LowMemory(full);
            return await AcceptAsync(async () =>
            {
            var r = await queue.RequeueAsync(id, OwnerOf(http), ct);
            return r.Status switch
            {
                RequeueStatus.Requeued => Results.Ok(new { jobId = r.JobId, total = r.Images }),
                RequeueStatus.UnknownJob => Results.NotFound(new { error = "unknown job id" }),
                RequeueStatus.NotOwner => Results.Unauthorized(),
                RequeueStatus.StillActive => Results.BadRequest(new { error = "That job hasn't finished yet." }),
                RequeueStatus.NothingMissing => Results.BadRequest(new { error = "Every image in that job was made." }),
                _ => Results.BadRequest(new { error = "This can't be remade: " + r.Reason }),
            };
            });
        });

        // Drop the renderer's loaded models + cached VRAM. The backend applies it between prompts, so it can't disturb
        // a render already running; queued work simply reloads its model when it starts.
        app.MapPost(Routes.FreeVram, async (IComfyClient comfy, CancellationToken ct) =>
        {
            await comfy.FreeMemoryAsync(ct);
            return Results.Ok(new { ok = true });
        });
    }

    /// <summary>
    /// The queue header's "what's left": outstanding jobs, outstanding images, and how long the lot should take.
    /// <para>The total is the in-flight slot's own countdown plus this machine's recent average for each image still
    /// waiting. Waiting slots have no expected time of their own — it's assigned at submit — so the workflow average
    /// is the only estimate available for them.</para>
    /// <para><c>unpricedImages</c> is not decoration. A workflow that has never rendered here has no average and so
    /// contributes NOTHING to the sum, which makes the total an under-report rather than an unknown. The client is
    /// told how many images are in that state so it can present the number as the lower bound it is.</para>
    /// <para>The estimate is wall-clock only if nothing else is submitted meanwhile. There is deliberately no "mine"
    /// variant: the scheduler is fair round-robin PER OWNER, so one user's wait is not the sum of their own slots —
    /// their work interleaves with everyone else's, and a subtotal would be a confidently wrong number.</para>
    /// </summary>
    private static async Task<object> OutstandingViewAsync(
        RenderOrchestrator queue, IGenTimingRepository timings, long viewer, CancellationToken ct)
    {
        var o = queue.Outstanding(viewer);
        if (o.Images == 0)
            return new { jobs = 0, mineJobs = 0, images = 0, etaSeconds = (double?)null, unpricedImages = 0 };

        var averagesMs = await timings.RecentAveragesMsAsync(Environment.MachineName, 10, ct);
        var eta = o.RunningRemainingSeconds ?? 0;
        var unpriced = 0;
        foreach (var model in o.WaitingModels)
        {
            if (averagesMs.TryGetValue(model, out var ms)) eta += ms / 1000.0;
            else unpriced++;
        }
        return new
        {
            jobs = o.Jobs,
            mineJobs = o.ViewerJobs,
            images = o.Images,
            etaSeconds = eta > 0 ? Math.Round(eta, 1) : (double?)null,
            unpricedImages = unpriced,
        };
    }

    /// <summary>One slot's view for the client.</summary>
    private static object SlotView(int index, string status, string? imageId, string model, string? effectivePrompt,
        Dictionary<string, string>? marks, int width, int height, bool? changed, double? changeScore, string? error,
        string? notice = null) => new
        {
            index, status, id = imageId, url = UrlFor(imageId), model,
            effectivePrompt, marks, width, height, changed, changeScore, error, notice
        };

    private static object SlotViewOf(RenderSlot s) => SlotView(
        s.Index, RenderPhases.Of(s.State).Wire(),
        s.ImageId, s.Model, s.EffectivePrompt, s.Marks, s.Width, s.Height,
        s.IsEdit ? s.Changed : (bool?)null, s.ChangeScore, s.Error, s.Notice);

    private static object JobViewOf(RenderJob j, RenderOrchestrator q)
    {
        var running = j.Slots.FirstOrDefault(s => s.State == SlotState.Running);
        var status = RenderPhases.Of(j).Wire();
        return new
        {
            jobId = j.JobId,
            kind = j.IsEdit ? "edit" : "generate",
            model = j.Model,
            prompt = j.Prompt,
            sourceImageId = j.IsEdit && j.Slots.Count > 0 ? j.Slots[0].Edit?.ImageId : null,
            referenceImageIds = j.IsEdit && j.Slots.Count > 0 ? j.Slots[0].Edit?.ReferenceImageIds : null,
            total = j.Total,
            progress = j.Progress,
            produced = j.Produced,
            status,
            jobsAhead = status == Discriminators.Queued ? q.JobsAhead(j) : 0,
            expectedSeconds = running?.ExpectedGenSeconds,
            startedAt = running?.GenStartedAt,
            imageIds = j.ImageIds(),
            slots = j.Slots.OrderBy(s => s.Index).Select(SlotViewOf),
            createdAt = j.CreatedAt
        };
    }

    private static object QueueRowOf(RenderJob j, RenderOrchestrator q, long me)
    {
        var running = j.Slots.FirstOrDefault(s => s.State == SlotState.Running);
        var status = RenderPhases.Of(j).Wire();
        bool mine = j.Owner == me;
        return new
        {
            jobId = j.JobId,
            kind = j.IsEdit ? "edit" : "generate",
            model = j.Model,
            total = j.Total,
            progress = j.Progress,
            produced = j.Produced,
            status,
            active = true,
            jobsAhead = status == Discriminators.Queued ? q.JobsAhead(j) : 0,
            expectedSeconds = running?.ExpectedGenSeconds ?? j.Slots.FirstOrDefault()?.ExpectedGenSeconds,
            startedAt = running?.GenStartedAt,
            mine,
            prompt = mine ? j.Prompt : null,
            requeueable = 0,   // still live: Cancel is what this row offers, not a re-run of work still to come
            createdAt = j.CreatedAt,
            finishedAt = (DateTimeOffset?)null
        };
    }

    private static object CompletedQueueRowOf(JobRecord r, long me)
    {
        bool mine = r.UserId == me;
        var ordered = r.Slots.OrderBy(s => s.SlotIndex).ToList();
        bool isEdit = ordered.Count > 0 && ordered[0].IsEdit;
        int produced = ordered.Count(s => s.ImageId is not null);
        int progress = ordered.Count(s => s.State is JobSlotState.Done or JobSlotState.Error or JobSlotState.Cancelled);
        // This row is only reached because the job is NOT in this instance's live set, so nothing is rendering it —
        // whatever it says. A non-terminal one reads "queued" (it may also be stranded, but stranded is not running).
        var status = RenderPhases.Of(r.Status).Wire();
        double? durationSeconds = null;
        if (r.FinishedAtUtc is DateTime finished)
        {
            var starts = ordered.Select(s => s.GenStartedAtUtc).OfType<DateTime>();
            var start = starts.Any() ? starts.Min() : r.CreatedAtUtc;
            var secs = (finished - start).TotalSeconds;
            if (secs >= 0) durationSeconds = Math.Round(secs, 1);
        }
        return new
        {
            jobId = r.JobId,
            kind = isEdit ? "edit" : "generate",
            model = r.Model,
            total = r.Total,
            progress,
            produced,
            status,
            // "Still open", not "live in memory": a non-terminal row has not finished, so the client keeps offering
            // Cancel on it rather than styling it as a completed generation.
            active = r.Status == JobStatus.Active,
            jobsAhead = 0,
            expectedSeconds = (double?)null,
            startedAt = (DateTimeOffset?)null,
            mine,
            prompt = mine ? r.Prompt : null,
            // How many images this job never made and could be re-run. Counted from the slots rather than inferred
            // from produced-vs-total, which would also count a slot that finished by declining (an edit the model
            // intentionally left alone) and offer a button that then reports nothing to do.
            requeueable = ordered.Count(s => s.ImageId is null && s.State is JobSlotState.Error or JobSlotState.Cancelled),
            createdAt = AsUtc(r.CreatedAtUtc),
            finishedAt = AsUtc(r.FinishedAtUtc),
            durationSeconds
        };
    }

    /// <summary>DB DateTimes come back Kind=Unspecified; without this they'd serialize with no offset marker and the
    /// browser's Date.parse would read the UTC wall-clock as local time.</summary>
    private static DateTimeOffset? AsUtc(DateTime? dt) =>
        dt is null ? null : new DateTimeOffset(DateTime.SpecifyKind(dt.Value, DateTimeKind.Utc));

    private static object JobRecordView(JobRecord r)
    {
        // A durable record, read only when the job is not live here — so never "running", whatever its slot rows say.
        var status = RenderPhases.Of(r.Status).Wire();
        return new
        {
            jobId = r.JobId,
            kind = r.Slots.Count > 0 && r.Slots[0].IsEdit ? "edit" : "generate",
            model = r.Model,
            prompt = r.Prompt,
            total = r.Total,
            progress = r.Slots.Count(s => s.State is JobSlotState.Done or JobSlotState.Error or JobSlotState.Cancelled),
            produced = r.Slots.Count(s => s.ImageId is not null),
            status,
            imageIds = r.Slots.OrderBy(s => s.SlotIndex).Select(s => s.ImageId),
            slots = r.Slots.OrderBy(s => s.SlotIndex).Select(s => SlotView(
                s.SlotIndex, RenderPhases.Of(s.State).Wire(),
                s.ImageId, r.Model, s.EffectivePrompt, MarksMap(s.Marks), s.Width ?? 0, s.Height ?? 0,
                s.IsEdit ? s.Changed : (bool?)null, s.ChangeScore, s.Error)),
            createdAt = AsUtc(r.CreatedAtUtc),
            finishedAt = AsUtc(r.FinishedAtUtc)
        };
    }

    /// <summary>Fetch a legacy (pre-DB) image from ComfyUI's <c>/view</c>, answering null when the backend genuinely
    /// does not have it — the case the caller turns into a 404. Every OTHER failure propagates: catching everything
    /// and reporting "image not found" would tell the user their image did not exist when ComfyUI was down,
    /// unreachable, or erroring. Those are opposite facts, and only one of them is the user's to act on.</summary>
    private static async Task<byte[]?> FetchLegacyOrNullAsync(IComfyClient comfy, string id, CancellationToken ct)
    {
        try { return await comfy.FetchLegacyImageAsync(id, ct); }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest) { return null; }
    }

    /// <summary>A slot's marks in the client's token-&gt;kind shape. They are stored as rows (dbo.JobSlotMark), so
    /// there is nothing here that can fail to parse.</summary>
    private static Dictionary<string, string>? MarksMap(List<Mark> marks) =>
        marks.Count == 0 ? null : marks.ToDictionary(m => m.Token, m => m.Kind.ToWire(), StringComparer.Ordinal);

    /// <summary>The legacy single-image result shape. It takes the derived phase, so a waiting slot says "queued"
    /// (the <c>queued</c> flag stays for old clients).</summary>
    private static object LegacyResultSlot(RenderPhase phase, string? imageId, double? expectedSeconds, DateTimeOffset? startedAt,
        int width, int height, bool isEdit, bool changed, double? changeScore, string? effectivePrompt,
        Dictionary<string, string>? marks, string? error, int jobsAhead, string? notice = null) => phase switch
        {
            RenderPhase.Queued => new { status = "queued", queued = true, jobsAhead, notice },
            RenderPhase.Running => (object)new { status = "running", queued = false, expectedSeconds, startedAt, notice },
            RenderPhase.Error => new { status = "error", error, notice },
            _ when isEdit && !changed => new { status = "done", changed = false, changeScore, notice },
            _ when isEdit => new { status = "done", id = imageId, url = UrlFor(imageId), width, height, changed = true, changeScore, notice },
            _ => new { status = "done", id = imageId, url = UrlFor(imageId), width, height, effectivePrompt, marks, notice }
        };

    #endregion

    #region images

    private static void MapImages(RouteGroupBuilder app)
    {
        // An upload is a render INPUT (edit source, reference, inpaint mask, i2v end frame) and is never retrievable
        // afterwards, so it is held in memory only and never written to dbo.ImageBlob. See IUploadStore.
        //
        // This is the door the memory gate matters most at: these bytes are what stays resident until the render that
        // needs them runs, and nothing will evict them to make room. A box that is already low says so HERE, to the
        // caller holding the file, rather than taking the upload and failing the job it belongs to later.
        app.MapPost(Routes.Upload, async (HttpRequest request, IUploadStore uploads, IMediaProcessor media,
            SubmissionMemoryGate gate, HttpContext ctx) =>
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(new { error = "Expected multipart/form-data with an 'image' field." });
            if (gate.Refusal() is { } full) return LowMemory(full);
            var form = await request.ReadFormAsync();
            var file = form.Files[FormFields.Image];
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "Missing 'image' file field." });

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ctx.RequestAborted);
            var bytes = ms.ToArray();

            // An upload whose header will not read is not an image, and this endpoint takes images. Storing it anyway
            // with null dimensions would push the rejection much later — to a failed render, or a gallery row of
            // unknown size — with nothing left pointing at the upload that caused it.
            ImageDimensions dims;
            try { dims = media.Identify(bytes); }
            catch (Exception ex) { return Results.BadRequest(new { error = $"That file isn't a readable image: {ex.Message}" }); }
            int? w = dims.Width, h = dims.Height;
            var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? ContentTypes.ImagePng : file.ContentType;
            var id = uploads.Add(new UploadedImage(bytes, contentType, w, h));
            return Results.Ok(new { id });
        });

        app.MapGet(Routes.Image, async (string id, int? w, bool? still, IUploadStore uploads, IImageBlobRepository blobs,
            IMemoryCache cache, IComfyClient comfy, IMediaProcessor media, HttpContext ctx) =>
        {
            if (w is int requested)
            {
                if (requested < MinWidth || requested > MaxWidth)
                    return Results.BadRequest(new { error = $"w must be between {MinWidth} and {MaxWidth}, got {requested}" });
                var width = requested;
                var wantStill = still == true;
                var key = $"thumb:{id}:{width}:{(wantStill ? "s" : "a")}";
                if (!cache.TryGetValue(key, out MediaPayload? thumb) || thumb is null)
                {
                    byte[]? source = (await LoadImageAsync(id, uploads, blobs, ctx.RequestAborted))?.Bytes;
                    source ??= await FetchLegacyOrNullAsync(comfy, id, ctx.RequestAborted);
                    if (source is null) return Results.NotFound(new { error = "image not found" });
                    // Thumbnailing is NOT wrapped. The image was found — that is what `source` is — so a failure here
                    // is ours to answer for; reporting it as "image not found" would send the client away looking for a
                    // missing image while the real fault (a codec, a truncated blob) goes unlogged and unfixed.
                    thumb = wantStill ? media.StillThumbnail(source, width) : media.Thumbnail(source, width);
                    cache.Set(key, thumb, new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromHours(2) });
                }
                ctx.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
                return Results.File(thumb.Bytes, thumb.ContentType);
            }

            var found = await LoadImageAsync(id, uploads, blobs, ctx.RequestAborted);
            if (found is { } image)
            {
                ctx.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
                return Results.File(image.Bytes, image.ContentType);
            }

            var png = await FetchLegacyOrNullAsync(comfy, id, ctx.RequestAborted);
            if (png is null) return Results.NotFound(new { error = "image not found" });
            ctx.Response.Headers.CacheControl = "public, max-age=300";
            return Results.File(png, ContentTypes.ImagePng);
        });

        // A LoRA's cached CivitAI preview media (image or clip), served from this box so the browser never hotlinks the
        // CivitAI CDN. The name rides in the query — it carries subfolder slashes and isn't sensitive. A short cache,
        // not immutable: the refresh button replaces the bytes under the same name, and the client cache-busts then.
        app.MapGet(Routes.LoraPreview, async (string? name, ILoraPreviewRepository previews, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(name))
                return Results.BadRequest(new { error = "name is required" });
            var blob = await previews.GetAsync(name, ctx.RequestAborted);
            if (blob is null)
                return Results.NotFound(new { error = "no cached preview for this LoRA" });
            ctx.Response.Headers.CacheControl = "public, max-age=300";
            return Results.File(blob.Bytes, blob.ContentType);
        });

        app.MapGet(Routes.ImageInfo, async (string id, IUploadStore uploads, IImageBlobRepository blobs,
            IComfyClient comfy, IMediaProcessor media, HttpContext ctx) =>
        {
            byte[]? bytes = (await LoadImageAsync(id, uploads, blobs, ctx.RequestAborted))?.Bytes;
            bytes ??= await FetchLegacyOrNullAsync(comfy, id, ctx.RequestAborted);
            if (bytes is null) return Results.NotFound(new { error = "image not found" });
            // Not wrapped, for the same reason as the thumbnail above: these bytes were found. Bytes we are storing
            // and cannot identify are a fault on this side, and answering "404 not an identifiable image" would both
            // deny that and discard the only description of what was actually wrong with them.
            var d = media.Identify(bytes);
            return Results.Ok(new { width = d.Width, height = d.Height });
        });

        app.MapGet(Routes.ImageMp4, async (string id, int? w, IUploadStore uploads, IImageBlobRepository blobs,
            IMemoryCache cache, IMediaProcessor media, HttpContext ctx) =>
        {
            if (w is int rw && (rw < MinWidth || rw > MaxWidth))
                return Results.BadRequest(new { error = $"w must be between {MinWidth} and {MaxWidth}, got {rw}" });
            int? width = w;
            var key = $"mp4:{id}:{width}";
            if (!cache.TryGetValue(key, out MediaPayload? clip) || clip is null)
            {
                // Uploads included: a V2V edit's source is an uploaded clip the editor plays back before rendering.
                var source = await LoadImageAsync(id, uploads, blobs, ctx.RequestAborted);
                if (source is not { } clipSource) return Results.NotFound(new { error = "no video for this id" });
                if (media.IsAnimatedWebp(clipSource.Bytes))
                    clip = new MediaPayload(await media.WebpToMp4Async(clipSource.Bytes, width, ctx.RequestAborted), ContentTypes.VideoMp4);
                else if (clipSource.ContentType.StartsWith(ContentTypes.VideoPrefix, StringComparison.OrdinalIgnoreCase))
                    clip = new MediaPayload(clipSource.Bytes, clipSource.ContentType);
                else
                    return Results.NotFound(new { error = "no video for this id" });
                cache.Set(key, clip, new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromHours(2) });
            }
            ctx.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            // Without a filename the browser would name a "Save as" after the last URL segment ("mp4") and append the
            // extension -> "mp4.mp4". Name it after the id. "inline" (not "attachment") so the <video> still plays.
            var ext = clip.ContentType.StartsWith(ContentTypes.VideoPrefix, StringComparison.OrdinalIgnoreCase)
                ? clip.ContentType[ContentTypes.VideoPrefix.Length..] : "mp4";
            var name = string.Concat($"{id}.{ext}".Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_'));
            ctx.Response.Headers.ContentDisposition = $"inline; filename=\"{name}\"";
            return Results.File(clip.Bytes, clip.ContentType);
        });

        app.MapGet(Routes.ImagePalette, async (string id, IImageBlobRepository blobs, CancellationToken ct) =>
        {
            var json = await blobs.GetPaletteAsync(id, ct);
            return json is null ? Results.NotFound(new { error = "no palette for this id" }) : Results.Content(json, ContentTypes.ApplicationJson);
        });

        // The fp quantize's pooled label frequencies (JSON float array, indexed by the palette's order) — fetched
        // together with /palette so a later single-frame re-quantize can replay BOTH globals bit-exactly.
        app.MapGet(Routes.ImageFrequencies, async (string id, IImageBlobRepository blobs, CancellationToken ct) =>
        {
            var json = await blobs.GetFrequenciesAsync(id, ct);
            return json is null ? Results.NotFound(new { error = "no frequencies for this id" }) : Results.Content(json, ContentTypes.ApplicationJson);
        });

        app.MapGet(Routes.ImageParams, async (string id, HttpRequest req, IJobRepository jobs, CancellationToken ct) =>
        {
            var r = await jobs.GetRequestByImageAsync(id, ct);
            return r is { } rr && rr.OwnerUserId == OwnerOf(req)
                ? Results.Content(rr.RequestJson, ContentTypes.ApplicationJson)
                : Results.NotFound(new { error = "no params for this id" });
        });

        app.MapGet(Routes.ImageFrames, async (string id, IImageFrameRepository frames, CancellationToken ct) =>
        {
            var list = await frames.GetFramesAsync(id, ct);
            if (list.Count == 0) return Results.NotFound(new { error = "no lossless frames for this id" });
            var ms = new MemoryStream();
            using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
                for (var i = 0; i < list.Count; i++)
                {
                    var entry = zip.CreateEntry($"{i:D3}.png", System.IO.Compression.CompressionLevel.NoCompression);
                    await using var es = entry.Open();
                    await es.WriteAsync(list[i], ct);
                }
            ms.Position = 0;
            return Results.File(ms, ContentTypes.ApplicationZip);
        });

        app.MapPost(Routes.Media, async (MediaTypesRequest body, IUploadStore uploads, IImageBlobRepository blobs, CancellationToken ct) =>
        {
            // ids arrive in the request BODY, not the query string (see MediaTypesRequest): the caller asks about
            // every gateway image on the page at once, which is hundreds of ids and a URL past Kestrel's request-line
            // limit -- a GET was aborted at the connection before this handler ever ran.
            //
            // Every id asked about is answered for. Dropping any — e.g. a .Take(200) cap — would be silent AND
            // unrecoverable: the client reads the response as authoritative (media.js: `verdict.set(id, !!map[id])`),
            // so a dropped id is absent from the map, cached as false, and rendered as "not a video" for the life of
            // the page -- no loop, no scrubber, no poster. The blob lookup chunks its parameters, so an id list of any
            // size is answered in full (SQL Server caps a command at 2100 parameters).
            var list = (body?.Ids ?? Array.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (list.Count == 0) return Results.Ok(new Dictionary<string, string>());
            // In-memory uploads answer for themselves; only the rest are worth a database round trip.
            var resident = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var id in list)
                if (uploads.Get(id) is { } up)
                    resident[id] = up.ContentType;
            var stored = await blobs.GetContentTypesAsync(list.Where(id => !resident.ContainsKey(id)).ToList(), ct);
            var types = resident.Concat(stored).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            // The media KIND per id, not just is-it-a-clip: the client renders an mp4 clip differently from a webp
            // clip. An mp4 (e.g. MiniMax-H3) has NO server-side still poster — ImageSharp can't decode an mp4 — so the
            // browser paints its own first frame; a webp keeps the cheap /image/{id}?still=true poster. "image" = still.
            static string Kind(string? c) =>
                c is null ? "image"
                : c.StartsWith(ContentTypes.VideoPrefix, StringComparison.OrdinalIgnoreCase) ? "mp4"
                : string.Equals(c, ContentTypes.ImageWebp, StringComparison.OrdinalIgnoreCase) ? "webp"
                : "image";
            var map = list.ToDictionary(id => id, id => Kind(types.TryGetValue(id, out var c) ? c : null), StringComparer.Ordinal);
            return Results.Ok(map);
        });
    }

    /// <summary>Bytes for an image id from either place it can live: an in-memory upload (a render input the user
    /// just handed us) or the durable <c>dbo.ImageBlob</c> row (a generated image). Null when neither has it — the
    /// callers then try the legacy ComfyUI /view fallback for ids that predate DB-first serving.</summary>
    private static async Task<(byte[] Bytes, string ContentType)?> LoadImageAsync(
        string id, IUploadStore uploads, IImageBlobRepository blobs, CancellationToken ct)
    {
        if (uploads.Get(id) is { } upload)
            return (upload.Bytes, upload.ContentType);
        if (await blobs.GetAsync(id, ct) is { } blob)
            return (blob.Bytes, blob.ContentType);
        return null;
    }

    #endregion

    #region live progress websocket

    private static void MapProgressSocket(RouteGroupBuilder app)
    {
        // Forward the backend's own progress WebSocket, filtered to this user's jobs and translated (backend prompt_id
        // → our jobId). The SPA connects here for live progress.
        app.Map(Routes.Ws, async (HttpContext ctx, IComfyClient comfy, RenderOrchestrator queue,
            ILogger<IComfyClient> log) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            var me = OwnerOf(ctx.Request);
            using var downstream = await ctx.WebSockets.AcceptWebSocketAsync();
            WebSocket upstream;
            try { upstream = await comfy.ConnectProgressSocketAsync(ctx.RequestAborted); }
            catch (Exception ex)
            {
                // The downstream socket is already accepted, so there is no status code left to answer with — closing
                // it IS the only signal available, and the client's own reconnect loop handles that. Doing it mutely
                // would be a mistake: a backend whose progress socket had stopped accepting connections would look
                // exactly like an idle one, and the page would never show progress again.
                log.LogWarning(ex, "Could not open the ComfyUI progress socket; closing this client's /ws.");
                await downstream.CloseAsync(WebSocketCloseStatus.EndpointUnavailable,
                    CloseReasons.ProgressBackendUnavailable, CancellationToken.None);
                return;
            }
            using (upstream)
                await Task.WhenAny(PumpTranslating(upstream, downstream, queue, me, log, ctx.RequestAborted),
                                   Pump(downstream, upstream, log, ctx.RequestAborted));
        });
    }

    /// <summary>True for the exceptions that simply mean "this socket went away" — the ordinary way a pump ends, and
    /// the only thing the pumps' catch is meant to cover. Anything else is a fault in the pump itself, and gets
    /// logged instead of disappearing: under a bare catch a real bug in the translating pump would end live progress
    /// for that client and look exactly like the user closing their tab.</summary>
    private static bool IsSocketShutdown(Exception ex)
        => ex is WebSocketException or OperationCanceledException or ObjectDisposedException
           or System.IO.IOException or System.Net.Sockets.SocketException;

    private static async Task Pump(WebSocket from, WebSocket to, ILogger log, CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        try
        {
            while (from.State == WebSocketState.Open)
            {
                var r = await from.ReceiveAsync(buf, ct);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    if (to.State == WebSocketState.Open)
                        await to.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, ct);
                    return;
                }
                if (to.State == WebSocketState.Open)
                    await to.SendAsync(new ArraySegment<byte>(buf, 0, r.Count), r.MessageType, r.EndOfMessage, ct);
            }
        }
        catch (Exception ex) when (IsSocketShutdown(ex)) { /* connection dropped — let the other pump finish */ }
        catch (Exception ex) { log.LogError(ex, "Progress socket pump failed; this client's live progress has stopped."); }
    }

    private static async Task PumpTranslating(WebSocket from, WebSocket to, RenderOrchestrator queue, long me,
        ILogger log, CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        var currentIsMine = false;
        try
        {
            while (from.State == WebSocketState.Open)
            {
                var r = await from.ReceiveAsync(buf, ct);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    if (to.State == WebSocketState.Open)
                        await to.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, ct);
                    return;
                }
                if (to.State != WebSocketState.Open) continue;

                if (r.MessageType == WebSocketMessageType.Text && r.EndOfMessage)
                {
                    var text = Encoding.UTF8.GetString(buf, 0, r.Count);
                    var decision = FilterFrame(text, queue, me);
                    if (decision.OwnerIsMe.HasValue) currentIsMine = decision.OwnerIsMe.Value;
                    if (!decision.Forward) continue;
                    var outBytes = Encoding.UTF8.GetBytes(decision.OutText);
                    await to.SendAsync(outBytes, WebSocketMessageType.Text, true, ct);
                }
                else if (currentIsMine)
                {
                    await to.SendAsync(new ArraySegment<byte>(buf, 0, r.Count), r.MessageType, r.EndOfMessage, ct);
                }
            }
        }
        catch (Exception ex) when (IsSocketShutdown(ex)) { /* connection dropped — let the other pump finish */ }
        catch (Exception ex) { log.LogError(ex, "Progress socket pump failed; this client's live progress has stopped."); }
    }

    /// <summary>The decision for one upstream text frame: whether to forward it, the (id-translated) text to send, and
    /// — when the frame carries a known prompt_id — whether that prompt is mine (gates binary previews).</summary>
    private readonly record struct WsFrameDecision(bool Forward, string OutText, bool? OwnerIsMe);

    private static WsFrameDecision FilterFrame(string text, RenderOrchestrator queue, long me)
    {
        // Only a JsonException is caught, and ONLY around the parse. This is the gate that decides whether another
        // user's render progress reaches this socket; running the whole body under a bare catch whose fallback was
        // "forward as-is" would make it a privacy filter that failed OPEN on any exception at all, including one
        // thrown after the frame had been identified as somebody else's. A frame that is not JSON carries no
        // prompt_id and so is not attributable to anyone; that, and only that, is the shared-status case below.
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch (JsonException) { return new WsFrameDecision(true, text, null); }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty(JsonFields.Data, out var data) || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty(JsonFields.PromptId, out var pid) || pid.ValueKind != JsonValueKind.String)
                return new WsFrameDecision(true, text, null);        // no prompt_id — a general backend status frame

            var comfyId = pid.GetString() ?? throw new JsonException("prompt_id is present but not a string value.");
            var owner = queue.OwnerForComfy(comfyId);
            if (owner is not long o) return new WsFrameDecision(false, text, false);   // unattributable — withhold
            if (o != me) return new WsFrameDecision(false, text, false);
            var jobId = queue.JobIdForComfy(comfyId);
            return new WsFrameDecision(true, jobId is not null ? text.Replace(comfyId, jobId) : text, true);
        }
    }

    #endregion
}

/// <summary>Body of PUT /forge/catalog/binding.</summary>
/// <param name="SlotId">The model slot.</param>
/// <param name="FileName">The file to bind, or blank/null to clear it.</param>
public sealed record BindingRequest(string SlotId, string? FileName);

/// <summary>Body of PUT /forge/catalog/override.</summary>
/// <param name="ConfigId">The workflow configuration.</param>
/// <param name="Key">Namespaced setting key.</param>
/// <param name="Value">The value, or blank/null to remove the override.</param>
public sealed record OverrideRequest(string ConfigId, string Key, string? Value);
