//TODO: CHECK FOR FALLBACKS
using ImageGen.Application.Platform;
using Microsoft.Data.Sqlite;

namespace ImageGen.Infrastructure.Database;

/// <summary>
/// <see cref="IDatabaseAvailability"/> for SQLite: recognises the failures that mean "could not get at the database",
/// as opposed to "the database rejected this".
///
/// <para>The distinction matters more here than it looks. There is no server to be down, so most of what SQL Server
/// calls unavailability cannot happen — but SQLite has its own version of it, and it is the common case rather than
/// the rare one: <b>a single writer</b>. When the render worker holds the write lock, a concurrent write gets
/// <c>SQLITE_BUSY</c>. That is a wait, not a failure, and reporting it as a failure would fail a user's job because
/// another job was mid-write. <c>busy_timeout</c> (see <see cref="SqliteConnectionFactory"/>) absorbs almost all of
/// it; this is what catches the remainder.</para>
///
/// <para>Deliberately a positive list, like its SQL Server counterpart. Treating every <see cref="SqliteException"/>
/// as unavailability would turn a constraint violation into an infinite wait and hide a real bug forever.</para>
/// </summary>
public sealed class SqliteDatabaseAvailability : IDatabaseAvailability
{
    /// <summary>
    /// SQLite primary result codes that mean the statement never got to run:
    /// <list type="bullet">
    /// <item>5 <c>SQLITE_BUSY</c> — another connection holds the write lock. Normal under a render load.</item>
    /// <item>6 <c>SQLITE_LOCKED</c> — a conflict within the same connection's shared cache.</item>
    /// <item>10 <c>SQLITE_IOERR</c> — the disk or file was not readable/writable at that moment.</item>
    /// <item>14 <c>SQLITE_CANTOPEN</c> — the file could not be opened (a volume not mounted yet, a permissions
    /// change). On a containerised install with the database on a bind mount, this is what a missing mount looks
    /// like, and it resolves without the app doing anything.</item>
    /// </list>
    /// <para>Notably absent: 11 <c>SQLITE_CORRUPT</c> and 13 <c>SQLITE_FULL</c>. Waiting does not fix either, and
    /// silently retrying a corrupt database forever is the worst possible response to it.</para>
    /// </summary>
    private static readonly HashSet<int> UnreachableCodes = [5, 6, 10, 14];

    /// <inheritdoc />
    public bool IsUnavailable(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is SqliteException sqlite && UnreachableCodes.Contains(sqlite.SqliteErrorCode))
                return true;
            // The file is on a path that went away — an unmounted volume, a removed directory. Same meaning as
            // CANTOPEN: nothing ran, and it may well be there on the next attempt.
            if (e is IOException or UnauthorizedAccessException)
                return true;
            if (e is TimeoutException)
                return true;
        }
        return false;
    }
}
