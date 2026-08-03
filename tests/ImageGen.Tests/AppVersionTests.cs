using ImageGen.Web.Updates;

namespace ImageGen.Tests;

/// <summary>
/// What the update banner is allowed to conclude.
///
/// <para>The failure that matters is not "we missed an update" — it is telling somebody to upgrade to something
/// they already have, or to a version that does not exist, because a string was read optimistically. Every case
/// below is a shape that must produce NO version rather than a guess.</para>
/// </summary>
public sealed class AppVersionTests
{
    [Theory]
    [InlineData("v0.6.0", "0.6.0")]
    [InlineData("0.6.0", "0.6.0")]
    [InlineData("V1.2.3", "1.2.3")]
    [InlineData("0.6.0+abc123", "0.6.0")]              // SDK build metadata
    [InlineData("0.6.0-rc.1", "0.6.0")]                // pre-release suffix
    [InlineData("v2.0.1+deadbeef", "2.0.1")]
    [InlineData("  v0.7.0  ", "0.7.0")]
    public void Reads_the_version_out_of_a_tag(string text, string expected) =>
        Assert.Equal(Version.Parse(expected), AppVersion.Parse(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("latest")]
    [InlineData("v")]
    [InlineData("release-2026-07")]
    [InlineData("vNext")]
    public void Refuses_anything_it_cannot_read(string? text) => Assert.Null(AppVersion.Parse(text));

    /// <summary>
    /// 1.0.0 is what the SDK stamps when nobody passed a version, so it means "this build was never released",
    /// not "version one". Treating it as a real version would make every developer build compare itself against
    /// the published releases and claim to be out of date.
    /// </summary>
    [Theory]
    [InlineData("1.0.0")]
    [InlineData("v1.0.0")]
    [InlineData("1.0.0+7c1e2f5")]
    public void An_unstamped_build_has_no_version(string text) => Assert.Null(AppVersion.Parse(text));

    /// <summary>The comparison the banner turns on, including the case that must stay silent: running ahead.</summary>
    [Theory]
    [InlineData("0.6.0", "0.7.0", true)]
    [InlineData("0.6.0", "0.6.1", true)]
    [InlineData("0.6.0", "1.0.1", true)]
    [InlineData("0.6.0", "0.6.0", false)]              // same
    [InlineData("0.7.0", "0.6.0", false)]              // a source build ahead of the last release
    [InlineData("0.10.0", "0.9.0", false)]             // 10 > 9, not string order
    public void Only_a_strictly_newer_release_counts(string current, string latest, bool newer)
    {
        var running = AppVersion.Parse(current)!;
        var published = AppVersion.Parse(latest)!;
        Assert.Equal(newer, published > running);
    }

    /// <summary>
    /// A pre-release tag is not an update, however its base version sorts. <see cref="AppVersion.Parse"/> still
    /// strips the suffix (so a pre-release build knows its own base version), but the update check asks THIS instead
    /// and never offers a '-build'. Build metadata after '+' can contain '-' and is not a pre-release marker.
    /// </summary>
    [Theory]
    [InlineData("v0.9.1-test", true)]
    [InlineData("0.6.0-rc.1", true)]
    [InlineData("0.6.0-beta+abc", true)]
    [InlineData("v0.9.0", false)]
    [InlineData("0.6.0", false)]
    [InlineData("0.6.0+ab-cd", false)]                 // build metadata with a dash — not a pre-release
    [InlineData(null, false)]
    [InlineData("", false)]
    public void A_prerelease_tag_is_recognised_and_never_offered(string? tag, bool pre) =>
        Assert.Equal(pre, AppVersion.IsPrerelease(tag));

    /// <summary>
    /// This test suite runs from a source build, which is exactly the case that must report nothing. If this
    /// ever fails, something has started stamping a version into ordinary builds and every developer will be
    /// told to upgrade.
    /// </summary>
    [Fact]
    public void A_build_from_the_working_copy_reports_no_version() => Assert.Null(AppVersion.Current);

    /// <summary>
    /// Against the LIVE releases API: an older build is told what is available, and a build at or ahead of the
    /// newest release is told nothing. Opt-in via IMAGEGEN_UPDATE_LIVE, because it needs the network.
    /// </summary>
    [SkippableFact]
    public async Task Live_release_check_reports_newer_and_stays_quiet_otherwise()
    {
        Skip.If(Environment.GetEnvironmentVariable("IMAGEGEN_UPDATE_LIVE") is null,
            "set IMAGEGEN_UPDATE_LIVE=1 to run this against the real releases API");

        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var log = Microsoft.Extensions.Logging.Abstractions.NullLogger<UpdateCheck>.Instance;

        var behind = await new UpdateCheck(new Factory(), config, log, new Version(0, 0, 1)).GetAsync(default);
        Assert.NotNull(behind.Latest);
        Assert.NotNull(behind.Url);
        Assert.True(Version.Parse(behind.Latest!) > new Version(0, 0, 1));

        // A version nothing can be newer than. Reports the running version and no update.
        var ahead = await new UpdateCheck(new Factory(), config, log, new Version(999, 0, 0)).GetAsync(default);
        Assert.Null(ahead.Latest);

        // A build with no version never even asks.
        var unversioned = await new UpdateCheck(new Factory(), config, log, null).GetAsync(default);
        Assert.Null(unversioned.Current);
        Assert.Null(unversioned.Latest);
    }

    private sealed class Factory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
