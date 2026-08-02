using System.Reflection;

namespace ImageGen.Web.Updates;

/// <summary>
/// What version of this application is running, if it is a build that knows.
///
/// <para>The version is stamped at publish time from the release tag (see <c>.github/workflows/release.yml</c>).
/// A build from a working copy has no meaningful version — it is whatever the checkout happens to be, which is
/// not a point on the release line and cannot be compared to one. Those builds report <see langword="null"/>
/// here and are never told an update exists, because the honest answer is that nobody knows.</para>
/// </summary>
public static class AppVersion
{
    /// <summary>What the SDK stamps when nobody passed a version. Present means "this build was not released".</summary>
    private const string Unstamped = "1.0.0";

    private static readonly Lazy<Version?> _current = new(Read);

    /// <summary>The running version, or null when this build carries none.</summary>
    public static Version? Current => _current.Value;

    /// <summary>The running version as text, for display. Null when this build carries none.</summary>
    public static string? CurrentText => Current?.ToString();

    private static Version? Read()
    {
        // THIS assembly, not the entry assembly. Whatever is hosting the process — the test runner, a profiler,
        // anything that runs the app as a library — has a version of its own, and reading it would report a
        // number that has nothing to do with this application. ImageGen.Web is the thing that gets released, so
        // its own stamp is the one that means anything.
        //
        // InformationalVersion rather than AssemblyVersion: it carries <Version> verbatim (plus a "+<commit>"
        // the SDK appends) instead of being truncated to four numeric parts.
        var informational = typeof(AppVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        return Parse(informational);
    }

    /// <summary>
    /// Read a version out of a tag or an informational version: a leading <c>v</c>, build metadata after
    /// <c>+</c>, and a pre-release suffix after <c>-</c> are all stripped. Anything that does not then parse as
    /// a plain numeric version returns null rather than a guess — comparing a version nobody can read is how an
    /// app tells people to upgrade to something older than what they have.
    /// </summary>
    public static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var value = text.Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];

        var plus = value.IndexOf('+');
        if (plus >= 0) value = value[..plus];

        var dash = value.IndexOf('-');
        if (dash >= 0) value = value[..dash];

        if (!Version.TryParse(value, out var parsed)) return null;

        // An unstamped build is not version 1.0.0, it is a build that was never released.
        return parsed.ToString(3) == Unstamped ? null : parsed;
    }

    /// <summary>
    /// Whether a tag/version names a PRE-RELEASE build — a <c>-suffix</c> after the numeric version (a <c>-test</c>,
    /// <c>-rc.1</c>, … tag). The update check never offers one of these as an update: a test or candidate build is not
    /// a release, even when it is published to GitHub as a normal (non-flagged) release and even when its base version
    /// sorts newer than what is running. <see cref="Parse"/> deliberately STRIPS the suffix so a pre-release build can
    /// still identify its own base version; this is the separate question of whether a tag is one.
    /// </summary>
    public static bool IsPrerelease(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];
        // Strip build metadata FIRST — it follows '+' and may itself contain '-' (e.g. a commit-ish), which is not a
        // pre-release marker. What remains carries a '-' only when there is a genuine pre-release suffix.
        var plus = value.IndexOf('+');
        if (plus >= 0) value = value[..plus];
        return value.Contains('-');
    }
}
