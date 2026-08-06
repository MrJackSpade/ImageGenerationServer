using ImageGen.Application.Platform;
using Microsoft.Data.SqlClient;

namespace ImageGen.Infrastructure.Database;

/// <summary>
/// <see cref="IDatabaseAvailability"/> for SQL Server: recognises the failures that mean "the server could not be
/// reached", as opposed to "the server rejected this".
/// <para>Deliberately a positive list. Treating every <see cref="SqlException"/> as unreachable would turn a
/// constraint violation or a bad column into an infinite wait — a real bug, hidden forever, which is precisely the
/// outcome fail-fast exists to prevent. Anything not recognised here is reported as a genuine failure.</para>
/// </summary>
public sealed class SqlDatabaseAvailability : IDatabaseAvailability
{
    /// <summary>
    /// Connection-level SQL Server errors. These are all "we never got to run your command":
    /// <list type="bullet">
    /// <item>-2, 11 — command/login timeout expired.</item>
    /// <item>53, 40, 26, 87, 233, 258 — server not found, name resolution, instance-specific, pipe not open.</item>
    /// <item>10053, 10054, 10060, 10061 — the TCP connection was aborted, reset, timed out or refused.</item>
    /// <item>4060, 18456, 4064 — the database could not be opened / login could not be processed. Included because
    /// a server coming back up rejects logins before it accepts them, and that window is a wait, not a failure.</item>
    /// <item>121, 64, 20 — transport-level errors while sending to or receiving from the server.</item>
    /// </list>
    /// </summary>
    private static readonly HashSet<int> UnreachableNumbers =
    [
        -2, 11, 20, 26, 40, 53, 64, 87, 121, 233, 258,
        4060, 4064, 10053, 10054, 10060, 10061, 18456,
    ];

    private static class Messages
    {
        /// <summary>The fragment the connection pool puts in its message when it cannot hand out a connection.</summary>
        public const string PoolExhaustionMessageFragment = "connection from the pool";
    }

    public bool IsUnavailable(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is SqlException sql && (sql.IsTransient || sql.Errors.Cast<SqlError>().Any(x => UnreachableNumbers.Contains(x.Number))))
            {
                return true;
            }
            // A TRANSPORT failure, whatever number it wears. The number list alone is not enough and cannot be made
            // enough: SqlClient surfaces connection failures with whatever the OS reported, so "the remote computer
            // refused the network connection" arrives as Win32 1225 and a dropped link as a socket error — neither of
            // which is a SQL Server error code anyone would think to list. What they have in common is a Win32 or
            // socket exception in the chain, which a rejected COMMAND never has: the server answered that one.
            if (e is System.Net.Sockets.SocketException or System.ComponentModel.Win32Exception)
            {
                return true;
            }
            // The pool gives up as a plain InvalidOperationException when it cannot hand out a connection, and a
            // socket that never answers surfaces as a raw timeout. Both mean the same thing: no connection happened.
            if (e is TimeoutException)
            {
                return true;
            }

            if (e is InvalidOperationException io && io.Message.Contains(Messages.PoolExhaustionMessageFragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
