using ImageGen.Infrastructure;
using ImageGen.Infrastructure.Database;
using Microsoft.Data.Sqlite;
using System.Data.Common;

namespace ImageGen.Tests;

/// <summary>
/// The SQLite schema is an append-only, version-segregated script replayed in full on every startup
/// (<c>schema.sqlite.sql</c>). These pin the two things that has to mean:
/// a column added in a later version must reach a database created by an earlier one, and replaying the whole
/// script must never error.
///
/// <para>Inlining a column such as <c>JobSlot.LorasJson</c> into the JobSlot <c>CREATE TABLE</c> would strand it: on
/// an existing database that CREATE is skipped (the table is already there), so the column never arrives and the app
/// dies on the first JobSlot query. A later-version column is instead an <c>ALTER TABLE … ADD COLUMN</c> in the 0.9.1
/// block, which the initializer applies only when the column is absent.</para>
/// </summary>
public sealed class SqliteSchemaMigrationTests
{
    private static string TempDbPath() => Path.Combine(Path.GetTempPath(), $"imagegen-schema-{Guid.NewGuid():N}.db");

    private static Task InitAsync(SqliteConnectionFactory factory) =>
        new DatabaseInitializer(factory, DatabaseProvider.Sqlite).EnsureSchemaAsync(CancellationToken.None);

    private static async Task<HashSet<string>> ColumnsAsync(SqliteConnectionFactory factory, string table)
    {
        await using DbConnection connection = await factory.OpenAsync(CancellationToken.None);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA dbo.table_info({table});";
        HashSet<string> columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(1));
        return columns;
    }

    private static async Task ExecAsync(SqliteConnectionFactory factory, string sql)
    {
        await using DbConnection connection = await factory.OpenAsync(CancellationToken.None);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static void Cleanup(string path)
    {
        // Release the native file handles before deleting, or the WAL/SHM siblings stay locked on Windows.
        SqliteConnection.ClearAllPools();
        foreach (string? file in new[] { path, path + "-wal", path + "-shm" })
            // A leaked temp file is worth strictly less than a readable test failure, so a locked file is ignored.
            try { if (File.Exists(file)) File.Delete(file); } catch (IOException) { }
    }

    [Fact]
    public async Task Replaying_the_whole_script_is_idempotent()
    {
        string path = TempDbPath();
        SqliteConnectionFactory factory = new SqliteConnectionFactory($"Data Source={path}");
        try
        {
            await InitAsync(factory);
            await InitAsync(factory);   // second startup: every IF NOT EXISTS and the ADD COLUMN must no-op, not throw

            Assert.Contains("LorasJson", await ColumnsAsync(factory, "JobSlot"));
            Assert.Contains("IsBackground", await ColumnsAsync(factory, "JobSlot"));   // 0.13.0 ADD COLUMN
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task A_column_added_after_0_9_0_reaches_a_preexisting_database()
    {
        string path = TempDbPath();
        SqliteConnectionFactory factory = new SqliteConnectionFactory($"Data Source={path}");
        try
        {
            // JobSlot as a 0.9.0 database has it: present, and WITHOUT LorasJson (added in 0.9.1). The UNIQUE mirrors
            // the real table so the composite foreign keys in the 0.9.0 block still create. The 0.9.0 CREATE TABLE
            // IF NOT EXISTS will see this table and skip it — exactly the case that would strand LorasJson.
            await ExecAsync(factory,
                "CREATE TABLE dbo.JobSlot (Id INTEGER PRIMARY KEY AUTOINCREMENT, JobId TEXT NOT NULL, " +
                "SlotIndex INTEGER NOT NULL, CONSTRAINT UQ_JobSlot_Job_Index UNIQUE (JobId, SlotIndex));");
            Assert.DoesNotContain("LorasJson", await ColumnsAsync(factory, "JobSlot"));

            // GenTiming as a 0.9.0 database has it: present, WITHOUT the 0.11.0 ETA columns. The 0.9.0 CREATE TABLE
            // IF NOT EXISTS skips it, so the 0.11.0 ADD COLUMNs are the only path those columns can arrive by.
            await ExecAsync(factory,
                "CREATE TABLE dbo.GenTiming (Id INTEGER PRIMARY KEY AUTOINCREMENT, MachineName TEXT NOT NULL, " +
                "ConfigId TEXT NOT NULL, IsEdit INTEGER NOT NULL, DurationMs INTEGER NOT NULL, CreatedAtUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);");
            Assert.DoesNotContain("RenderWidth", await ColumnsAsync(factory, "GenTiming"));

            // AppUser as a 0.9.0 database has it: the full 0.9.0 column set (those columns are inline in the 0.9.0
            // CREATE, not later ALTERs, so a real 0.9.0 database already carries them) but WITHOUT the 0.12.0
            // PinBookmarkSuggestions column. The 0.9.0 CREATE TABLE IF NOT EXISTS skips this table, so the 0.12.0 ADD
            // COLUMN is the only path that column can arrive by.
            await ExecAsync(factory,
                "CREATE TABLE dbo.AppUser (Id INTEGER PRIMARY KEY AUTOINCREMENT, Username TEXT NOT NULL COLLATE NOCASE, " +
                "PasswordHash TEXT NOT NULL, DisplayName TEXT NOT NULL, CreatedAtUtc TEXT NOT NULL, " +
                "ComposerPrefs TEXT NULL, EditPrefs TEXT NULL, FavoriteWorkflowIds TEXT NULL, CustomWorkflowTags TEXT NULL, " +
                "HiddenWorkflowIds TEXT NULL, GenerationTagTypes TEXT NULL, BookmarkPrefs TEXT NULL, ApiKey TEXT NULL, " +
                "CONSTRAINT UQ_AppUser_Username UNIQUE (Username));");
            Assert.DoesNotContain("PinBookmarkSuggestions", await ColumnsAsync(factory, "AppUser"));

            await InitAsync(factory);

            Assert.Contains("LorasJson", await ColumnsAsync(factory, "JobSlot"));   // the 0.9.1 ADD COLUMN reached it
            Assert.Contains("IsBackground", await ColumnsAsync(factory, "JobSlot"));   // the 0.13.0 ADD COLUMN reached the pre-existing JobSlot
            Assert.NotEmpty(await ColumnsAsync(factory, "LoraDisplay"));            // and every later-version table exists
            Assert.NotEmpty(await ColumnsAsync(factory, "TagDisplay"));            // 0.9.2
            Assert.NotEmpty(await ColumnsAsync(factory, "LoraPreview"));           // 0.9.3
            Assert.Contains("RenderWidth", await ColumnsAsync(factory, "GenTiming"));   // 0.11.0 ADD COLUMN reached the pre-existing GenTiming
            Assert.Contains("Frames", await ColumnsAsync(factory, "GenTiming"));
            Assert.Contains("PinBookmarkSuggestions", await ColumnsAsync(factory, "AppUser"));   // 0.12.0 ADD COLUMN reached the pre-existing AppUser
        }
        finally { Cleanup(path); }
    }
}
