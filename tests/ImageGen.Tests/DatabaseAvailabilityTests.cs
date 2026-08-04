//TODO: CHECK FOR FALLBACKS
using ImageGen.Application.Platform;
using ImageGen.Infrastructure.Database;
using Microsoft.Data.SqlClient;

namespace ImageGen.Tests;

/// <summary>
/// The one question that decides whether accepted work waits or dies: is the database out of reach, or was this
/// operation wrong?
///
/// <para>Both directions are dangerous. Answering "unavailable" too readily turns a real bug — a constraint
/// violation, a bad column — into an infinite, silent wait, which is precisely what fail-fast exists to prevent.
/// Answering it too rarely throws away a finished render because the machine drove out of range of the server.</para>
/// </summary>
public sealed class DatabaseAvailabilityTests
{
    private readonly IDatabaseAvailability _availability = new SqlDatabaseAvailability();

    /// <summary>
    /// The real thing, end to end: a server that cannot be reached must be recognised from the exception it actually
    /// throws, not from a list someone hoped matched. Port 9 (discard) is closed on a normal box, so the connection is
    /// refused immediately and offline.
    /// </summary>
    [Fact]
    public async Task An_unreachable_server_is_unavailable()
    {
        await using var conn = new SqlConnection(
            "Server=127.0.0.1,9;Database=nope;Trusted_Connection=True;TrustServerCertificate=True;Encrypt=False");

        var ex = await Record.ExceptionAsync(() => conn.OpenAsync());

        Assert.NotNull(ex);
        Assert.True(_availability.IsUnavailable(ex!), $"a refused connection should read as unavailable, got: {ex}");
    }

    /// <summary>An ordinary bug is not an outage. If this ever starts answering true, the render path stops failing
    /// on real errors and starts waiting for them forever instead.</summary>
    [Theory]
    [MemberData(nameof(NotOutages))]
    public void An_ordinary_failure_is_not_unavailable(Exception ex) =>
        Assert.False(_availability.IsUnavailable(ex));

    public static TheoryData<Exception> NotOutages() =>
    [
        new Exception("something went wrong"),
        new InvalidOperationException("the sequence contains no elements"),
        new ArgumentException("bad argument"),
        new NullReferenceException(),
        new Exception("outer", new ArgumentOutOfRangeException()),
    ];

    /// <summary>A socket that never answers surfaces as a bare timeout — no connection happened, so it is a wait.</summary>
    [Fact]
    public void A_timeout_is_unavailable() =>
        Assert.True(_availability.IsUnavailable(new TimeoutException()));

    /// <summary>The connection pool gives up as a plain InvalidOperationException; that is still "no connection".</summary>
    [Fact]
    public void A_pool_exhaustion_is_unavailable() =>
        Assert.True(_availability.IsUnavailable(new InvalidOperationException(
            "Timeout expired. The timeout period elapsed prior to obtaining a connection from the pool.")));

    /// <summary>Failures arrive wrapped, so the whole chain is examined rather than only what was thrown last.</summary>
    [Fact]
    public void A_wrapped_outage_is_still_an_outage() =>
        Assert.True(_availability.IsUnavailable(
            new InvalidOperationException("while saving", new TimeoutException())));
}
