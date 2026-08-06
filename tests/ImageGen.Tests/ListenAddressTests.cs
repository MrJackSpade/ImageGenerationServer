using ImageGen.Web.Hosting;
using System.Net;

namespace ImageGen.Tests;

/// <summary>
/// Choosing the port to bind. The socket probe is injected, so these assert the WALK — which port is picked and
/// what is left alone — without depending on what happens to be listening on the machine running the tests.
/// </summary>
public sealed class ListenAddressTests
{
    /// <summary>A probe where the named ports are taken and everything else is free.</summary>
    private static Func<IPAddress, int, bool> Taken(params int[] ports) => (_, port) => !ports.Contains(port);

    [Fact]
    public void A_free_port_is_left_exactly_as_configured()
    {
        string? resolved = ListenAddress.Resolve("http://0.0.0.0:8080", isPortFree: Taken());

        Assert.Equal("http://0.0.0.0:8080", resolved);
    }

    [Fact]
    public void A_taken_port_moves_to_the_next_one_up()
    {
        // Upward, not OS-assigned: 8081 after 8080 is a guess a person will make.
        string? resolved = ListenAddress.Resolve("http://0.0.0.0:8080", isPortFree: Taken(8080));

        Assert.Equal("http://0.0.0.0:8081", resolved);
    }

    [Fact]
    public void It_keeps_walking_past_a_run_of_taken_ports()
    {
        string? resolved = ListenAddress.Resolve("http://0.0.0.0:8080", isPortFree: Taken(8080, 8081, 8082));

        Assert.Equal("http://0.0.0.0:8083", resolved);
    }

    [Fact]
    public void Moving_is_reported()
    {
        (string Host, int Wanted, int Actual)? moved = null;

        _ = ListenAddress.Resolve("http://0.0.0.0:8080", onMoved: (h, w, a) => moved = (h, w, a), isPortFree: Taken(8080));

        // The address is the one thing the user needs after this, so a silent move would be the worst outcome.
        Assert.Equal(("0.0.0.0", 8080, 8081), moved);
    }

    [Fact]
    public void Nothing_is_reported_when_nothing_moved()
    {
        bool moved = false;

        _ = ListenAddress.Resolve("http://0.0.0.0:8080", onMoved: (_, _, _) => moved = true, isPortFree: Taken());

        Assert.False(moved);
    }

    [Fact]
    public void Each_url_in_a_list_is_resolved_independently()
    {
        string? resolved = ListenAddress.Resolve(
            "http://0.0.0.0:8080;https://0.0.0.0:8443", isPortFree: Taken(8443));

        Assert.Equal("http://0.0.0.0:8080;https://0.0.0.0:8443".Replace("8443", "8444"), resolved);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unset_value_is_handed_back_untouched(string? configured) =>
        // Null means the host uses its own default; inventing an address here would override that silently.
        Assert.Equal(configured, ListenAddress.Resolve(configured, isPortFree: Taken()));

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("http://0.0.0.0")]          // no port
    [InlineData("http://0.0.0.0:port")]     // unparseable port
    public void Anything_it_cannot_parse_is_passed_through_unchanged(string configured) =>
        // Kestrel owns the URL grammar. A form this does not recognise is Kestrel's to accept or reject, and
        // rewriting it on a guess would turn a clear error into a mystery.
        Assert.Equal(configured, ListenAddress.Resolve(configured, isPortFree: Taken()));

    [Fact]
    public void The_original_is_kept_when_nothing_above_the_port_is_free()
    {
        // Then Kestrel throws its own bind error, which is the accurate one — better than an invented failure here.
        string? resolved = ListenAddress.Resolve("http://0.0.0.0:65535", isPortFree: (_, _) => false);

        Assert.Equal("http://0.0.0.0:65535", resolved);
    }

    [Fact]
    public void A_wildcard_host_is_preserved_when_the_port_moves()
    {
        string? resolved = ListenAddress.Resolve("http://*:8080", isPortFree: Taken(8080));

        Assert.Equal("http://*:8081", resolved);
    }
}
