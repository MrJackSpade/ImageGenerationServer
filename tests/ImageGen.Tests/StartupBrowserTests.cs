using ImageGen.Web.Hosting;

namespace ImageGen.Tests;

/// <summary>
/// Whether to open a browser, and at what. The launcher is the trigger — a container, a service and a scheduled
/// task all start the executable directly and must be left alone, so the default when nothing asked is NO.
/// </summary>
public sealed class StartupBrowserTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_asked_means_no_browser(string? value)
    {
        // The headless case, and the one that matters: a server restarting under systemd must not spawn browsers.
        Assert.False(StartupBrowser.Requested(value));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("False")]
    public void An_explicit_no_is_honoured(string value)
    {
        // How someone turns it off in a launcher that would otherwise ask.
        Assert.False(StartupBrowser.Requested(value));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("yes")]
    public void Anything_else_means_yes(string value) => Assert.True(StartupBrowser.Requested(value));

    [Theory]
    [InlineData("http://0.0.0.0:8080", "http://localhost:8080")]
    [InlineData("http://[::]:8080", "http://localhost:8080")]
    [InlineData("http://*:8080", "http://localhost:8080")]
    [InlineData("http://+:8080", "http://localhost:8080")]
    public void A_bind_address_becomes_one_a_browser_can_open(string bound, string expected)
    {
        // Kestrel reports what it BOUND. http://0.0.0.0:8080 means "every interface" and a browser cannot load it.
        Assert.Equal(expected, StartupBrowser.Reachable(bound));
    }

    [Theory]
    [InlineData("http://localhost:8080")]
    [InlineData("http://127.0.0.1:8099")]
    [InlineData("https://192.168.1.204:8080")]
    public void A_real_address_is_left_alone(string bound) => Assert.Equal(bound, StartupBrowser.Reachable(bound));
}
