using System.Data.Common;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace ImageGen.Infrastructure.Database;

/// <summary>
/// Applies the embedded schema for the configured provider on startup. Both scripts are append-only histories that
/// re-run in full every time and are safe to do so.
///
/// <para>How much this is relied upon differs by provider, which is why <c>Database:EnsureSchemaOnStartup</c>
/// defaults differently for each. Under SQL Server it is off: the app's login deliberately has no DDL rights, and
/// the schema is applied out-of-band by an elevated <c>sqlcmd</c> (see the operator's own tooling). Under SQLite
/// there is no login, no elevation and no server — the file either has the tables or the app cannot run — so this
/// IS the mechanism, and it runs by default.</para>
///
/// <para><b>The SQLite script is version-segregated and append-only</b> (see the header of <c>schema.sqlite.sql</c>):
/// one block per released version, replayed top to bottom against whatever database the user already has. Every
/// statement is idempotent — tables and indexes via <c>IF NOT EXISTS</c>, and a new column via a plain
/// <c>ALTER TABLE … ADD COLUMN</c> that this runner executes ONLY when the column is absent, because SQLite has no
/// <c>ADD COLUMN IF NOT EXISTS</c>. That guard is the whole reason an existing 0.9 database gains a later version's
/// columns instead of the app starting and then failing on the first query that names one.</para>
/// </summary>
public sealed class DatabaseInitializer(IDbConnectionFactory connectionFactory, DatabaseProvider provider)
{
    private readonly IDbConnectionFactory _connectionFactory = connectionFactory;
    private readonly DatabaseProvider _provider = provider;

    /// <summary>Embedded schema resources, matched by suffix in <see cref="ReadEmbeddedSchema"/>.</summary>
    private const string SqliteSchemaResource = "schema.sqlite.sql";
    private const string SqlServerSchemaResource = "schema.sql";

    /// <summary>Line-comment marker stripped before the SQLite script is split into statements.</summary>
    private const string LineCommentPrefix = "--";

    /// <summary>Named groups of <see cref="AddColumn"/>, read back off a successful match.</summary>
    private const string TableGroup = "table";
    private const string ColumnGroup = "col";

    /// <summary>An additive column: <c>ALTER TABLE [dbo.]Table ADD [COLUMN] Name …</c>. Matched so the runner can
    /// skip it when the column already exists — the one statement in the SQLite script that is not self-idempotent.</summary>
    private static readonly Regex AddColumn = new(
        $@"^\s*ALTER\s+TABLE\s+(?:(?<schema>\w+)\s*\.\s*)?(?<{TableGroup}>\w+)\s+ADD\s+(?:COLUMN\s+)?(?<{ColumnGroup}>\w+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Create anything the configured provider's schema says is missing.</summary>
    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        await using var connection = await _connectionFactory.OpenAsync(ct);
        if (_provider == DatabaseProvider.Sqlite)
            await ApplySqliteAsync(connection, ReadEmbeddedSchema(SqliteSchemaResource), ct);
        else
            await ApplySqlServerAsync(connection, ReadEmbeddedSchema(SqlServerSchemaResource), ct);
    }

    /// <summary>
    /// SQL Server: batches separated by a lone <c>GO</c>. Its guarded <c>ALTER</c>s must not be PARSED until the
    /// preceding batch has actually added the column, so each batch is executed as its own command.
    /// </summary>
    private static async Task ApplySqlServerAsync(DbConnection connection, string script, CancellationToken ct)
    {
        foreach (var batch in SplitBatches(script))
        {
            await using var command = connection.Command(batch);
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// SQLite: one append-only, version-segregated script (see <c>schema.sqlite.sql</c>) replayed in full every
    /// startup. Statements run one at a time so an <c>ADD COLUMN</c> can be skipped when the column is already
    /// present — SQLite has no <c>ADD COLUMN IF NOT EXISTS</c>, and this is what makes the replay idempotent.
    /// </summary>
    private static async Task ApplySqliteAsync(DbConnection connection, string script, CancellationToken ct)
    {
        foreach (var statement in SqliteStatements(script))
        {
            var add = AddColumn.Match(statement);
            if (add.Success && await SqliteColumnExistsAsync(connection, add.Groups[TableGroup].Value, add.Groups[ColumnGroup].Value, ct))
                continue;   // already applied by an earlier run or an earlier version — replaying it is a no-op, not an error

            await using var command = connection.Command(statement);
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<bool> SqliteColumnExistsAsync(DbConnection connection, string table, string column, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        // The real tables live in the attached `dbo` schema (SqliteConnectionFactory), so table_info is asked of dbo.
        // `table` is a bare identifier parsed out of our own schema file, never anything a user supplied.
        command.CommandText = $"PRAGMA dbo.table_info({table});";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))   // (cid, name, …) — name is ordinal 1
                return true;
        return false;
    }

    private static string ReadEmbeddedSchema(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        // EndsWith with the '.' guard, so "schema.sql" cannot also match "schema.sqlite.sql".
        var name = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase));
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded schema resource '{name}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Split the SQLite script into individual statements. Line comments are stripped FIRST: a <c>--</c> comment can
    /// contain a <c>;</c> (the type-mapping note) and the <c>-- --- x.y.z</c> block banners are comments too. What
    /// remains is pure DDL whose only <c>;</c> terminate statements — this file has no string literal containing
    /// <c>;</c> or <c>--</c>, so the split is exact. Running statements individually is what lets an ADD COLUMN be
    /// guarded; Microsoft.Data.Sqlite would otherwise run the whole text as one command.
    /// </summary>
    private static IEnumerable<string> SqliteStatements(string script)
    {
        var code = new StringBuilder(script.Length);
        foreach (var line in script.Split('\n'))
        {
            var comment = line.IndexOf(LineCommentPrefix, StringComparison.Ordinal);
            code.Append(comment >= 0 ? line[..comment] : line).Append('\n');
        }

        foreach (var raw in code.ToString().Split(';'))
        {
            var statement = raw.Trim();
            if (statement.Length > 0)
                yield return statement;
        }
    }

    /// <summary>
    /// Split a script into separately-executed batches on lines consisting solely of <c>GO</c>.
    ///
    /// <para><c>GO</c> is a client-side batch separator, not SQL, and only <c>schema.sql</c> uses it — its guarded
    /// <c>ALTER</c>s must not be parsed until the preceding batch has actually added the column.</para>
    /// </summary>
    private static IEnumerable<string> SplitBatches(string script)
    {
        foreach (var batch in script.Split(["\nGO\n", "\nGO\r\n", "\r\nGO\r\n"], StringSplitOptions.None))
        {
            var trimmed = batch.Trim();
            if (trimmed.Length > 0)
                yield return trimmed;
        }
    }
}
