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
    private static readonly Lazy<Version?> _current = new(Read);

    /// <summary>The running version, or null when this build carries none.</summary>
    public static Version? Current => _current.Value;

    /// <summary>The running version as text, for display. Null when this build carries none.</summary>
    public static string? CurrentText => Current?.ToString();

    private static readonly Lazy<string?> _display = new(ReadDisplay);

    /// <summary>
    /// The stamped version as it was tagged, for display: the pre-release suffix is KEPT (a box running
    /// <c>0.13.5-test</c> should say so — <see cref="CurrentText"/> strips it because version COMPARISON must), the
    /// SDK's <c>+&lt;commit&gt;</c> build metadata is dropped. Null when this build carries none.
    /// </summary>
    public static string? DisplayText => _display.Value;

    private static string? ReadDisplay()
    {
        string? informational = Informational();
        // Parse decides whether this build HAS a version (absent / unstamped-sentinel → null, malformed → throw);
        // only the display FORM differs from it.
        if (informational is null || Parse(informational) is null)
        {
            return null;
        }

        string value = informational.Trim();
        int plus = value.IndexOf('+');
        return plus >= 0 ? value[..plus] : value;
    }

    /// <summary>Reads the stamp of THIS assembly, not the entry assembly. Whatever is hosting the process — the test
    /// runner, a profiler, anything that runs the app as a library — has a version of its own, and reading it would
    /// report a number that has nothing to do with this application. ImageGen.Web is the thing that gets released, so
    /// its own stamp is the one that means anything.</summary>
    private static Version? Read() => Parse(Informational());

    /// <summary>InformationalVersion rather than AssemblyVersion: it carries <c>&lt;Version&gt;</c> verbatim (plus a
    /// <c>+&lt;commit&gt;</c> the SDK appends) instead of being truncated to four numeric parts.</summary>
    private static string? Informational() => typeof(AppVersion).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    /// <summary>
    /// Read a version out of a tag or an informational version: a leading <c>v</c>, build metadata after
    /// <c>+</c>, and a pre-release suffix after <c>-</c> are all stripped. Null ONLY for a genuinely absent value
    /// (null/blank) or the SDK's unstamped <c>1.0.0</c> sentinel — both real "this is not a released version" states.
    /// A value that is PRESENT but does not parse as a version is malformed input and THROWS: laundering a broken tag
    /// or build stamp into "no version" would hide the defect. A caller for which a single bad tag must not abort the
    /// run (see <c>UpdateCheck</c>) catches it there — that is the caller's decision, not this method's contract.
    /// </summary>
    public static Version? Parse(string? text)
    {
        // Genuinely absent — nothing to read. A real, distinct "no version here" state (an unstamped build, a release
        // with no tag), NOT a parse failure to be conflated with the malformed case below.
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string value = text.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        int plus = value.IndexOf('+');
        if (plus >= 0)
        {
            value = value[..plus];
        }

        int dash = value.IndexOf('-');
        if (dash >= 0)
        {
            value = value[..dash];
        }

        // Present but not a version is MALFORMED, not "no version": a version string that will not parse is a broken
        // tag or a broken build stamp, and returning null here would launder that into silence. Surface it.
        if (!Version.TryParse(value, out Version? parsed))
        {
            throw new FormatException(
                $"'{text}' is not a version — '{value}' does not parse as major.minor[.build[.revision]].");
        }

        // The SDK stamps 1.0.0 when nobody set a version: a build that was never released, not a version to compare.
        // Distinct from the malformed case above — 1.0.0 parses fine, so it is a sentinel that maps to null, not a failure.
        return parsed.ToString(3) == Sentinels.Unstamped ? null : parsed;
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
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string value = text.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }
        // Strip build metadata FIRST — it follows '+' and may itself contain '-' (e.g. a commit-ish), which is not a
        // pre-release marker. What remains carries a '-' only when there is a genuine pre-release suffix.
        int plus = value.IndexOf('+');
        if (plus >= 0)
        {
            value = value[..plus];
        }

        return value.Contains('-');
    }

    /// <summary>The SDK's unstamped-version sentinel.</summary>
    private static class Sentinels
    {
        /// <summary>What the SDK stamps when nobody passed a version. Present means "this build was not released".</summary>
        public const string Unstamped = "1.0.0";
    }
}
