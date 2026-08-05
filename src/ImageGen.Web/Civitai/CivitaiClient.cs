using ImageGen.Application.Civitai;
using ImageGen.Domain;
using ImageGen.Web.Configuration;
using System.Text.Json;

namespace ImageGen.Web.Civitai;

/// <summary>
/// Looks a LoRA up on CivitAI by its file hash to recover its trigger words + a preview image. Mirrors
/// <see cref="Updates.UpdateCheck"/>: an outbound HTTP call gated by a machine setting, degrading to null (never an
/// error) when it can't run. The by-hash endpoint is public — no API key needed for public models.
/// </summary>
public sealed class CivitaiClient(IHttpClientFactory httpFactory, IConfiguration config, ILogger<CivitaiClient> log) : ICivitaiClient
{
    public static class Keys
    {
        /// <summary>Opt-out (default on): turning this off stops the box contacting CivitAI at all.</summary>
        public const string EnabledKey = MachineSettingSpecs.Keys.CivitaiEnabled;
    }

    private static class Request
    {
        public const string ByHash = "https://civitai.com/api/v1/model-versions/by-hash/";

        /// <summary>User-Agent sent on CivitAI requests.</summary>
        public const string UserAgent = "ImageGen";
    }

    private static class Props
    {
        /// <summary>Model-version JSON: the containing model object.</summary>
        public const string ModelProperty = "model";

        /// <summary>Model-version JSON: a model's display name.</summary>
        public const string NameProperty = "name";

        /// <summary>Model-version JSON: the array of trigger words.</summary>
        public const string TrainedWordsProperty = "trainedWords";

        /// <summary>Model-version JSON: the array of preview images.</summary>
        public const string ImagesProperty = "images";

        /// <summary>Model-version JSON: an image's URL.</summary>
        public const string UrlProperty = "url";
    }

    private static class MimeTypes
    {
        /// <summary>The generic media type a CDN sends when it declares nothing specific.</summary>
        public const string OctetStreamMediaType = "application/octet-stream";
    }

    public bool IsEnabled() => config.IsOn(Keys.EnabledKey);

    public async Task<CivitaiLoraInfo?> LookupByHashAsync(string sha256, CancellationToken ct)
    {
        if (!config.IsOn(Keys.EnabledKey) || string.IsNullOrWhiteSpace(sha256))
            return null;

        try
        {
            HttpClient http = httpFactory.CreateClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd(Request.UserAgent);
            using HttpResponseMessage resp = await http.GetAsync(Request.ByHash + Uri.EscapeDataString(sha256), ct);
            if (!resp.IsSuccessStatusCode)
                return null;   // 404 = not published on CivitAI; anything else = nothing to add this run

            using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            JsonElement root = doc.RootElement;

            string? model = root.TryGetProperty(Props.ModelProperty, out JsonElement m) && m.TryGetProperty(Props.NameProperty, out JsonElement mn)
                ? mn.GetString() : null;

            List<string> words = new List<string>();
            if (root.TryGetProperty(Props.TrainedWordsProperty, out JsonElement tw) && tw.ValueKind == JsonValueKind.Array)
                foreach (JsonElement w in tw.EnumerateArray())
                    if (w.GetString() is { Length: > 0 } s) words.Add(s);

            string? preview = null;
            if (root.TryGetProperty(Props.ImagesProperty, out JsonElement imgs) && imgs.ValueKind == JsonValueKind.Array)
                foreach (JsonElement img in imgs.EnumerateArray())
                    if (img.TryGetProperty(Props.UrlProperty, out JsonElement u) && u.GetString() is { Length: > 0 } url) { preview = url; break; }

            return new CivitaiLoraInfo(model, words, preview);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogInformation("CivitAI lookup for {Hash} could not run: {Reason}", sha256, ex.Message);
            return null;
        }
    }

    public async Task<CivitaiPreview?> DownloadPreviewAsync(string url, CancellationToken ct)
    {
        if (!config.IsOn(Keys.EnabledKey) || string.IsNullOrWhiteSpace(url))
            return null;

        try
        {
            HttpClient http = httpFactory.CreateClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd(Request.UserAgent);
            using HttpResponseMessage resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                return null;

            byte[] bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length == 0)
                return null;
            // Trust the CDN's declared type; fall back to the URL's extension (some CivitAI clips are .mp4). The
            // browser needs this to decide <img> vs <video>, so a wrong guess would render an mp4 as a broken image.
            string? contentType = resp.Content.Headers.ContentType?.MediaType;
            if (string.IsNullOrWhiteSpace(contentType) || contentType == MimeTypes.OctetStreamMediaType)
                contentType = GuessContentType(url);
            return new CivitaiPreview(bytes, contentType);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogInformation("CivitAI preview {Url} could not be fetched: {Reason}", url, ex.Message);
            return null;
        }
    }

    private static string GuessContentType(string url)
    {
        string path = Uri.TryCreate(url, UriKind.Absolute, out Uri? u) ? u.AbsolutePath : url;
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            MediaFileExtensions.Mp4 => "video/mp4",
            MediaFileExtensions.Webm => "video/webm",
            MediaFileExtensions.Png => "image/png",
            MediaFileExtensions.Webp => "image/webp",
            MediaFileExtensions.Gif => "image/gif",
            _ => "image/jpeg",
        };
    }
}
