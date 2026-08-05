using ImageGen.Application.Security;
using ImageGen.Domain.CodeAnalysis;
using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;
using ImageGen.Infrastructure.Database;
using System.Data.Common;

namespace ImageGen.Infrastructure.Repositories;

[AllowMagicStrings("SQL query text and its bound @parameter-name tokens")]
public sealed class UserRepository(IDbConnectionFactory connectionFactory, IUserCipher cipher, ISqlDialect dialect) : IUserRepository
{
    /// <summary>Select list for every user read. The ordinals in <see cref="MapUserAsync"/> are positional against
    /// this list — change one and renumber the other.</summary>
    private const string Columns = "Id, Username, PasswordHash, DisplayName, CreatedAtUtc, ComposerPrefs, EditPrefs, ApiKey, GenerationTagTypes, BookmarkPrefs";
    private readonly IDbConnectionFactory _connectionFactory = connectionFactory;
    /// <summary>Supplies the few SQL fragments the two engines spell differently.</summary>
    private readonly ISqlDialect _dialect = dialect;
    private readonly IUserCipher _cipher = cipher;

    public async Task<User?> GetByIdAsync(long id, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command($"SELECT {Columns} FROM dbo.AppUser WHERE Id = @id;");
        cmd.AddParam("@id", id);
        await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? await MapUserAsync(reader, ct) : null;
    }

    public async Task<User?> GetByUsernameAsync(string username, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command($"SELECT {Columns} FROM dbo.AppUser WHERE Username = @username;");
        cmd.AddParam("@username", username);
        await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? await MapUserAsync(reader, ct) : null;
    }

    public async Task<bool> AnyExistAsync(CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        // EXISTS, not COUNT(*): the question is "is there one", and this runs on every anonymous hit of the
        // sign-in page.
        await using DbCommand cmd = conn.Command(
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.AppUser) THEN 1 ELSE 0 END;");
        return await cmd.ScalarInt32Async(ct) == 1;
    }

    public async Task<User?> GetByApiKeyAsync(string apiKey, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command($"SELECT {Columns} FROM dbo.AppUser WHERE ApiKey = @apiKey;");
        cmd.AddParam("@apiKey", apiKey);
        await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? await MapUserAsync(reader, ct) : null;
    }

    public async Task<User?> CreateAsync(User user, CancellationToken ct)
    {
        // The trailing identity read must return NULL when the guard matched an existing row -- that NULL is how this
        // method reports "username already taken" below. See ISqlDialect.InsertedIdentityOrNull: SQLite needs a
        // changes() guard to behave the way SCOPE_IDENTITY() does for free.
        string sql = $@"
INSERT INTO dbo.AppUser (Username, PasswordHash, DisplayName, CreatedAtUtc)
SELECT @username, @hash, @displayName, @created
WHERE NOT EXISTS (SELECT 1 FROM dbo.AppUser WHERE Username = @username);
{_dialect.InsertedIdentityOrNull}";

        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command(sql);
        cmd.AddParam("@username", user.Username);
        cmd.AddParam("@hash", user.PasswordHash);
        cmd.AddParam("@displayName", user.DisplayName);
        cmd.AddParam("@created", user.CreatedAtUtc);
        long? newId = await cmd.ScalarNullableInt64Async(ct);
        if (newId is null)
            return null;   // username already taken

        return new User
        {
            Id = newId.Value,
            Username = user.Username,
            PasswordHash = user.PasswordHash,
            DisplayName = user.DisplayName,
            CreatedAtUtc = user.CreatedAtUtc,
        };
    }

    public async Task UpdateComposerPrefsAsync(long userId, string? prefsJson, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command("UPDATE dbo.AppUser SET ComposerPrefs = @prefs WHERE Id = @id;");
        cmd.AddParam("@prefs", (object?)await _cipher.EncryptNullableAsync(userId, prefsJson, ct) ?? DBNull.Value);
        cmd.AddParam("@id", userId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateEditPrefsAsync(long userId, string? prefsJson, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command("UPDATE dbo.AppUser SET EditPrefs = @prefs WHERE Id = @id;");
        // Editor state blob -> encrypt at rest with the user cipher, exactly like ComposerPrefs.
        cmd.AddParam("@prefs", (object?)await _cipher.EncryptNullableAsync(userId, prefsJson, ct) ?? DBNull.Value);
        cmd.AddParam("@id", userId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateBookmarkPrefsAsync(long userId, string? prefsJson, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command("UPDATE dbo.AppUser SET BookmarkPrefs = @prefs WHERE Id = @id;");
        // The keys carry the user's own category names -> encrypt at rest, exactly like ComposerPrefs.
        cmd.AddParam("@prefs", (object?)await _cipher.EncryptNullableAsync(userId, prefsJson, ct) ?? DBNull.Value);
        cmd.AddParam("@id", userId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<UserWorkflowPrefs> GetWorkflowPrefsAsync(long userId, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);

        List<string> favorites = await ReadWorkflowIdsAsync(conn, "dbo.UserFavoriteWorkflow", userId, ct);
        List<string> hidden = await ReadWorkflowIdsAsync(conn, "dbo.UserHiddenWorkflow", userId, ct);
        List<string> hiddenApi = await ReadWorkflowIdsAsync(conn, "dbo.UserHiddenApiWorkflow", userId, ct);

        // Buffered before decrypting: the reader has to be closed before the cipher touches its own connection.
        List<(string Workflow, string Tag)> rawTags = new List<(string Workflow, string Tag)>();
        await using (DbCommand cmd = conn.Command(
            "SELECT WorkflowId, Tag FROM dbo.UserWorkflowTag WHERE UserId = @id ORDER BY WorkflowId;"))
        {
            cmd.AddParam("@id", userId);
            await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                rawTags.Add((reader.GetString(0), reader.GetString(1)));
        }

        Dictionary<string, List<string>> tags = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach ((string? workflow, string? tag) in rawTags)
        {
            if (!tags.TryGetValue(workflow, out List<string>? list))
                tags[workflow] = list = [];
            list.Add(await _cipher.DecryptDeterministicAsync(userId, tag, ct));
        }

        return new UserWorkflowPrefs(
            favorites, hidden, hiddenApi,
            tags.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.Ordinal));
    }

    public Task SetFavoriteWorkflowsAsync(long userId, IReadOnlyList<string> workflowIds, CancellationToken ct) =>
        ReplaceWorkflowIdsAsync("dbo.UserFavoriteWorkflow", userId, workflowIds, ct);

    public Task SetHiddenWorkflowsAsync(long userId, IReadOnlyList<string> workflowIds, CancellationToken ct) =>
        ReplaceWorkflowIdsAsync("dbo.UserHiddenWorkflow", userId, workflowIds, ct);

    public Task SetHiddenApiWorkflowsAsync(long userId, IReadOnlyList<string> workflowIds, CancellationToken ct) =>
        ReplaceWorkflowIdsAsync("dbo.UserHiddenApiWorkflow", userId, workflowIds, ct);

    public async Task SetWorkflowTagsAsync(
        long userId, IReadOnlyDictionary<string, IReadOnlyList<string>> tags, CancellationToken ct)
    {
        // Provision the key BEFORE the transaction: the cipher writes on its own connection the first time a
        // user encrypts anything, and SQLite allows one writer -- doing it inside would deadlock against us.
        await _cipher.EnsureKeyAsync(userId, ct);
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbTransaction tx = await conn.BeginTransactionAsync(ct);

        // Replace the whole set in one transaction: this is a "here is my labelling now" write, and a partial apply
        // would leave the user looking at labels they had just removed.
        await using (DbCommand del = conn.Command("DELETE FROM dbo.UserWorkflowTag WHERE UserId = @id;", tx))
        {
            del.AddParam("@id", userId);
            await del.ExecuteNonQueryAsync(ct);
        }

        foreach ((string? workflowId, IReadOnlyList<string>? labels) in tags)
        {
            if (string.IsNullOrWhiteSpace(workflowId))
                continue;
            // Deterministic, because a label is a set member: the primary key is what keeps it unique per workflow,
            // and that only works if equal text encrypts equally.
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string label in labels)
            {
                if (string.IsNullOrWhiteSpace(label)) continue;
                string cipherText = await _cipher.DeterministicAsync(userId, label, ct);
                if (!seen.Add(cipherText)) continue;   // the same label twice is one row, not a key violation
                await using DbCommand cmd = conn.Command(
                    "INSERT INTO dbo.UserWorkflowTag (UserId, WorkflowId, Tag) VALUES (@id, @wf, @tag);", tx);
                cmd.AddParam("@id", userId);
                cmd.AddParam("@wf", workflowId);
                cmd.AddParam("@tag", cipherText);
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }

        await tx.CommitAsync(ct);
    }

    private async Task<List<string>> ReadWorkflowIdsAsync(
        DbConnection conn, string table, long userId, CancellationToken ct)
    {
        List<string> ids = new List<string>();
        await using DbCommand cmd = conn.Command($"SELECT WorkflowId FROM {table} WHERE UserId = @id ORDER BY WorkflowId;");
        cmd.AddParam("@id", userId);
        await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            ids.Add(reader.GetString(0));
        return ids;
    }

    /// <summary>Replace a user's whole set in one transaction. Workflow ids are not sensitive, so they go in plain —
    /// which is the point: a plain id is one a query can join, count and clean up against.</summary>
    private async Task ReplaceWorkflowIdsAsync(
        string table, long userId, IReadOnlyList<string> workflowIds, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbTransaction tx = await conn.BeginTransactionAsync(ct);

        await using (DbCommand del = conn.Command($"DELETE FROM {table} WHERE UserId = @id;", tx))
        {
            del.AddParam("@id", userId);
            await del.ExecuteNonQueryAsync(ct);
        }

        foreach (string? workflowId in workflowIds.Where(w => !string.IsNullOrWhiteSpace(w)).Distinct(StringComparer.Ordinal))
        {
            await using DbCommand cmd = conn.Command(
                $"INSERT INTO {table} (UserId, WorkflowId) VALUES (@id, @wf);", tx);
            cmd.AddParam("@id", userId);
            cmd.AddParam("@wf", workflowId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task UpdateGenerationTagTypesAsync(long userId, string? typesJson, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command("UPDATE dbo.AppUser SET GenerationTagTypes = @types WHERE Id = @id;");
        cmd.AddParam("@types", (object?)typesJson ?? DBNull.Value);   // tag-type names — stored plain
        cmd.AddParam("@id", userId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<User> MapUserAsync(DbDataReader r, CancellationToken ct)
    {
        long userId = r.GetInt64(0);
        string? composerPrefs = r.IsDBNull(5) ? null : r.GetString(5);
        string? editPrefs = r.IsDBNull(6) ? null : r.GetString(6);
        string? bookmarkPrefs = r.IsDBNull(9) ? null : r.GetString(9);
        return new User
        {
            Id = userId,
            Username = r.GetString(1),
            PasswordHash = r.GetString(2),
            DisplayName = r.GetString(3),
            CreatedAtUtc = DateTime.SpecifyKind(r.GetDateTime(4), DateTimeKind.Utc),
            ComposerPrefs = await _cipher.DecryptNullableAsync(userId, composerPrefs, ct),
            EditPrefs = await _cipher.DecryptNullableAsync(userId, editPrefs, ct),
            ApiKey = r.IsDBNull(7) ? null : r.GetString(7),
            GenerationTagTypes = r.IsDBNull(8) ? null : r.GetString(8),
            BookmarkPrefs = await _cipher.DecryptNullableAsync(userId, bookmarkPrefs, ct),
        };
    }
}
