using System.Diagnostics;

namespace ImageGen.Web.Hosting;

/// <summary>
/// Opens the app in a browser once it is listening, so the first thing after a double-click is the app rather
/// than a URL to copy.
///
/// <para><b>Only when the launcher asked for it.</b> The launcher — start.bat / start.sh — is the interactive way
/// in, and it sets IMAGEGEN_OPEN_BROWSER; nothing else does. A container, a service, a scheduled task and
/// `dotnet run` all start the executable directly and are left alone by construction. That is a real distinction
/// rather than a guess at whether a desktop is present, and it means a headless box cannot start spawning
/// browsers on every restart. Set IMAGEGEN_OPEN_BROWSER=0 to turn it off in a launcher that would otherwise.</para>
///
/// <para>Best effort, and the failure is reported rather than swallowed: the address has already been printed, so
/// a browser that will not open costs a click, not the session.</para>
/// </summary>
public static class StartupBrowser
{
    public const string EnvVar = "IMAGEGEN_OPEN_BROWSER";

    /// <summary>Env value meaning "off", numeric form.</summary>
    private const string DisabledNumeric = "0";

    /// <summary>Env value meaning "off", boolean form.</summary>
    private const string DisabledBoolean = "false";

    /// <summary>Scheme separator plus the IPv4 any-interface host a browser cannot load.</summary>
    private const string AnyHostIPv4 = "://0.0.0.0";

    /// <summary>Scheme separator plus the IPv6 any-interface host.</summary>
    private const string AnyHostIPv6 = "://[::]";

    /// <summary>Scheme separator plus Kestrel's "*" any-host wildcard.</summary>
    private const string AnyHostStar = "://*";

    /// <summary>Scheme separator plus Kestrel's "+" any-host wildcard.</summary>
    private const string AnyHostPlus = "://+";

    /// <summary>Scheme separator plus the loopback host a browser can actually open.</summary>
    private const string LoopbackHost = "://localhost";

    /// <summary>Command that opens a URL in the default browser on Linux.</summary>
    private const string LinuxOpenCommand = "xdg-open";

    /// <summary>Command that opens a URL in the default browser on macOS.</summary>
    private const string MacOpenCommand = "open";

    /// <summary>Whether the launcher asked for a browser. Anything but "0"/"false" counts as yes.</summary>
    public static bool Requested(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.Equals(DisabledNumeric, StringComparison.Ordinal)
        && !value.Equals(DisabledBoolean, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A bound address turned into one a browser can actually open. Kestrel reports the address it BINDS, and
    /// http://0.0.0.0:8080 means "every interface" — a browser cannot load it.
    /// </summary>
    public static string Reachable(string boundAddress) =>
        boundAddress
            .Replace(AnyHostIPv4, LoopbackHost)
            .Replace(AnyHostIPv6, LoopbackHost)
            .Replace(AnyHostStar, LoopbackHost)
            .Replace(AnyHostPlus, LoopbackHost);

    /// <summary>Open <paramref name="url"/> in the system browser, logging rather than throwing if it will not.</summary>
    public static void Open(string url, ILogger logger)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsLinux())
                Process.Start(new ProcessStartInfo(LinuxOpenCommand, url) { UseShellExecute = false });
            else if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo(MacOpenCommand, url) { UseShellExecute = false });
            else
                logger.LogInformation("No way to open a browser on this platform; open {Url} yourself.", url);
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "Could not open a browser; open {Url} yourself.", url);
        }
    }
}
