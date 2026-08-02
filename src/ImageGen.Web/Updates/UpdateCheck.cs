using System.Text.Json;
using ImageGen.Web.Configuration;

namespace ImageGen.Web.Updates;

/// <summary>What the app knows about newer releases. All three are null when there is nothing to say.</summary>
/// <param name="Current">The running version.</param>
/// <param name="Latest">The newest published release, when it is newer than <paramref name="Current"/>.</param>
/// <param name="Url">Where to get it.</param>
public sealed record UpdateStatus(string? Current, string? Latest, string? Url)
{
    public static readonly UpdateStatus Nothing = new(null, null, null);
}

/// <summary>
/// Asks GitHub whether a newer release exists, ONCE per process.
///
/// <para>Once, deliberately. A poll interval or a cache lifetime would be a number nobody chose, and an app that
/// contacts a third party on a timer of its own invention is doing something the operator did not ask for.
/// Starting up is an event that already happens, and a deployment restarts; that is when this asks.</para>
///
/// <para>A failure — offline, rate-limited, or a repository that answers 404 because it is private — is the
/// answer for this process, not an error to raise. Nobody signed in to make pictures needs a banner about a
/// failed version check, and there is nothing they could do about it. It is logged once and the page simply
/// says nothing.</para>
/// </summary>
/// <param name="running">
/// The version this process is. Injected rather than read from <see cref="AppVersion"/> inside, so that the
/// comparison can be exercised against the real releases without having to be built as one.
/// </param>
public sealed class UpdateCheck(IHttpClientFactory httpFactory, IConfiguration config, ILogger<UpdateCheck> log, Version? running)
{
    /// <summary>Turns the check off entirely. It contacts github.com, and that should be refusable.</summary>
    public const string EnabledKey = "Updates:Enabled";

    /// <summary>Where releases are published. Not configurable: it is where THIS application comes from.</summary>
    private const string LatestRelease = "https://api.github.com/repos/MrJackSpade/ImageGenerationServer/releases/latest";
    private const string ReleasesPage = "https://github.com/MrJackSpade/ImageGenerationServer/releases";

    /// <summary>Read when the repository is private, so the check works for whoever can already see it.</summary>
    private static readonly string[] TokenVariables = ["IMAGEGEN_GITHUB_TOKEN", "GITHUB_TOKEN"];

    private readonly IHttpClientFactory _httpFactory = httpFactory;
    private readonly IConfiguration _config = config;
    private readonly ILogger<UpdateCheck> _log = log;

    private readonly SemaphoreSlim _once = new(1, 1);
    private UpdateStatus? _answer;

    public async Task<UpdateStatus> GetAsync(CancellationToken ct)
    {
        if (_answer is not null) return _answer;

        await _once.WaitAsync(ct);
        try
        {
            return _answer ??= await AskAsync(ct);
        }
        finally
        {
            _once.Release();
        }
    }

    private async Task<UpdateStatus> AskAsync(CancellationToken ct)
    {
        var current = running;
        if (current is null)
        {
            _log.LogDebug("Update check skipped: this build carries no version, so there is nothing to compare.");
            return UpdateStatus.Nothing;
        }

        if (!_config.IsOn(EnabledKey))
        {
            _log.LogDebug("Update check skipped: {Key} is off.", EnabledKey);
            return UpdateStatus.Nothing;
        }

        try
        {
            var http = _httpFactory.CreateClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ImageGen");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            var token = TokenVariables.Select(Environment.GetEnvironmentVariable)
                                      .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            if (token is not null)
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            using var response = await http.GetAsync(LatestRelease, ct);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogInformation(
                    "Update check answered {Status}. {Hint}", (int)response.StatusCode,
                    (int)response.StatusCode == 404 && token is null
                        ? "The releases are not public to this machine; nothing further will be reported."
                        : "No update will be reported this run.");
                return UpdateStatus.Nothing;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var tag = document.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;

            // A pre-release tag (a '-suffix' build: -test, -rc.1, …) is never offered as an update. GitHub's
            // /releases/latest already skips releases FLAGGED prerelease, but a test tag published as a normal
            // release still lands here, and its base version can sort newer than the running one. The '-' in the
            // tag is the authoritative signal that it is not a release, so it is rejected regardless of the flag.
            if (AppVersion.IsPrerelease(tag))
            {
                _log.LogInformation("Update check: latest published '{Tag}' is a pre-release; not offered as an update.", tag);
                return new UpdateStatus(current.ToString(), null, null);
            }

            var latest = AppVersion.Parse(tag);

            if (latest is null)
            {
                _log.LogInformation("Update check: '{Tag}' is not a version this can compare.", tag);
                return UpdateStatus.Nothing;
            }

            if (latest <= current)
            {
                _log.LogInformation("Update check: running {Current}, latest published {Latest}. Up to date.", current, latest);
                return new UpdateStatus(current.ToString(), null, null);
            }

            _log.LogInformation("Update check: running {Current}, {Latest} is available.", current, latest);

            var url = document.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() : null;
            return new UpdateStatus(current.ToString(), latest.ToString(), url ?? ReleasesPage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogInformation("Update check could not run: {Reason}", ex.Message);
            return UpdateStatus.Nothing;
        }
    }
}
