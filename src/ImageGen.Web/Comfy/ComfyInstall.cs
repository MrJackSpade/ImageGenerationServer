//TODO: CHECK FOR FALLBACKS
using System.Text.Json;
using ImageGen.Comfy;

namespace ImageGen.Web.Comfy;

/// <summary>A ComfyUI directory, and if it is not usable, why not.</summary>
/// <param name="Root">The configured path, as given. Null when nothing is configured.</param>
/// <param name="Ok">It is there and it is a ComfyUI installation.</param>
/// <param name="Error">What is wrong with it, for the page to show. Null when <paramref name="Ok"/>.</param>
public sealed record ComfyInstallInfo(string? Root, bool Ok, string? Error);

/// <summary>
/// Where ComfyUI's files are, as distinct from where it is listening.
///
/// <para>The application has only ever known ComfyUI as a URL, which is all it needs to render. Patching is the
/// first thing that needs the installation itself, and a URL cannot supply it: the renderer may be another box
/// entirely, and no amount of asking it over HTTP reveals a path this process can write to. So it is configured,
/// separately and explicitly, and is allowed to be unset — an install pointed at somebody else's ComfyUI simply
/// has no patches to manage, which the page says rather than pretending.</para>
/// </summary>
public sealed class ComfyInstall(IConfiguration config, IHttpClientFactory httpFactory, IComfyEndpoint endpoint)
{
    /// <summary>The ComfyUI installation directory. Read live, so the settings page can change it.</summary>
    public const string PathKey = "ComfyUI:Path";

    /// <summary>The interpreter that runs it — used only to install a fetched node pack's requirements.</summary>
    public const string PythonKey = "ComfyUI:Python";

    private readonly IConfiguration _config = config;
    private readonly IHttpClientFactory _httpFactory = httpFactory;
    private readonly IComfyEndpoint _endpoint = endpoint;

    public string? Root => Blank(_config[PathKey]);

    public string? Python => Blank(_config[PythonKey]);

    /// <summary>
    /// Whether the configured path is somewhere patches can be applied. Checks for <c>main.py</c> and
    /// <c>comfy/</c> rather than mere existence: pointing this at the wrong folder and having the app write a
    /// tree of Python into it is a worse outcome than being told the path is wrong.
    /// </summary>
    public ComfyInstallInfo Describe()
    {
        var root = Root;
        if (root is null)
            return new ComfyInstallInfo(null, false,
                "No renderer folder is set. Patches change ComfyUI's own files, so this needs the directory it is "
                + "installed in — set it on Settings → This machine. Leave it empty if ComfyUI is on another machine.");

        if (!Directory.Exists(root))
            return new ComfyInstallInfo(root, false, $"{root} is not there.");

        if (!File.Exists(Path.Combine(root, "main.py")) || !Directory.Exists(Path.Combine(root, "comfy")))
            return new ComfyInstallInfo(root, false, $"{root} does not look like a ComfyUI installation — no main.py and comfy/ in it.");

        return new ComfyInstallInfo(root, true, null);
    }

    /// <summary>
    /// Ask the renderer where it is installed, and return that only if it turns out to be a ComfyUI on THIS
    /// filesystem.
    ///
    /// <para><c>/internal/folder_paths</c> answers with absolute paths on the renderer's own disk. The first
    /// entry for <c>configs</c> is always ComfyUI's own <c>models/configs</c> — <c>extra_model_paths.yaml</c> adds
    /// roots but never redirects that one — so the installation directory is its grandparent.</para>
    ///
    /// <para>The local check is not belt-and-braces, it is the whole point: a path that exists here and holds
    /// <c>main.py</c> and <c>comfy/</c> is the same installation, and one that does not means the renderer is
    /// another machine, whose disk nothing here can write to however the address is spelled. Returning null is
    /// then the correct and permanent answer.</para>
    ///
    /// <para><c>/internal/*</c> is not ComfyUI's documented API and may change, which is why this only ever
    /// SUGGESTS a value that <see cref="PathKey"/> overrides.</para>
    /// </summary>
    public async Task<string?> DetectRootAsync(CancellationToken ct)
    {
        var address = _endpoint.BaseUrl?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(address)) return null;
        if (!Uri.TryCreate(address + "/internal/folder_paths", UriKind.Absolute, out var uri)) return null;

        try
        {
            var http = _httpFactory.CreateClient();
            using var response = await http.GetAsync(uri, ct);
            if (!response.IsSuccessStatusCode) return null;

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!document.RootElement.TryGetProperty("configs", out var configs) ||
                configs.ValueKind != JsonValueKind.Array ||
                configs.GetArrayLength() == 0) return null;

            var configured = configs[0].GetString();
            if (string.IsNullOrWhiteSpace(configured)) return null;

            // <root>/models/configs -> <root>. Done with string operations because the separators are the
            // RENDERER's; on a different platform they simply will not resolve, which is the answer we want.
            var models = Path.GetDirectoryName(configured.TrimEnd('/', '\\'));
            var root = models is null ? null : Path.GetDirectoryName(models);
            if (string.IsNullOrWhiteSpace(root)) return null;

            return File.Exists(Path.Combine(root, "main.py")) && Directory.Exists(Path.Combine(root, "comfy"))
                ? root
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not being able to ask is an answer of "no", not a failure to report: an unreachable renderer, an
            // older ComfyUI without the endpoint, and a renderer on another box all mean the same thing here.
            return null;
        }
    }

    /// <summary>The root, or an exception naming what is wrong with it. For the write paths, which cannot proceed.</summary>
    public string RequireRoot()
    {
        var info = Describe();
        return info.Ok ? info.Root! : throw new InvalidOperationException(info.Error!);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
