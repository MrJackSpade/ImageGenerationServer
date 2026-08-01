using System.Reflection;

namespace ImageGen.Infrastructure.Database;

/// <summary>
/// Applies the embedded schema for the configured provider. Both scripts are idempotent, so this is safe to run on
/// every startup.
///
/// <para>How much this is relied upon differs by provider, which is why <c>Database:EnsureSchemaOnStartup</c>
/// defaults differently for each. Under SQL Server it is off: the app's login deliberately has no DDL rights, and
/// the schema is applied out-of-band by an elevated <c>sqlcmd</c> (see the operator's own tooling). Under SQLite
/// there is no login, no elevation and no server — the file either has the tables or the app cannot run — so this
/// IS the mechanism, and it runs by default.</para>
/// </summary>
public sealed class DatabaseInitializer(IDbConnectionFactory connectionFactory, DatabaseProvider provider)
{
    private readonly IDbConnectionFactory _connectionFactory = connectionFactory;
    private readonly DatabaseProvider _provider = provider;

    /// <summary>Create anything the configured provider's schema says is missing.</summary>
    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        var resource = _provider == DatabaseProvider.Sqlite ? "schema.sqlite.sql" : "schema.sql";
        var script = ReadEmbeddedSchema(resource);
        await using var connection = await _connectionFactory.OpenAsync(ct);

        foreach (var batch in SplitBatches(script))
        {
            await using var command = connection.Command(batch);
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private static string ReadEmbeddedSchema(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        // EndsWith with the '.' guard, so "schema.sql" cannot also match "schema.sqlite.sql".
        var name = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Split a script into separately-executed batches on lines consisting solely of <c>GO</c>.
    ///
    /// <para><c>GO</c> is a client-side batch separator, not SQL, and only <c>schema.sql</c> uses it — its guarded
    /// <c>ALTER</c>s must not be parsed until the preceding batch has actually added the column. The SQLite script has
    /// no <c>GO</c> at all and comes back as a single batch, which is correct: Microsoft.Data.Sqlite executes a
    /// multi-statement command text statement by statement anyway.</para>
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
