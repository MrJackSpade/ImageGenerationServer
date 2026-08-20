using ImageGen.Application.Security;
using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;
using ImageGen.Infrastructure;
using ImageGen.Infrastructure.Database;
using ImageGen.Infrastructure.Repositories;
using ImageGen.Infrastructure.Security;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

namespace ImageGen.Tests;

/// <summary>
/// A fresh, empty database for the repository tests, on whichever engine is selected.
///
/// <para><b>SQLite by default.</b> The fixture creates a temp-file SQLite database, so the whole suite runs on a bare
/// checkout. Requiring a SQL Server LocalDB instance would make <c>dotnet test</c> impossible on any machine that had
/// not installed one — a hard prerequisite for anyone cloning the repo, and enough on its own to make the suite
/// non-portable.</para>
///
/// <para><b>SQL Server on demand.</b> Set <c>IMAGEGEN_TEST_SQLSERVER=1</c> and the identical suite runs against
/// LocalDB. That is not a nicety: SQLite and SQL Server are the two things the repositories claim to work on, and the
/// only proof of the claim is running the same assertions against both. Every test in the suite is engine-agnostic on
/// purpose — none of them name a provider.</para>
/// </summary>
public sealed class TestDatabaseFixture : IAsyncLifetime
{
    /// <summary>
    /// Where the SQL Server run points. LocalDB by default, which is what a Windows developer has; CI has no LocalDB
    /// and runs a SQL Server container instead, so both strings are overridable by environment variable. Without
    /// this the SQL Server half of the suite could only ever run on one developer's machine, which is the same as
    /// not running it.
    /// </summary>
    private static string MasterConnectionString =>
        Environment.GetEnvironmentVariable("IMAGEGEN_TEST_SQLSERVER_MASTER")
        ?? @"Server=(localdb)\MSSQLLocalDB;Database=master;Integrated Security=true;TrustServerCertificate=true";

    private static string SqlServerTestConnectionString =>
        Environment.GetEnvironmentVariable("IMAGEGEN_TEST_SQLSERVER_DB")
        ?? @"Server=(localdb)\MSSQLLocalDB;Database=ImageGenTest;Integrated Security=true;TrustServerCertificate=true";

    private readonly string _sqliteDbPath =
        Path.Combine(Path.GetTempPath(), $"imagegen-tests-{Guid.NewGuid():N}.db");

    /// <summary>Which engine this run is exercising. Set <c>IMAGEGEN_TEST_SQLSERVER=1</c> for SQL Server.</summary>
    public static DatabaseProvider Provider =>
        Environment.GetEnvironmentVariable("IMAGEGEN_TEST_SQLSERVER") is "1" or "true"
            ? DatabaseProvider.SqlServer
            : DatabaseProvider.Sqlite;

    /// <summary>The dialect matching <see cref="Provider"/>, handed to every repository that needs one.</summary>
    public ISqlDialect Dialect { get; }

    public IDbConnectionFactory ConnectionFactory { get; }

    /// <summary>Real per-user cipher over the test DB, so repository tests exercise the actual encrypt/decrypt path.</summary>
    public IUserCipher Cipher { get; }

    public TestDatabaseFixture()
    {
        if (Provider == DatabaseProvider.SqlServer)
        {
            ConnectionFactory = new SqlConnectionFactory(SqlServerTestConnectionString);
            Dialect = new SqlServerDialect();
        }
        else
        {
            ConnectionFactory = new SqliteConnectionFactory($"Data Source={_sqliteDbPath}");
            Dialect = new SqliteDialect();
        }

        Cipher = new UserCipher(ConnectionFactory);
    }

    public IUserRepository Users => new UserRepository(ConnectionFactory, Cipher, Dialect);
    public IHistoryRepository History => new HistoryRepository(ConnectionFactory, Cipher, Dialect);
    public IBookmarkRepository Bookmarks => new BookmarkRepository(ConnectionFactory, Cipher, Dialect);
    public IPendingJobRepository Pending => new PendingJobRepository(ConnectionFactory, Cipher);
    public IArtistDisplayRepository ArtistDisplays => new ArtistDisplayRepository(ConnectionFactory, Cipher);
    public ITagDisplayRepository TagDisplays => new TagDisplayRepository(ConnectionFactory, Cipher);
    public ILoraDisplayRepository LoraDisplays => new LoraDisplayRepository(ConnectionFactory, Cipher);
    public IBannedTokenRepository Bans => new BannedTokenRepository(ConnectionFactory, Cipher);
    public IImageBlobRepository Blobs => new ImageBlobRepository(ConnectionFactory);
    public IImageFrameRepository Frames => new ImageFrameRepository(ConnectionFactory);
    public IJobRepository Jobs => new JobRepository(ConnectionFactory, Cipher, TimeProvider.System, Dialect);
    public IImageDeletionRepository ImageDeletions => new ImageDeletionRepository(ConnectionFactory);
    public IImageViewRepository ImageViews => new ImageViewRepository(ConnectionFactory);
    public IImageVisibilityRepository ImageVisibility => new ImageVisibilityRepository(ConnectionFactory);
    public ICatalogOverrideRepository CatalogOverrides => new CatalogOverrideRepository(ConnectionFactory);
    public IWorkflowVariantRepository WorkflowVariants => new WorkflowVariantRepository(ConnectionFactory);
    public IMachineSettingRepository MachineSettings => new MachineSettingRepository(ConnectionFactory);

    public async Task InitializeAsync()
    {
        if (Provider == DatabaseProvider.SqlServer)
        {
            await using SqlConnection master = new(MasterConnectionString);
            await master.OpenAsync();
            await DropSqlServerTestDbAsync(master);
            await Exec(master, "CREATE DATABASE ImageGenTest");
        }
        // SQLite needs no create step: the path is unique per fixture, and opening it makes the file.

        await new DatabaseInitializer(ConnectionFactory, Provider).EnsureSchemaAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        if (Provider == DatabaseProvider.SqlServer)
        {
            await using SqlConnection master = new(MasterConnectionString);
            await master.OpenAsync();
            await DropSqlServerTestDbAsync(master);
            return;
        }

        // Release the native file handles before deleting, or the WAL/SHM siblings stay locked on Windows.
        SqliteConnection.ClearAllPools();
        foreach (string? file in new[] { _sqliteDbPath, _sqliteDbPath + "-wal", _sqliteDbPath + "-shm" })
        {
            // A leaked temp file is worth strictly less than a readable test failure, so a locked file is ignored.
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch (IOException) { }
        }
    }

    /// <summary>Create a distinct user so each test gets an isolated, per-user data island.</summary>
    public async Task<User> NewUserAsync(string tag) =>
        await Users.CreateAsync(
            new User { Username = $"user-{tag}", PasswordHash = "x", DisplayName = tag, CreatedAtUtc = DateTime.UtcNow },
            CancellationToken.None)
        ?? throw new InvalidOperationException($"could not create user-{tag}");

    private static Task DropSqlServerTestDbAsync(SqlConnection master) => Exec(master,
        "IF DB_ID('ImageGenTest') IS NOT NULL BEGIN " +
        "ALTER DATABASE ImageGenTest SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ImageGenTest; END");

    private static async Task Exec(SqlConnection connection, string sql)
    {
        await using SqlCommand command = new(sql, connection);
        _ = await command.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition("db")]
public sealed class DatabaseCollection : ICollectionFixture<TestDatabaseFixture>;
