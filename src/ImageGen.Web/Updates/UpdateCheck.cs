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
/// Asks GitHub whether a newer release exists, refreshing the cached answer whenever a request finds it older
/// than <see cref="MaxAge"/>.
///
/// <para>The answer is held between requests and only re-fetched when a request arrives and the last check was
/// over an hour ago, so github.com is contacted at most about once an hour however many pages ask. A release
/// published while the process is up is therefore noticed on the first request past the hour — the app does not
/// have to be restarted to see it. The client re-reads the stored answer on its own short timer, so the banner
/// appears by itself once a refresh has run.</para>
///
/// <para>A failure — offline, rate-limited, or GitHub simply not answering — is the answer for that refresh,
/// not an error to raise. Nobody signed in to make pictures needs a banner about a failed version check, and
/// there is nothing they could do about it. It is logged and the page simply says nothing until the next
/// refresh is due.</para>
/// </summary>
/// <param name="running">
/// The version this process is. Injected rather than read from <see cref="AppVersion"/> inside, so that the
/// comparison can be exercised against the real releases without having to be built as one.
/// </param>
public sealed class UpdateCheck(IHttpClientFactory httpFactory, IConfiguration config, ILogger<UpdateCheck> log, Version? running)
{
    /// <summary>Turns the check off entirely. It contacts github.com, and that should be refusable.</summary>
    public const string EnabledKey = MachineSettingSpecs.UpdatesEnabled;

    /// <summary>Where releases are published. Not configurable: it is where THIS application comes from.</summary>
    private const string LatestRelease = "https://api.github.com/repos/MrJackSpade/ImageGenerationServer/releases/latest";
    private const string ReleasesPage = "https://github.com/MrJackSpade/ImageGenerationServer/releases";

    /// <summary>User-Agent sent on the GitHub API request; GitHub rejects requests without one.</summary>
    private const string UserAgent = "ImageGen";

    /// <summary>Accept header selecting GitHub's JSON media type.</summary>
    private const string GitHubJsonMediaType = "application/vnd.github+json";

    /// <summary>Release JSON property carrying the tag name.</summary>
    private const string TagNameProperty = "tag_name";

    /// <summary>Release JSON property carrying the release page URL.</summary>
    private const string HtmlUrlProperty = "html_url";

    private readonly IHttpClientFactory _httpFactory = httpFactory;
    private readonly IConfiguration _config = config;
    private readonly ILogger<UpdateCheck> _log = log;

    /// <summary>How long a fetched answer is served before a request triggers a fresh check. One hour: soon
    /// enough to notice a release the day it lands, seldom enough that github.com is barely touched.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private UpdateStatus? _answer;
    private DateTime _checkedAtUtc;

    /// <summary>
    /// The cached answer, re-fetched in place when a request finds it older than <see cref="MaxAge"/>. Concurrent
    /// requests that arrive during a refresh wait on the gate and then see the just-stored answer, rather than
    /// each launching a fetch of its own.
    /// </summary>
    public async Task<UpdateStatus> GetAsync(CancellationToken ct)
    {
        if (FreshAnswer is { } cached) return cached;

        await _gate.WaitAsync(ct);
        try
        {
            if (FreshAnswer is { } refreshed) return refreshed;   // another request refreshed it while we waited on the gate

            _answer = await AskAsync(ct);
            _checkedAtUtc = DateTime.UtcNow;
            return _answer;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The answer we already have if it was last checked within <see cref="MaxAge"/>; else null.</summary>
    private UpdateStatus? FreshAnswer =>
        _answer is { } a && DateTime.UtcNow - _checkedAtUtc < MaxAge ? a : null;

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
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            http.DefaultRequestHeaders.Accept.ParseAdd(GitHubJsonMediaType);

            using var response = await http.GetAsync(LatestRelease, ct);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogInformation("Update check answered {Status}; no update will be reported this run.", (int)response.StatusCode);
                return UpdateStatus.Nothing;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var tag = document.RootElement.TryGetProperty(TagNameProperty, out var t) ? t.GetString() : null;

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

            var url = document.RootElement.TryGetProperty(HtmlUrlProperty, out var u) ? u.GetString() : null;
            return new UpdateStatus(current.ToString(), latest.ToString(), url ?? ReleasesPage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogInformation("Update check could not run: {Reason}", ex.Message);
            return UpdateStatus.Nothing;
        }
    }
}
