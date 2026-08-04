//TODO: CHECK FOR FALLBACKS
namespace ImageGen.Infrastructure;

/// <summary>
/// Which database engine backs the app, selected by the <c>Database:Provider</c> configuration key.
///
/// <para>The default is <see cref="SqlServer"/> so an existing deployment that sets nothing keeps the database it
/// already has. A packaged install (Docker, or the published release) sets <c>Database__Provider=Sqlite</c> — new
/// users get SQLite because the packaging chose it, not because a default changed under a running app.</para>
/// </summary>
public enum DatabaseProvider
{
    /// <summary>Microsoft SQL Server. Supports several app instances against one shared database.</summary>
    SqlServer = 0,

    /// <summary>
    /// A local SQLite file. Zero setup — no server, no login, no elevated schema step — at the cost of one writer:
    /// SQLite is single-writer, so exactly ONE app instance may point at a given file.
    /// </summary>
    Sqlite = 1,
}
