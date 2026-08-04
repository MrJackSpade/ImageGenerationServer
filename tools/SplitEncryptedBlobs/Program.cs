using System.Text.Json;
using ImageGen.Application.Security;
using ImageGen.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

// One-time migration: split the encrypted JSON blobs into the typed columns and relation tables that replaced them.
//
// A JSON blob in this database is only acceptable for an audit record — written once, read whole, never joined on.
// These were not that. Each held relational data, and encrypting the whole object because two or three of its fields
// are protected dragged every neutral field behind the same opaque wall. A foreign key inside an encrypted blob is
// not a foreign key: nothing can join it, count it, or garbage-collect against it — which is exactly how 19,329
// upload rows / 7.1 GB became unreachable, their only reference living inside JobSlot.RequestJson.
//
// What this moves:
//   * JobSlot.RequestJson  -> JobSlot.Workflow/Prompt/NegativePrompt/Aspect/RandomArtist/RandomPrompt/Temperature/
//                             TagTypesJson/OverridesJson/SourceImageId/MaskImageId/LastFrameImageId
//                             + dbo.JobSlotReference (the ordered reference image ids)
//   * JobSlot.MarksJson    -> dbo.JobSlotMark              (deterministic Token, mirroring dbo.HistoryMark)
//   * AppUser.FavoriteWorkflowIds -> dbo.UserFavoriteWorkflow
//   * AppUser.HiddenWorkflowIds   -> dbo.UserHiddenWorkflow
//   * AppUser.CustomWorkflowTags  -> dbo.UserWorkflowTag   (deterministic Tag; the label is the user's own words)
//
// The old columns are LEFT IN PLACE and untouched. This schema is additive and never drops data; dropping them is a
// separate, deliberate step once the new shape has been running.
//
// Reads the CURRENT property names only. A blob written before the Workflow property was renamed (it was "Model")
// migrates without a workflow and stays un-requeueable — deliberately. Those are finished slots whose images are
// already in history; teaching this tool every name the field has ever had is how a codebase accumulates
// compatibility it can never put down, to recover a capability nobody is going to use on a job from last year.
//
// PRIVACY: this decrypts by necessity — it has to read the plaintext to re-encrypt it per field. It must never PRINT
// any of it. Counts and ids only, never content, not even truncated, not even in a dry run. (NoPlaintextLogTests
// fails the build if a Console or ILogger call here so much as looks like it emits one.)
//
// Idempotent and resumable: a job slot whose Workflow column is already filled is skipped, its child rows are
// cleared before being rewritten, and each user relation set is only written for a user who has no rows in that
// table yet. Safe to re-run.
//
// Usage:  dotnet run --project tools/SplitEncryptedBlobs -- [--dry-run] [--conn=<connection string>]
//         (falls back to IMAGEGEN_CONNECTION, then to localhost/ImageGen)

var dryRun = args.Contains("--dry-run");
var connectionString = args.FirstOrDefault(a => a.StartsWith("--conn=", StringComparison.Ordinal))?["--conn=".Length..]
    ?? Environment.GetEnvironmentVariable("IMAGEGEN_CONNECTION")
    ?? "Server=localhost;Database=ImageGen;Trusted_Connection=True;TrustServerCertificate=True;Encrypt=False";

var services = new ServiceCollection().AddInfrastructure(connectionString).BuildServiceProvider();
var cipher = services.GetRequiredService<IUserCipher>();
var ct = CancellationToken.None;

await using var conn = new SqlConnection(connectionString);
await conn.OpenAsync(ct);

Console.WriteLine($"Splitting encrypted blobs into typed columns{(dryRun ? "  [DRY RUN - no writes]" : "")}");
Console.WriteLine();

var slots = await MigrateJobSlotsAsync();
var users = await MigrateUserWorkflowRelationsAsync();

Console.WriteLine();
Console.WriteLine($"Done. {slots} job slot(s) and {users} user(s) migrated.");
Console.WriteLine("The old columns are untouched; drop them once this shape has been running.");
return;

async Task<int> MigrateJobSlotsAsync()
{
    // Only slots that still have a blob and no typed spec. That guard is what makes re-running a no-op.
    var rows = new List<(string JobId, int SlotIndex, long UserId, bool IsEdit, string? Request, string? Marks)>();
    await using (var cmd = new SqlCommand(
        "SELECT s.JobId, s.SlotIndex, j.UserId, s.IsEdit, s.RequestJson, s.MarksJson " +
        "FROM dbo.JobSlot s JOIN dbo.Job j ON j.JobId = s.JobId " +
        "WHERE s.Workflow IS NULL AND (s.RequestJson IS NOT NULL OR s.MarksJson IS NOT NULL);", conn))
    {
        cmd.CommandTimeout = 0;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add((reader.GetString(0), reader.GetInt32(1), reader.GetInt64(2), reader.GetBoolean(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
    }

    Console.WriteLine($"  job slots to split: {rows.Count}");
    if (dryRun || rows.Count == 0)
        return rows.Count;

    var migrated = 0;
    foreach (var row in rows)
    {
        JsonElement? request = null;
        if (row.Request is not null)
        {
            var plain = await cipher.DecryptAsync(row.UserId, row.Request, ct);
            // A blob that will not parse is a corrupt row, not something to guess at. Report WHERE, never what.
            try { request = JsonDocument.Parse(plain).RootElement.Clone(); }
            catch (JsonException)
            {
                Console.WriteLine($"    skipped {row.JobId} slot {row.SlotIndex}: stored request is not readable JSON");
                continue;
            }
        }

        string? Text(string name) =>
            request?.TryGetProperty(name, out var v) == true && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        bool? Flag(string name) =>
            request?.TryGetProperty(name, out var v) == true && v.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? v.GetBoolean() : null;
        double? Number(string name) =>
            request?.TryGetProperty(name, out var v) == true && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        string? Bag(string name) =>
            request?.TryGetProperty(name, out var v) == true && v.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                ? v.GetRawText() : null;

        // The blob's property names are the old GenerateSpec/EditSpec ones. An edit called its text "Instruction"
        // and its source "ImageId"; a generate called them "Prompt" and had neither.
        var prompt = row.IsEdit ? Text("Instruction") : Text("Prompt");

        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        // Clear this slot's child rows first. The tool is meant to be re-runnable, and re-inserting a reference the
        // previous pass already wrote would violate (JobId, SlotIndex, Ordinal) and fail the whole slot.
        await using (var del = new SqlCommand(
            "DELETE FROM dbo.JobSlotReference WHERE JobId = @jobId AND SlotIndex = @idx;" +
            "DELETE FROM dbo.JobSlotMark WHERE JobId = @jobId AND SlotIndex = @idx;", conn, tx))
        {
            del.Parameters.AddWithValue("@jobId", row.JobId);
            del.Parameters.AddWithValue("@idx", row.SlotIndex);
            await del.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = new SqlCommand(@"
UPDATE dbo.JobSlot SET
    Workflow = @workflow, Prompt = @prompt, NegativePrompt = @negative, Aspect = @aspect,
    RandomArtist = @randomArtist, RandomPrompt = @randomPrompt, Temperature = @temperature,
    TagTypesJson = @tagTypes, OverridesJson = @overrides,
    SourceImageId = @source, MaskImageId = @mask, LastFrameImageId = @lastFrame
WHERE JobId = @jobId AND SlotIndex = @idx;", conn, tx))
        {
            cmd.Parameters.AddWithValue("@jobId", row.JobId);
            cmd.Parameters.AddWithValue("@idx", row.SlotIndex);
            cmd.Parameters.AddWithValue("@workflow", (object?)Text("Workflow") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@prompt", (object?)await cipher.EncryptNullableAsync(row.UserId, prompt, ct) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@negative", (object?)await cipher.EncryptNullableAsync(row.UserId, Text("NegativePrompt"), ct) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@aspect", (object?)Text("Aspect") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@randomArtist", (object?)Flag("RandomArtist") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@randomPrompt", (object?)Flag("RandomPrompt") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@temperature", (object?)Number("Temperature") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@tagTypes", (object?)Bag("TagTypes") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@overrides", (object?)Bag("Overrides") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@source", (object?)Text("ImageId") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@mask", (object?)Text("MaskImageId") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@lastFrame", (object?)Text("LastFrameImageId") ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // The reference images: an ordered relation, so they become ordered rows.
        if (request?.TryGetProperty("ReferenceImageIds", out var refs) == true && refs.ValueKind == JsonValueKind.Array)
        {
            var ordinal = 0;
            foreach (var r in refs.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.String || r.GetString() is not { Length: > 0 } id) continue;
                await using var cmd = new SqlCommand(
                    "INSERT INTO dbo.JobSlotReference (JobId, SlotIndex, Ordinal, ImageId) VALUES (@jobId, @idx, @ord, @img);",
                    conn, tx);
                cmd.Parameters.AddWithValue("@jobId", row.JobId);
                cmd.Parameters.AddWithValue("@idx", row.SlotIndex);
                cmd.Parameters.AddWithValue("@ord", ordinal++);
                cmd.Parameters.AddWithValue("@img", id);
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }

        // The marks map -> rows, with the token deterministically encrypted so equality keeps working over it.
        if (row.Marks is not null)
        {
            var plainMarks = await cipher.DecryptAsync(row.UserId, row.Marks, ct);
            Dictionary<string, string>? marks = null;
            try { marks = JsonSerializer.Deserialize<Dictionary<string, string>>(plainMarks); }
            catch (JsonException)
            {
                Console.WriteLine($"    {row.JobId} slot {row.SlotIndex}: stored marks are not readable JSON; spec migrated without them");
            }
            foreach (var (token, kind) in marks ?? [])
            {
                if (string.IsNullOrWhiteSpace(token)) continue;
                await using var cmd = new SqlCommand(
                    "INSERT INTO dbo.JobSlotMark (JobId, SlotIndex, Token, Kind) SELECT @jobId, @idx, @token, @kind " +
                    "WHERE NOT EXISTS (SELECT 1 FROM dbo.JobSlotMark WHERE JobId = @jobId AND SlotIndex = @idx AND Token = @token AND Kind = @kind);",
                    conn, tx);
                cmd.Parameters.AddWithValue("@jobId", row.JobId);
                cmd.Parameters.AddWithValue("@idx", row.SlotIndex);
                cmd.Parameters.AddWithValue("@token", await cipher.DeterministicAsync(row.UserId, token, ct));
                cmd.Parameters.AddWithValue("@kind", (byte)(string.Equals(kind, "artist", StringComparison.OrdinalIgnoreCase) ? 1 : 0));
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }

        await tx.CommitAsync(ct);
        migrated++;
    }

    Console.WriteLine($"  job slots split: {migrated}");
    return migrated;
}

async Task<int> MigrateUserWorkflowRelationsAsync()
{
    var rows = new List<(long Id, string? Favorites, string? Hidden, string? Tags)>();
    await using (var cmd = new SqlCommand(
        "SELECT Id, FavoriteWorkflowIds, HiddenWorkflowIds, CustomWorkflowTags FROM dbo.AppUser " +
        "WHERE FavoriteWorkflowIds IS NOT NULL OR HiddenWorkflowIds IS NOT NULL OR CustomWorkflowTags IS NOT NULL;", conn))
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add((reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
    }

    Console.WriteLine($"  users with workflow blobs: {rows.Count}");
    if (dryRun || rows.Count == 0)
        return rows.Count;

    var migrated = 0;
    foreach (var row in rows)
    {
        var wroteSomething = false;
        wroteSomething |= await CopyIdsAsync("dbo.UserFavoriteWorkflow", row.Id, row.Favorites);
        wroteSomething |= await CopyIdsAsync("dbo.UserHiddenWorkflow", row.Id, row.Hidden);

        if (row.Tags is not null && !await HasRowsAsync("dbo.UserWorkflowTag", row.Id))
        {
            var plain = await cipher.DecryptAsync(row.Id, row.Tags, ct);
            Dictionary<string, List<string>>? map = null;
            try { map = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(plain); }
            catch (JsonException) { Console.WriteLine($"    user {row.Id}: workflow tags are not readable JSON; skipped"); }

            foreach (var (workflowId, labels) in map ?? [])
            {
                if (string.IsNullOrWhiteSpace(workflowId)) continue;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var label in labels ?? [])
                {
                    if (string.IsNullOrWhiteSpace(label)) continue;
                    var cipherText = await cipher.DeterministicAsync(row.Id, label, ct);
                    if (!seen.Add(cipherText)) continue;
                    await using var cmd = new SqlCommand(
                        "INSERT INTO dbo.UserWorkflowTag (UserId, WorkflowId, Tag) VALUES (@id, @wf, @tag);", conn);
                    cmd.Parameters.AddWithValue("@id", row.Id);
                    cmd.Parameters.AddWithValue("@wf", workflowId);
                    cmd.Parameters.AddWithValue("@tag", cipherText);
                    await cmd.ExecuteNonQueryAsync(ct);
                    wroteSomething = true;
                }
            }
        }

        if (wroteSomething) migrated++;
    }

    Console.WriteLine($"  users migrated: {migrated}");
    return migrated;
}

// A plain JSON array of workflow ids -> rows. Skipped when the table already has rows for this user, which is what
// makes a re-run a no-op rather than a duplicate-key failure.
async Task<bool> CopyIdsAsync(string table, long userId, string? json)
{
    if (json is null || await HasRowsAsync(table, userId))
        return false;

    List<string>? ids;
    try { ids = JsonSerializer.Deserialize<List<string>>(json); }
    catch (JsonException) { Console.WriteLine($"    user {userId}: {table} source is not readable JSON; skipped"); return false; }

    var wrote = false;
    foreach (var id in (ids ?? []).Where(i => !string.IsNullOrWhiteSpace(i)).Distinct(StringComparer.Ordinal))
    {
        await using var cmd = new SqlCommand($"INSERT INTO {table} (UserId, WorkflowId) VALUES (@id, @wf);", conn);
        cmd.Parameters.AddWithValue("@id", userId);
        cmd.Parameters.AddWithValue("@wf", id);
        await cmd.ExecuteNonQueryAsync(ct);
        wrote = true;
    }
    return wrote;
}

async Task<bool> HasRowsAsync(string table, long userId)
{
    await using var cmd = new SqlCommand($"SELECT COUNT(*) FROM {table} WHERE UserId = @id;", conn);
    cmd.Parameters.AddWithValue("@id", userId);
    return (int)(await cmd.ExecuteScalarAsync(ct))! > 0;
}
