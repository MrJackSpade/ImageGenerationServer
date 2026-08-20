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
    /// <item>4060 — the requested database is temporarily unavailable while the server itself is reachable.</item>
    /// <item>121, 64, 20 — transport-level errors while sending to or receiving from the server.</item>
    /// </list>
    /// </summary>
    private static readonly HashSet<int> UnreachableNumbers =
    [
        -2, 11, 20, 26, 40, 53, 64, 87, 121, 233, 258,
        4060, 10053, 10054, 10060, 10061,
    ];

    /// <summary>The server answered and rejected the login. These override SqlClient's broad IsTransient flag: a
    /// permanent password/default-database error must never enter the unbounded outage retry loop.</summary>
    private static readonly HashSet<int> RejectedNumbers = [4064, 18456];

    /// <summary>Native transport failures SqlClient may wrap as a bare Win32Exception instead of a SocketException.
    /// This is deliberately not "any Win32 error": SSPI/Kerberos authentication rejection uses the same exception
    /// type with different codes and must fail.</summary>
    private static readonly HashSet<int> UnreachableNativeNumbers =
    [
        53, 64, 121, 258, 1225, 1231, 1232,
        10053, 10054, 10060, 10061,
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
            if (e is SqlException sql)
            {
                int[] numbers = [.. sql.Errors.Cast<SqlError>().Select(x => x.Number)];
                if (!numbers.Any(RejectedNumbers.Contains)
                    && (sql.IsTransient || numbers.Any(IsUnavailableNumber)))
                {
                    return true;
                }
            }
            // A TRANSPORT failure, whatever number it wears. The number list alone is not enough and cannot be made
            // enough: SqlClient surfaces connection failures with whatever the OS reported, so "the remote computer
            // refused the network connection" arrives as Win32 1225 and a dropped link as a socket error — neither of
            // which is a SQL Server error code anyone would think to list. What they have in common is a Win32 or
            // socket exception in the chain. Only the socket-specific shape proves transport failure; a bare Win32
            // error can instead be SSPI rejecting the login after the server answered.
            // A SocketException is a Win32Exception subclass, but the converse is not true. SqlClient also wraps
            // some real network failures (notably connection-refused 1225) as bare Win32Exception, so classify only
            // the explicit transport codes. SSPI/Kerberos rejection remains outside the list and fails immediately.
            if (e is System.Net.Sockets.SocketException
                || e is System.ComponentModel.Win32Exception win32
                && UnreachableNativeNumbers.Contains(win32.NativeErrorCode))
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

    /// <summary>Kept separate so the permanent-auth boundary has direct unit coverage without manufacturing a
    /// <see cref="SqlException"/>, whose provider-owned constructors are intentionally non-public.</summary>
    internal static bool IsUnavailableNumber(int number) => UnreachableNumbers.Contains(number);
}
