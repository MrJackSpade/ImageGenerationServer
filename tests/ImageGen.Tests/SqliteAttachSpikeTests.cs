using Microsoft.Data.Sqlite;

namespace ImageGen.Tests;

/// <summary>
/// SPIKE — decides whether the SQLite provider can keep every existing <c>dbo.</c>-qualified SQL string verbatim by
/// attaching the database file under the alias <c>dbo</c>, or whether all 271 references have to be rewritten.
/// Also pins the three SQLite behaviours the port depends on and cannot verify by compiling.
/// </summary>
public sealed class SqliteAttachSpikeTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"imagegen-spike-{Guid.NewGuid():N}.db");

    /// <summary>The alias makes <c>dbo.Thing</c> resolve, so the repositories' SQL needs no schema-prefix surgery.</summary>
    [Fact]
    public async Task Attaching_the_file_as_dbo_makes_schema_qualified_sql_resolve()
    {
        await using var conn = await OpenAsync();

        await ExecAsync(conn, "CREATE TABLE dbo.Thing (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL);");
        await ExecAsync(conn, "INSERT INTO dbo.Thing (Name) VALUES ('alpha');");

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Name FROM dbo.Thing WHERE Id = 1;";
        Assert.Equal("alpha", await cmd.ExecuteScalarAsync());
    }

    /// <summary>
    /// The identity contract the three guarded inserts depend on: a NULL id must mean "the row already existed".
    /// <c>last_insert_rowid()</c> alone returns the PREVIOUS insert's id when nothing was inserted, which would
    /// silently break duplicate detection for registration, history and bookmarks — hence the <c>changes()</c> guard.
    /// </summary>
    [Fact]
    public async Task Changes_guard_makes_last_insert_rowid_report_null_when_nothing_was_inserted()
    {
        await using var conn = await OpenAsync();
        await ExecAsync(conn, "CREATE TABLE dbo.U (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL);");

        const string guardedInsert = @"
INSERT INTO dbo.U (Name)
SELECT @name WHERE NOT EXISTS (SELECT 1 FROM dbo.U WHERE Name = @name);
SELECT CASE WHEN changes() = 0 THEN NULL ELSE last_insert_rowid() END;";

        // First insert wins and reports its id.
        var first = await ScalarAsync(conn, guardedInsert, ("@name", "bob"));
        Assert.Equal(1L, Convert.ToInt64(first));

        // Second is a duplicate: NULL, not the id of row 1 -- and not the previous rowid either.
        var second = await ScalarAsync(conn, guardedInsert, ("@name", "bob"));
        Assert.True(second is null or DBNull, $"expected NULL for a duplicate, got '{second}'");

        // A different name inserts again, proving the guard did not wedge the statement.
        var third = await ScalarAsync(conn, guardedInsert, ("@name", "carol"));
        Assert.Equal(2L, Convert.ToInt64(third));
    }

    /// <summary>
    /// Naked <c>last_insert_rowid()</c> does exactly the damage the guard prevents. Kept as a test so the reason for
    /// the guard is executable rather than a comment someone later "simplifies" away.
    /// </summary>
    [Fact]
    public async Task Unguarded_last_insert_rowid_wrongly_reports_the_previous_id_for_a_duplicate()
    {
        await using var conn = await OpenAsync();
        await ExecAsync(conn, "CREATE TABLE dbo.U (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL);");

        const string unguarded = @"
INSERT INTO dbo.U (Name)
SELECT @name WHERE NOT EXISTS (SELECT 1 FROM dbo.U WHERE Name = @name);
SELECT last_insert_rowid();";

        Assert.Equal(1L, Convert.ToInt64(await ScalarAsync(conn, unguarded, ("@name", "bob"))));
        // The duplicate reports 1 -- a real id, for a row it did not insert.
        Assert.Equal(1L, Convert.ToInt64(await ScalarAsync(conn, unguarded, ("@name", "bob"))));
    }

    /// <summary>
    /// <c>COLLATE NOCASE</c> restores the case-insensitive username uniqueness the app gets free from SQL Server's
    /// default collation. Without it, Bob and bob both register and one of them can never log in predictably.
    /// </summary>
    [Fact]
    public async Task Collate_nocase_preserves_case_insensitive_username_uniqueness()
    {
        await using var conn = await OpenAsync();
        // NOTE the DDL asymmetry this spike exists to find: the INDEX NAME carries the schema, the table it indexes
        // must NOT ("CREATE INDEX ... ON dbo.AppUser" is a syntax error). DML is unaffected.
        await ExecAsync(conn,
            "CREATE TABLE dbo.AppUser (Id INTEGER PRIMARY KEY AUTOINCREMENT, Username TEXT NOT NULL COLLATE NOCASE);" +
            "CREATE UNIQUE INDEX dbo.UQ_AppUser_Username ON AppUser (Username);");

        await ExecAsync(conn, "INSERT INTO dbo.AppUser (Username) VALUES ('Bob');");

        var dup = await Assert.ThrowsAsync<SqliteException>(
            () => ExecAsync(conn, "INSERT INTO dbo.AppUser (Username) VALUES ('bob');"));
        Assert.Contains("UNIQUE", dup.Message, StringComparison.OrdinalIgnoreCase);

        // And the login lookup still finds the row whatever case is typed.
        Assert.Equal(1L, Convert.ToInt64(await ScalarAsync(
            conn, "SELECT Id FROM dbo.AppUser WHERE Username = @u;", ("@u", "BOB"))));
    }

    /// <summary>ON DELETE CASCADE only fires with <c>PRAGMA foreign_keys = ON</c>, which is off by default and which
    /// 13 foreign keys in this schema rely on. Proves the pragma survives on the connection the factory hands out.</summary>
    [Fact]
    public async Task Foreign_key_cascade_fires_on_a_connection_from_the_factory()
    {
        await using var conn = await OpenAsync();
        // A foreign key can never cross databases in SQLite, so the referenced table is named WITHOUT the schema
        // even though the table being created carries it. "REFERENCES dbo.Parent(Id)" is a syntax error.
        await ExecAsync(conn,
            "CREATE TABLE dbo.Parent (Id INTEGER PRIMARY KEY AUTOINCREMENT);" +
            "CREATE TABLE dbo.Child (Id INTEGER PRIMARY KEY AUTOINCREMENT, ParentId INTEGER NOT NULL " +
            "  REFERENCES Parent(Id) ON DELETE CASCADE);");
        await ExecAsync(conn, "INSERT INTO dbo.Parent (Id) VALUES (1); INSERT INTO dbo.Child (ParentId) VALUES (1);");

        await ExecAsync(conn, "DELETE FROM dbo.Parent WHERE Id = 1;");

        Assert.Equal(0L, Convert.ToInt64(await ScalarAsync(conn, "SELECT COUNT(*) FROM dbo.Child;")));
    }

    /// <summary>A transaction spanning statements against the attached database commits atomically. The
    /// super-journal restriction on multi-file transactions in WAL mode does not apply: every table is in one file.</summary>
    [Fact]
    public async Task A_transaction_over_the_attached_database_is_atomic_under_wal()
    {
        await using var conn = await OpenAsync();
        await ExecAsync(conn, "CREATE TABLE dbo.T (Id INTEGER PRIMARY KEY AUTOINCREMENT, V INTEGER NOT NULL);");

        await using (var tx = await conn.BeginTransactionAsync(default))
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = "INSERT INTO dbo.T (V) VALUES (1); INSERT INTO dbo.T (V) VALUES (2);";
            await cmd.ExecuteNonQueryAsync();
            await tx.RollbackAsync();
        }
        Assert.Equal(0L, Convert.ToInt64(await ScalarAsync(conn, "SELECT COUNT(*) FROM dbo.T;")));

        await using (var tx = await conn.BeginTransactionAsync(default))
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = "INSERT INTO dbo.T (V) VALUES (3); INSERT INTO dbo.T (V) VALUES (4);";
            await cmd.ExecuteNonQueryAsync();
            await tx.CommitAsync();
        }
        Assert.Equal(2L, Convert.ToInt64(await ScalarAsync(conn, "SELECT COUNT(*) FROM dbo.T;")));
    }

    /// <summary>
    /// What SQLite's single integer type actually costs, measured rather than assumed.
    ///
    /// <para><c>GetValue</c> hands back <see cref="long"/> for every integral column — but
    /// <c>Microsoft.Data.Sqlite</c>'s TYPED getters (<c>GetByte</c>/<c>GetBoolean</c>/<c>GetInt32</c>) convert
    /// internally, so they do NOT throw. The received wisdom that they break on SQLite is wrong for this provider.</para>
    ///
    /// <para>What genuinely breaks is unboxing a scalar: <c>(int)(await cmd.ExecuteScalarAsync())</c> on a
    /// <c>COUNT(*)</c> is an unbox of a boxed <c>long</c> to <c>int</c>, which the CLR refuses regardless of provider.
    /// That is the real hazard, and it is the one this codebase had ~8 of.</para>
    /// </summary>
    [Fact]
    public async Task Integral_columns_are_long_but_only_the_scalar_unbox_actually_breaks()
    {
        await using var conn = await OpenAsync();
        await ExecAsync(conn, "CREATE TABLE dbo.N (Tiny INTEGER, Flag INTEGER, Num INTEGER, Amount REAL);");
        await ExecAsync(conn, "INSERT INTO dbo.N VALUES (3, 1, 42, 1.5);");

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Tiny, Flag, Num, Amount FROM dbo.N;";
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());

            // The underlying value really is long for all three integral columns.
            Assert.IsType<long>(reader.GetValue(0));
            Assert.IsType<long>(reader.GetValue(1));
            Assert.IsType<long>(reader.GetValue(2));
            Assert.IsType<double>(reader.GetValue(3));

            // ...yet the typed getters cope. Suppressed because calling the banned getters IS the measurement.
#pragma warning disable IMGDB001
            Assert.Equal(3, reader.GetByte(0));
            Assert.True(reader.GetBoolean(1));
            Assert.Equal(42, reader.GetInt32(2));
            Assert.Equal(1.5, reader.GetDouble(3));
#pragma warning restore IMGDB001

            // And the converting reads agree with them, which is what makes the shim a safe substitution.
            Assert.Equal(3, Convert.ToByte(reader.GetValue(0)));
            Assert.Equal(42, Convert.ToInt32(reader.GetValue(2)));
        }

        // The real break: COUNT(*) boxes a long, and (int) on a boxed long is an invalid unbox.
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM dbo.N;";
            var boxed = await cmd.ExecuteScalarAsync();
            Assert.IsType<long>(boxed);
            Assert.Throws<InvalidCastException>(() => (int)boxed!);
            Assert.Equal(1, Convert.ToInt32(boxed));   // what ScalarInt32Async does instead
        }
    }

    /// <summary>Opens a connection the way the real factory will: file attached as <c>dbo</c>, WAL, FK enforcement on.</summary>
    private async Task<SqliteConnection> OpenAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await ExecAsync(conn, $"ATTACH DATABASE '{_dbPath.Replace("'", "''")}' AS dbo;");
        await ExecAsync(conn, "PRAGMA dbo.journal_mode = WAL;");
        await ExecAsync(conn, "PRAGMA foreign_keys = ON;");
        return conn;
    }

    private static async Task ExecAsync(SqliteConnection conn, string sql, params (string, object)[] ps)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqliteConnection conn, string sql, params (string, object)[] ps)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        return await cmd.ExecuteScalarAsync();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            // A leaked temp file is worth strictly less than a readable test failure, so a locked file is ignored.
            if (File.Exists(f)) try { File.Delete(f); } catch (IOException) { }
    }
}
