//TODO: CHECK FOR FALLBACKS
namespace ImageGen.Web.Configuration;

/// <summary>Whether anything answered, and if not, why not.</summary>
/// <param name="Ok">A ComfyUI responded at that address.</param>
/// <param name="Error">Why it did not, in the words the caller can show. Null when <paramref name="Ok"/>.</param>
public sealed record ProbeResult(bool Ok, string? Error);

/// <summary>
/// Asks an address whether ComfyUI is behind it.
///
/// <para>"Which box answers" is the fact worth knowing about a renderer address, and the only one no amount of
/// checking the stored value can establish: a configured address can be present, well-formed and wrong, and the
/// first time anyone finds out is when a render fails minutes later.</para>
/// </summary>
public sealed class ComfyProbe(IHttpClientFactory httpFactory)
{
    /// <summary>The address a ComfyUI on this machine almost always uses — the setup page's starting suggestion.</summary>
    public const string LikelyLocal = "http://localhost:8188";

    private readonly IHttpClientFactory _httpFactory = httpFactory;

    public async Task<ProbeResult> TryAsync(string? url, CancellationToken ct)
    {
        var address = (url ?? "").Trim().TrimEnd('/');
        if (address.Length == 0) return new ProbeResult(false, "no address given");
        if (!Uri.TryCreate(address + "/system_stats", UriKind.Absolute, out var probe))
            return new ProbeResult(false, "that is not a valid address");

        // No timeout is set here: a number invented in this method is a number nobody chose. An unreachable host
        // fails on the handler's own terms, and a refused connection — the common case for "it isn't running" —
        // comes back at once.
        var http = _httpFactory.CreateClient();
        try
        {
            using var response = await http.GetAsync(probe, ct);
            return response.IsSuccessStatusCode
                ? new ProbeResult(true, null)
                : new ProbeResult(false, $"it answered {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            // Reporting the failure IS the result. This method's whole job is to answer "did it respond?", so an
            // exception is an answer of no, with the reason — not an error to swallow or to let escape.
            return new ProbeResult(false, ex.Message);
        }
    }
}
