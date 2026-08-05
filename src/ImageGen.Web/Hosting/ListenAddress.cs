using System.Net;
using System.Net.Sockets;

namespace ImageGen.Web.Hosting;

/// <summary>
/// Resolves the address Kestrel will bind, moving off a port that is already taken instead of refusing to start.
///
/// <para>Kestrel's own behaviour is to throw, which on a desktop box almost always means "the app is already
/// running" or "something else has 8080" — a failure the user can do nothing useful with at the moment they hit
/// it. Walking up to the next free port and saying so gets them a working app; the address is printed by Kestrel's
/// own startup line, and by the warning this emits.</para>
///
/// <para>Upward from the configured port, not an OS-assigned one: a predictable second port is findable, and
/// 8081 after 8080 is a guess a person will make. A random high port that changes on every restart is not. The
/// search is bounded by the port space itself, not by an invented number of attempts.</para>
///
/// <para>This is a bind-time race by nature — the probe and the real bind are separate syscalls — and that is
/// accepted. Losing the race means Kestrel throws exactly as it does today.</para>
/// </summary>
public static class ListenAddress
{
    /// <summary>Kestrel's "*" any-host wildcard in a listen URL.</summary>
    private const string WildcardStar = "*";

    /// <summary>Kestrel's "+" any-host wildcard in a listen URL.</summary>
    private const string WildcardPlus = "+";

    /// <summary>The IPv4 any-interface host in a listen URL.</summary>
    private const string AnyHostIPv4 = "0.0.0.0";

    /// <summary>The scheme/host separator in a listen URL.</summary>
    private const string SchemeSeparator = "://";

    /// <summary>
    /// Returns <paramref name="configuredUrls"/> unchanged when every port in it is free, otherwise the same list
    /// with each unavailable port moved to the next free one. Null/empty in, null out — the caller leaves the host
    /// to its own default.
    /// </summary>
    /// <param name="isPortFree">
    /// Injected so the walk is testable without opening sockets. Defaults to actually trying to listen.
    /// </param>
    public static string? Resolve(string? configuredUrls, Action<string, int, int>? onMoved = null, Func<IPAddress, int, bool>? isPortFree = null)
    {
        if (string.IsNullOrWhiteSpace(configuredUrls)) return configuredUrls;
        isPortFree ??= CanListen;

        List<string> resolved = new List<string>();
        foreach (string raw in configuredUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParse(raw, out string? scheme, out string? host, out int port) || isPortFree(AddressFor(host), port))
            {
                resolved.Add(raw);
                continue;
            }

            int? free = NextFreePort(AddressFor(host), port, isPortFree);
            if (free is null)
            {
                // Nothing above it is free. Hand back the original and let Kestrel report the real bind error
                // rather than inventing a different failure here.
                resolved.Add(raw);
                continue;
            }

            onMoved?.Invoke(host, port, free.Value);
            resolved.Add($"{scheme}://{host}:{free.Value}");
        }

        return string.Join(';', resolved);
    }

    /// <summary>The first free port above <paramref name="from"/>, or null if the port space runs out.</summary>
    private static int? NextFreePort(IPAddress address, int from, Func<IPAddress, int, bool> isPortFree)
    {
        for (int port = from + 1; port <= IPEndPoint.MaxPort; port++)
            if (isPortFree(address, port)) return port;
        return null;
    }

    private static bool CanListen(IPAddress address, int port)
    {
        try
        {
            using TcpListener listener = new TcpListener(address, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// What a host part in a listen URL means to a socket. "*" and "+" are Kestrel's any-host wildcards; a name
    /// probes the loopback, which is the interface a name for this box resolves to.
    /// </summary>
    private static IPAddress AddressFor(string host) =>
        host is WildcardStar or WildcardPlus or AnyHostIPv4 ? IPAddress.Any
        : IPAddress.TryParse(host, out IPAddress? parsed) ? parsed
        : IPAddress.Loopback;

    private static bool TryParse(string url, out string scheme, out string host, out int port)
    {
        scheme = host = ""; port = 0;
        int schemeEnd = url.IndexOf(SchemeSeparator, StringComparison.Ordinal);
        if (schemeEnd <= 0) return false;

        scheme = url[..schemeEnd];
        string rest = url[(schemeEnd + 3)..].TrimEnd('/');
        int portStart = rest.LastIndexOf(':');
        if (portStart < 0 || !int.TryParse(rest[(portStart + 1)..], out port)) return false;

        host = rest[..portStart];
        return host.Length > 0 && port is > 0 and <= IPEndPoint.MaxPort;
    }
}
