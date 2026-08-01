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

    /// <summary>Whether the launcher asked for a browser. Anything but "0"/"false" counts as yes.</summary>
    public static bool Requested(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.Equals("0", StringComparison.Ordinal)
        && !value.Equals("false", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A bound address turned into one a browser can actually open. Kestrel reports the address it BINDS, and
    /// http://0.0.0.0:8080 means "every interface" — a browser cannot load it.
    /// </summary>
    public static string Reachable(string boundAddress) =>
        boundAddress
            .Replace("://0.0.0.0", "://localhost")
            .Replace("://[::]", "://localhost")
            .Replace("://*", "://localhost")
            .Replace("://+", "://localhost");

    /// <summary>Open <paramref name="url"/> in the system browser, logging rather than throwing if it will not.</summary>
    public static void Open(string url, ILogger logger)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsLinux())
                Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false });
            else if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open", url) { UseShellExecute = false });
            else
                logger.LogInformation("No way to open a browser on this platform; open {Url} yourself.", url);
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "Could not open a browser; open {Url} yourself.", url);
        }
    }
}
