using ImageGen.Infrastructure;
using ImageGen.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace ImageGen.Tests;

/// <summary>
/// The SQLite schema is an append-only, version-segregated script replayed in full on every startup
/// (<c>schema.sqlite.sql</c>). These pin the two things that has to mean, and the bug that made them necessary:
/// a column added in a later version must reach a database created by an earlier one, and replaying the whole
/// script must never error.
///
/// <para>The bug: <c>JobSlot.LorasJson</c> was once inlined into the JobSlot <c>CREATE TABLE</c>. On an existing
/// database that CREATE is skipped (the table is already there), so the column never arrived, and the app started
/// and then died on the first JobSlot query. It is now an <c>ALTER TABLE … ADD COLUMN</c> in the 0.9.1 block, which
/// the initializer applies only when the column is absent.</para>
/// </summary>
public sealed class SqliteSchemaMigrationTests
{
    private static string TempDbPath() => Path.Combine(Path.GetTempPath(), $"imagegen-schema-{Guid.NewGuid():N}.db");

    private static Task InitAsync(SqliteConnectionFactory factory) =>
        new DatabaseInitializer(factory, DatabaseProvider.Sqlite).EnsureSchemaAsync(CancellationToken.None);

    private static async Task<HashSet<string>> ColumnsAsync(SqliteConnectionFactory factory, string table)
    {
        await using var connection = await factory.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA dbo.table_info({table});";
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(1));
        return columns;
    }

    private static async Task ExecAsync(SqliteConnectionFactory factory, string sql)
    {
        await using var connection = await factory.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static void Cleanup(string path)
    {
        // Release the native file handles before deleting, or the WAL/SHM siblings stay locked on Windows.
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            try { if (File.Exists(file)) File.Delete(file); } catch (IOException) { }
    }

    [Fact]
    public async Task Replaying_the_whole_script_is_idempotent()
    {
        var path = TempDbPath();
        var factory = new SqliteConnectionFactory($"Data Source={path}");
        try
        {
            await InitAsync(factory);
            await InitAsync(factory);   // second startup: every IF NOT EXISTS and the ADD COLUMN must no-op, not throw

            Assert.Contains("LorasJson", await ColumnsAsync(factory, "JobSlot"));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task A_column_added_after_0_9_0_reaches_a_preexisting_database()
    {
        var path = TempDbPath();
        var factory = new SqliteConnectionFactory($"Data Source={path}");
        try
        {
            // JobSlot as a 0.9.0 database has it: present, and WITHOUT LorasJson (added in 0.9.1). The UNIQUE mirrors
            // the real table so the composite foreign keys in the 0.9.0 block still create. The 0.9.0 CREATE TABLE
            // IF NOT EXISTS will see this table and skip it — exactly the case that stranded LorasJson before.
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

            await InitAsync(factory);

            Assert.Contains("LorasJson", await ColumnsAsync(factory, "JobSlot"));   // the 0.9.1 ADD COLUMN reached it
            Assert.NotEmpty(await ColumnsAsync(factory, "LoraDisplay"));            // and every later-version table exists
            Assert.NotEmpty(await ColumnsAsync(factory, "TagDisplay"));            // 0.9.2
            Assert.NotEmpty(await ColumnsAsync(factory, "LoraPreview"));           // 0.9.3
            Assert.Contains("RenderWidth", await ColumnsAsync(factory, "GenTiming"));   // 0.11.0 ADD COLUMN reached the pre-existing GenTiming
            Assert.Contains("Frames", await ColumnsAsync(factory, "GenTiming"));
        }
        finally { Cleanup(path); }
    }
}
