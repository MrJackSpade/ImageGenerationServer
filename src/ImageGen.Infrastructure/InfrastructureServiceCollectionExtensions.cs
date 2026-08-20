using ImageGen.Application.Platform;
using ImageGen.Application.Security;
using ImageGen.Domain.Repositories;
using ImageGen.Infrastructure.Database;
using ImageGen.Infrastructure.Repositories;
using ImageGen.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;

namespace ImageGen.Infrastructure;

/// <summary>
/// Registers the persistence layer: the SQL connection factory, the per-user column cipher, and the ADO.NET
/// repositories. Lifetimes are load-bearing: request-scoped repositories for per-request work, and singleton
/// repositories for the ones the singleton render orchestrator resolves from the root provider (it writes jobs,
/// image blobs, frames, timings, and the user log through them on every transition). See ARCHITECTURE.md §4.
/// </summary>
public static class InfrastructureServiceCollectionExtensions
{
    private static class ConnKeys
    {
        public const string ServerKey = "Server=";
        public const string InitialCatalogKey = "Initial Catalog=";
        public const string IntegratedSecurityKey = "Integrated Security=";
        public const string TrustedConnectionKey = "Trusted_Connection=";
    }

    /// <summary>
    /// Add the persistence layer for the given connection string, against the given engine.
    ///
    /// <para>The provider decides three registrations — the connection factory, the SQL dialect, and how an
    /// unreachable-database error is recognised. Everything downstream of those is provider-agnostic: the
    /// repositories are written against <see cref="IDbConnectionFactory"/> and <see cref="ISqlDialect"/> and name
    /// neither engine.</para>
    /// </summary>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, string connectionString, DatabaseProvider provider = DatabaseProvider.SqlServer)
    {
        GuardConnectionStringMatchesProvider(connectionString, provider);

        if (provider == DatabaseProvider.Sqlite)
        {
            _ = services.AddSingleton<IDbConnectionFactory>(_ => new SqliteConnectionFactory(connectionString));
            _ = services.AddSingleton<ISqlDialect, SqliteDialect>();
            _ = services.AddSingleton<IDatabaseAvailability, SqliteDatabaseAvailability>();
        }
        else
        {
            _ = services.AddSingleton<IDbConnectionFactory>(_ => new SqlConnectionFactory(connectionString));
            _ = services.AddSingleton<ISqlDialect, SqlServerDialect>();
            // Lets the render path tell "the database is out of reach" from "this operation was wrong" without knowing
            // what kind of database it is. Stateless — a singleton beside the factory.
            _ = services.AddSingleton<IDatabaseAvailability, SqlDatabaseAvailability>();
        }

        // Per-user column cipher: a singleton (the singleton job/blob repositories depend on it) that caches each
        // user's derived subkeys. No master key — keys live in dbo.UserEncryptionKey.
        _ = services.AddSingleton<IUserCipher, UserCipher>();

        _ = services.AddScoped<IUserRepository, UserRepository>();
        _ = services.AddScoped<IHistoryRepository, HistoryRepository>();
        _ = services.AddScoped<IBookmarkRepository, BookmarkRepository>();
        _ = services.AddScoped<IBannedTokenRepository, BannedTokenRepository>();
        _ = services.AddScoped<IArtistDisplayRepository, ArtistDisplayRepository>();
        _ = services.AddScoped<ILoraDisplayRepository, LoraDisplayRepository>();
        _ = services.AddScoped<ITagDisplayRepository, TagDisplayRepository>();
        _ = services.AddScoped<ILoraMetaRepository, LoraMetaRepository>();
        // Stateless byte store (a fresh connection per call), so a singleton — the singleton populator resolves it directly.
        _ = services.AddSingleton<ILoraPreviewRepository, LoraPreviewRepository>();
        _ = services.AddScoped<ILoraUserSettingRepository, LoraUserSettingRepository>();
        _ = services.AddScoped<IImageViewRepository, ImageViewRepository>();
        // Who may read an image id. Stateless (fresh connection per call), and also used by the singleton renderer to
        // fail closed when replaying a persisted edit, so it shares the singleton-safe repository lifetime below.
        _ = services.AddSingleton<IImageVisibilityRepository, ImageVisibilityRepository>();

        // Stateless (fresh connection per call) → singletons, so the singleton render orchestrator can resolve them
        // from the root provider and write through on every state transition.
        _ = services.AddSingleton<IImageBlobRepository, ImageBlobRepository>();
        _ = services.AddSingleton<IImageDeletionRepository, ImageDeletionRepository>();
        _ = services.AddSingleton<IImageFrameRepository, ImageFrameRepository>();
        _ = services.AddSingleton<IGenTimingRepository, GenTimingRepository>();
        // Machine-scoped catalogue overrides (model bindings, per-config settings). Singleton for the same reason
        // as GenTiming: stateless, a fresh connection per call, and resolved by the singleton catalog service.
        _ = services.AddSingleton<ICatalogOverrideRepository, CatalogOverrideRepository>();
        // DB-backed workflow variants (duplicates of shipped configs). Same machine-scoped, stateless-singleton shape.
        _ = services.AddSingleton<IWorkflowVariantRepository, WorkflowVariantRepository>();
        // This machine's own configuration. Same reasoning again — and note it is read before the host is built
        // (the configuration provider that surfaces it uses CreateConnectionFactory below, not this registration).
        _ = services.AddSingleton<IMachineSettingRepository, MachineSettingRepository>();
        _ = services.AddSingleton<IJobRepository, JobRepository>();
        _ = services.AddSingleton<IUserLogRepository, UserLogRepository>();
        // Auth persistence: server-side cookie sessions and the Data Protection key ring, both DB-backed so a
        // session survives a restart and everything auth dies together with a database wipe. Singletons — the
        // cookie handler's session store and the key manager's XML repository are themselves singletons.
        _ = services.AddSingleton<IAuthSessionRepository, AuthSessionRepository>();
        _ = services.AddSingleton<IDataProtectionKeyRepository, DataProtectionKeyRepository>();
        return services;
    }

    /// <summary>
    /// A connection factory built outside DI, for the one caller that needs the database BEFORE the host exists:
    /// the configuration provider that reads this machine's settings out of it. Applies the same provider guard as
    /// <see cref="AddInfrastructure"/>, so a mismatched pair fails here with the same message rather than later.
    /// </summary>
    public static IDbConnectionFactory CreateConnectionFactory(string connectionString, DatabaseProvider provider)
    {
        GuardConnectionStringMatchesProvider(connectionString, provider);
        return provider == DatabaseProvider.Sqlite
            ? new SqliteConnectionFactory(connectionString)
            : new SqlConnectionFactory(connectionString);
    }

    /// <summary>
    /// Refuses to start when the connection string is obviously for the other engine.
    ///
    /// <para>Worth failing loudly over: pointing the SQLite provider at a SQL Server connection string does not
    /// error, it silently creates an empty database file named after whatever it found and the user is greeted by a
    /// registration page with all their history apparently gone. A startup exception naming both halves is the
    /// kindest outcome available.</para>
    /// </summary>
    private static void GuardConnectionStringMatchesProvider(string connectionString, DatabaseProvider provider)
    {
        bool looksLikeSqlServer =
            connectionString.Contains(ConnKeys.ServerKey, StringComparison.OrdinalIgnoreCase) ||
            connectionString.Contains(ConnKeys.InitialCatalogKey, StringComparison.OrdinalIgnoreCase) ||
            connectionString.Contains(ConnKeys.IntegratedSecurityKey, StringComparison.OrdinalIgnoreCase) ||
            connectionString.Contains(ConnKeys.TrustedConnectionKey, StringComparison.OrdinalIgnoreCase);

        if (provider == DatabaseProvider.Sqlite && looksLikeSqlServer)
        {
            throw new InvalidOperationException(
                "Database:Provider is 'Sqlite' but ConnectionStrings:ImageGen looks like a SQL Server connection "
                + "string. SQLite expects a file, e.g. \"Data Source=/data/imagegen.db\". Refusing to start rather "
                + "than silently creating an empty database and appearing to have lost every account.");
        }

        if (provider == DatabaseProvider.SqlServer && !looksLikeSqlServer)
        {
            throw new InvalidOperationException(
                "Database:Provider is 'SqlServer' but ConnectionStrings:ImageGen does not look like a SQL Server "
                + "connection string. Set Database:Provider to 'Sqlite' if you meant to use a local file.");
        }
    }
}
