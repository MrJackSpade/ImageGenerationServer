using System.Text.Json;
using ImageGen.Application.Civitai;
using ImageGen.Web.Configuration;

namespace ImageGen.Web.Civitai;

/// <summary>
/// Looks a LoRA up on CivitAI by its file hash to recover its trigger words + a preview image. Mirrors
/// <see cref="Updates.UpdateCheck"/>: an outbound HTTP call gated by a machine setting, degrading to null (never an
/// error) when it can't run. The by-hash endpoint is public — no API key needed for public models.
/// </summary>
public sealed class CivitaiClient(IHttpClientFactory httpFactory, IConfiguration config, ILogger<CivitaiClient> log) : ICivitaiClient
{
    /// <summary>Opt-out (default on): turning this off stops the box contacting CivitAI at all.</summary>
    public const string EnabledKey = MachineSettingSpecs.CivitaiEnabled;

    private const string ByHash = "https://civitai.com/api/v1/model-versions/by-hash/";

    /// <summary>User-Agent sent on CivitAI requests.</summary>
    private const string UserAgent = "ImageGen";

    /// <summary>Model-version JSON: the containing model object.</summary>
    private const string ModelProperty = "model";

    /// <summary>Model-version JSON: a model's display name.</summary>
    private const string NameProperty = "name";

    /// <summary>Model-version JSON: the array of trigger words.</summary>
    private const string TrainedWordsProperty = "trainedWords";

    /// <summary>Model-version JSON: the array of preview images.</summary>
    private const string ImagesProperty = "images";

    /// <summary>Model-version JSON: an image's URL.</summary>
    private const string UrlProperty = "url";

    /// <summary>The generic media type a CDN sends when it declares nothing specific.</summary>
    private const string OctetStreamMediaType = "application/octet-stream";

    /// <summary>Preview file extension: MPEG-4 video.</summary>
    private const string Mp4Extension = ".mp4";

    /// <summary>Preview file extension: WebM video.</summary>
    private const string WebmExtension = ".webm";

    /// <summary>Preview file extension: PNG image.</summary>
    private const string PngExtension = ".png";

    /// <summary>Preview file extension: WebP image.</summary>
    private const string WebpExtension = ".webp";

    /// <summary>Preview file extension: GIF image.</summary>
    private const string GifExtension = ".gif";

    public bool IsEnabled() => config.IsOn(EnabledKey);

    public async Task<CivitaiLoraInfo?> LookupByHashAsync(string sha256, CancellationToken ct)
    {
        if (!config.IsOn(EnabledKey) || string.IsNullOrWhiteSpace(sha256))
            return null;

        try
        {
            var http = httpFactory.CreateClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            using var resp = await http.GetAsync(ByHash + Uri.EscapeDataString(sha256), ct);
            if (!resp.IsSuccessStatusCode)
                return null;   // 404 = not published on CivitAI; anything else = nothing to add this run

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            var model = root.TryGetProperty(ModelProperty, out var m) && m.TryGetProperty(NameProperty, out var mn)
                ? mn.GetString() : null;

            var words = new List<string>();
            if (root.TryGetProperty(TrainedWordsProperty, out var tw) && tw.ValueKind == JsonValueKind.Array)
                foreach (var w in tw.EnumerateArray())
                    if (w.GetString() is { Length: > 0 } s) words.Add(s);

            string? preview = null;
            if (root.TryGetProperty(ImagesProperty, out var imgs) && imgs.ValueKind == JsonValueKind.Array)
                foreach (var img in imgs.EnumerateArray())
                    if (img.TryGetProperty(UrlProperty, out var u) && u.GetString() is { Length: > 0 } url) { preview = url; break; }

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
        if (!config.IsOn(EnabledKey) || string.IsNullOrWhiteSpace(url))
            return null;

        try
        {
            var http = httpFactory.CreateClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                return null;

            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length == 0)
                return null;
            // Trust the CDN's declared type; fall back to the URL's extension (some CivitAI clips are .mp4). The
            // browser needs this to decide <img> vs <video>, so a wrong guess would render an mp4 as a broken image.
            var contentType = resp.Content.Headers.ContentType?.MediaType;
            if (string.IsNullOrWhiteSpace(contentType) || contentType == OctetStreamMediaType)
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
        var path = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath : url;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            Mp4Extension => "video/mp4",
            WebmExtension => "video/webm",
            PngExtension => "image/png",
            WebpExtension => "image/webp",
            GifExtension => "image/gif",
            _ => "image/jpeg",
        };
    }
}
