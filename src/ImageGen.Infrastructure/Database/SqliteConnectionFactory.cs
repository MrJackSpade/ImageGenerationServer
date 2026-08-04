//TODO: CHECK FOR FALLBACKS
using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace ImageGen.Infrastructure.Database;

/// <summary>
/// The SQLite implementation of <see cref="IDbConnectionFactory"/>.
///
/// <para><b>Why the database file is ATTACHed rather than opened directly.</b> Every one of the ~130 statements in
/// this assembly names its table as <c>dbo.Something</c>, because that is what the schema is called on SQL Server.
/// SQLite has no <c>dbo</c> schema — but it does let a file be attached under a chosen name, and
/// <c>ATTACH '...' AS dbo</c> makes every one of those statements resolve unchanged. The alternative was rewriting
/// 271 table references across 96 hand-written SQL strings, i.e. touching every query in the app to add a provider
/// it does not otherwise care about. <c>SqliteAttachSpikeTests</c> pins this behaviour.</para>
///
/// <para>The consequence is that <c>main</c> is a throwaway in-memory database and <b>all</b> real tables live in the
/// attached file. That also sidesteps SQLite's rule that a transaction spanning several attached databases is not
/// atomic under WAL: no transaction here spans two files, because there is only ever one.</para>
///
/// <para>Two pragmas are not optional. <c>foreign_keys</c> defaults to OFF, and 13 foreign keys in this schema carry
/// <c>ON DELETE CASCADE</c> that the image-delete cascade depends on. <c>busy_timeout</c> is what makes a single
/// writer survive a write-heavy render loop concurrent with HTTP reads, instead of surfacing SQLITE_BUSY.</para>
/// </summary>
public sealed class SqliteConnectionFactory : IDbConnectionFactory
{
    /// <summary>How long a statement waits for the write lock before giving up. SQLite allows exactly one writer, and
    /// the render worker writes through on every slot transition while requests read, so contention is normal
    /// operation rather than an error. Deliberately generous: waiting is always better than failing a user's job.</summary>
    private const int BusyTimeoutMs = 30_000;

    private readonly string _dataSource;

    /// <summary>
    /// Builds the factory from the app's connection string. Only the <c>Data Source</c> is used — the file to attach —
    /// so the same <c>ConnectionStrings:ImageGen</c> key works for either provider.
    /// </summary>
    public SqliteConnectionFactory(string connectionString)
    {
        var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
        if (string.IsNullOrWhiteSpace(dataSource))
            throw new InvalidOperationException(
                "The SQLite connection string has no 'Data Source'. Expected something like "
                + "\"Data Source=/data/imagegen.db\".");

        _dataSource = Path.GetFullPath(dataSource);
        var directory = Path.GetDirectoryName(_dataSource);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
    }

    /// <summary>The resolved absolute path of the database file, for diagnostics and for the schema initializer.</summary>
    public string DataSource => _dataSource;

    /// <inheritdoc />
    public async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        try
        {
            await using var setup = connection.CreateCommand();
            // The path is interpolated because ATTACH will not take a parameter for it. Single quotes are doubled,
            // which is the whole of SQLite string-literal escaping.
            setup.CommandText =
                $"ATTACH DATABASE '{_dataSource.Replace("'", "''")}' AS dbo;" +
                "PRAGMA dbo.journal_mode = WAL;" +
                "PRAGMA foreign_keys = ON;" +
                $"PRAGMA busy_timeout = {BusyTimeoutMs};";
            await setup.ExecuteNonQueryAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
