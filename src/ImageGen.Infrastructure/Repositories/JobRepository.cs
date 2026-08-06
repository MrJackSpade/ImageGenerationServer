using ImageGen.Application.Security;
using ImageGen.Domain;
using ImageGen.Domain.CodeAnalysis;
using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;
using ImageGen.Infrastructure.Database;
using System.Data;
using System.Data.Common;
using System.Text.Json;

namespace ImageGen.Infrastructure.Repositories;

/// <summary>
/// ADO.NET storage for render jobs and their slots. Registered as a <b>Singleton</b> (unlike the scoped repositories)
/// because the singleton <c>JobQueue</c> writes through it on every state transition — same singleton-safe exception as
/// <see cref="ImageBlobRepository"/> (it holds no mutable state; it opens a fresh connection per call). See
/// ARCHITECTURE.md §4.
/// <para>Encryption is per FIELD. The user's text — Job.Prompt, and a slot's EffectivePrompt / RawPrompt /
/// RawNegativePrompt / Prompt / NegativePrompt — is randomized-encrypted under the job owner's key. A slot's MARK
/// tokens are deterministically encrypted, so equality and IN (…) still work over them. Everything else — ids,
/// workflow, states, flags, numbers, timings — is plaintext, because none of it is protected and hiding it behind a
/// key would leave four image foreign keys unjoinable.</para>
/// </summary>
[AllowMagicStrings("SQL query text and its bound @parameter-name tokens")]
public sealed class JobRepository(IDbConnectionFactory connectionFactory, IUserCipher cipher, TimeProvider clock, ISqlDialect dialect)
    : IJobRepository
{
    private readonly IDbConnectionFactory _connectionFactory = connectionFactory;
    /// <summary>Supplies the few SQL fragments the two engines spell differently.</summary>
    private readonly ISqlDialect _dialect = dialect;
    private readonly IUserCipher _cipher = cipher;
    /// <summary>Supplies <c>FinishedAtUtc</c>, stamped from the app clock rather than a database-side <c>SYSUTCDATETIME()</c>.</summary>
    private readonly TimeProvider _clock = clock;

    private static class Sql
    {
        /// <summary>Positional: MapSlot reads by ordinal, so append — never insert — a column here.</summary>
        public const string SlotColumns =
            "JobId, SlotIndex, IsEdit, State, ComfyPromptId, ImageId, Width, Height, Changed, ChangeScore, " +
            "Error, EffectivePrompt, GenStartedAtUtc, ExpectedGenSeconds, RawPrompt, RawNegativePrompt, " +
            "Workflow, Prompt, NegativePrompt, Aspect, RandomArtist, RandomPrompt, Temperature, TagTypesJson, " +
            "OverridesJson, SourceImageId, MaskImageId, LastFrameImageId, LorasJson, IsBackground";
    }

    public async Task UpsertAsync(JobRecord job, CancellationToken ct)
    {
        // Provision the key BEFORE the transaction: the cipher writes on its own connection the first time a
        // user encrypts anything, and SQLite allows one writer -- doing it inside would deadlock against us.
        await _cipher.EnsureKeyAsync(job.UserId, ct);
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbTransaction tx = await conn.BeginTransactionAsync(ct);

        // MERGE vs INSERT ... ON CONFLICT DO UPDATE. Both spell the same upsert and both key on JobId's primary key;
        // the statement text lives in the dialect because there is no wording the two engines share.
        string jobSql = _dialect.UpsertJob;

        await using (DbCommand cmd = conn.Command(jobSql, tx))
        {
            cmd.AddParam("@jobId", job.JobId);
            cmd.AddParam("@userId", job.UserId);
            cmd.AddParam("@machine", job.MachineName);
            cmd.AddParam("@model", job.Model);
            cmd.AddParam("@prompt", await _cipher.EncryptAsync(job.UserId, job.Prompt, ct));
            cmd.AddParam("@total", job.Total);
            cmd.AddParam("@status", (byte)job.Status);
            cmd.AddParam("@created", job.CreatedAtUtc);
            cmd.AddParam("@finished", (object?)job.FinishedAtUtc ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Keyed on the (JobId, SlotIndex) unique constraint, which both dialects' upserts rely on existing.
        string slotSql = _dialect.UpsertJobSlot;

        foreach (JobSlotRecord slot in job.Slots)
        {
            await using DbCommand cmd = conn.Command(slotSql, tx);
            cmd.AddParam("@jobId", slot.JobId);
            cmd.AddParam("@idx", slot.SlotIndex);
            cmd.AddParam("@isEdit", slot.IsEdit);
            cmd.AddParam("@isBackground", slot.IsBackground);
            cmd.AddParam("@state", (byte)slot.State);
            cmd.AddParam("@comfy", (object?)slot.ComfyPromptId ?? DBNull.Value);
            cmd.AddParam("@imageId", (object?)slot.ImageId ?? DBNull.Value);
            cmd.AddParam("@width", (object?)slot.Width ?? DBNull.Value);
            cmd.AddParam("@height", (object?)slot.Height ?? DBNull.Value);
            cmd.AddParam("@changed", slot.Edit?.Changed ?? true);
            cmd.AddParam("@score", (object?)slot.Edit?.ChangeScore ?? DBNull.Value);
            cmd.AddParam("@error", (object?)slot.Error ?? DBNull.Value);
            cmd.AddParam("@effective", (object?)await _cipher.EncryptNullableAsync(job.UserId, slot.EffectivePrompt, ct) ?? DBNull.Value);
            cmd.AddParam("@raw", (object?)await _cipher.EncryptNullableAsync(job.UserId, slot.RawPrompt, ct) ?? DBNull.Value);
            cmd.AddParam("@rawNeg", (object?)await _cipher.EncryptNullableAsync(job.UserId, slot.RawNegativePrompt, ct) ?? DBNull.Value);
            cmd.AddParam("@started", (object?)slot.GenStartedAtUtc ?? DBNull.Value);
            cmd.AddParam("@expected", (object?)slot.ExpectedGenSeconds ?? DBNull.Value);
            // The spec, per field: the user's text encrypted, everything else plain and therefore queryable.
            cmd.AddParam("@workflow", (object?)slot.Workflow ?? DBNull.Value);
            cmd.AddParam("@specPrompt", (object?)await _cipher.EncryptNullableAsync(job.UserId, slot.Prompt, ct) ?? DBNull.Value);
            cmd.AddParam("@specNegative", (object?)await _cipher.EncryptNullableAsync(job.UserId, slot.NegativePrompt, ct) ?? DBNull.Value);
            cmd.AddParam("@aspect", (object?)slot.Generate?.Aspect ?? DBNull.Value);
            cmd.AddParam("@randomArtist", (slot.Generate?.RandomArtist ?? TriState.Unspecified).ToNullableBitParam());
            cmd.AddParam("@randomPrompt", (slot.Generate?.RandomPrompt ?? TriState.Unspecified).ToNullableBitParam());
            cmd.AddParam("@temperature", (object?)slot.Generate?.Temperature ?? DBNull.Value);
            cmd.AddParam("@tagTypes", (object?)slot.Generate?.TagTypesJson ?? DBNull.Value);
            cmd.AddParam("@overrides", (object?)slot.OverridesJson ?? DBNull.Value);
            cmd.AddParam("@loras", (object?)slot.LorasJson ?? DBNull.Value);
            cmd.AddParam("@source", (object?)slot.Edit?.SourceImageId ?? DBNull.Value);
            cmd.AddParam("@mask", (object?)slot.Edit?.MaskImageId ?? DBNull.Value);
            cmd.AddParam("@lastFrame", (object?)slot.Edit?.LastFrameImageId ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);

            // The slot's child rows are REPLACED, not merged: this is a write-through of the whole in-memory slot,
            // and a reference list that shrank must not leave the dropped rows behind.
            await ReplaceSlotChildrenAsync(conn, tx, job.UserId, slot, ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<JobRecord?> GetAsync(string jobId, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        JobRecord? job;
        await using (DbCommand cmd = conn.Command(
            "SELECT JobId, UserId, MachineName, Model, Prompt, Total, Status, CreatedAtUtc, FinishedAtUtc " +
            "FROM dbo.Job WHERE JobId = @jobId;"))
        {
            cmd.AddParam("@jobId", jobId);
            await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            job = MapJob(reader);
        }

        await using (DbCommand cmd = conn.Command(
            $"SELECT {Sql.SlotColumns} FROM dbo.JobSlot WHERE JobId = @jobId ORDER BY SlotIndex ASC;"))
        {
            cmd.AddParam("@jobId", jobId);
            await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                job.Slots.Add(MapSlot(reader));
        }
        await LoadSlotChildrenAsync(conn, job, ct);
        await DecryptInPlaceAsync(job, ct);
        return job;
    }

    public async Task<int> CountLatestBatchImagesAsync(long userId, CancellationToken ct)
    {
        // One statement, no slot decryption: the newest job of this user's, then its slots that hold an image.
        // COUNT(ImageId) ignores nulls, so a queued or errored slot contributes nothing, and a user with no jobs
        // counts over an empty set and comes back 0.
        string sql = $@"
SELECT COUNT(s.ImageId)
FROM dbo.JobSlot s
WHERE s.JobId = (SELECT {_dialect.TopPrefix("@take")}JobId FROM dbo.Job WHERE UserId = @userId
                  ORDER BY CreatedAtUtc DESC, JobId DESC{_dialect.TopSuffix("@take")});";

        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command(sql);
        cmd.AddParam("@take", 1);
        cmd.AddParam("@userId", userId);
        return await cmd.ScalarInt32Async(ct);
    }

    public async Task<IReadOnlyList<JobRecord>> ListActiveForMachineAsync(string machineName, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        List<JobRecord> jobs = new List<JobRecord>();
        await using (DbCommand cmd = conn.Command(
            "SELECT JobId, UserId, MachineName, Model, Prompt, Total, Status, CreatedAtUtc, FinishedAtUtc " +
            "FROM dbo.Job WHERE MachineName = @machine AND Status = 0 ORDER BY CreatedAtUtc ASC;"))
        {
            cmd.AddParam("@machine", machineName);
            await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                jobs.Add(MapJob(reader));
        }

        foreach (JobRecord job in jobs)
        {
            await using (DbCommand cmd = conn.Command(
                $"SELECT {Sql.SlotColumns} FROM dbo.JobSlot WHERE JobId = @jobId ORDER BY SlotIndex ASC;"))
            {
                cmd.AddParam("@jobId", job.JobId);
                await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    job.Slots.Add(MapSlot(reader));
            }
        }
        foreach (JobRecord job in jobs)
        {
            await LoadSlotChildrenAsync(conn, job, ct);
            await DecryptInPlaceAsync(job, ct);
        }
        return jobs;
    }

    public async Task<PagedResult<JobRecord>> ListPageAsync(
        string machineName, long viewerUserId, int page, int pageSize, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);

        int total;
        await using (DbCommand cmd = conn.Command("SELECT COUNT(*) FROM dbo.Job WHERE MachineName = @machine;"))
        {
            cmd.AddParam("@machine", machineName);
            total = await cmd.ScalarInt32Async(ct);
        }

        // ORDERING: unfinished work FIRST, in the order the queue will actually serve it (oldest enqueued renders
        // next), then finished jobs newest-first.
        //
        // Newest-created-first for everything would put the page's ordering in direct contradiction with the
        // scheduler's: the fair queue renders the OLDEST queued job, so the one actually on the GPU would sit at the
        // BOTTOM of the backlog — page 3 of a 64-job burst — while page 1, the only page the client polls, would hold
        // 25 jobs that cannot change until the drain is nearly over. The queue page would look frozen for as long as
        // the backlog takes, and any live row a user scrolled to would be on a page that never refreshes.
        List<JobRecord> jobs = new List<JobRecord>();
        await using (DbCommand cmd = conn.Command(
            "SELECT JobId, UserId, MachineName, Model, Prompt, Total, Status, CreatedAtUtc, FinishedAtUtc " +
            "FROM dbo.Job WHERE MachineName = @machine " +
            "ORDER BY CASE WHEN Status = 0 THEN 0 ELSE 1 END, " +          // active first
            "         CASE WHEN Status = 0 THEN CreatedAtUtc END ASC, " +  // ...in service order (oldest renders next)
            "         CreatedAtUtc DESC, JobId DESC " +                    // finished: newest first
            _dialect.Paginate("@offset", "@take") + ";"))
        {
            cmd.AddParam("@machine", machineName);
            cmd.AddParam("@offset", (page - 1) * pageSize);
            cmd.AddParam("@take", pageSize);
            await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                jobs.Add(MapJob(reader));
        }

        // Lightweight slots for the page in one batched read — only the columns a queue row needs (no encrypted
        // prompt columns, so nothing to decrypt here): kind badge (IsEdit) + produced/progress counts.
        if (jobs.Count > 0)
        {
            Dictionary<string, JobRecord> byId = jobs.ToDictionary(j => j.JobId);
            string[] names = jobs.Select((_, i) => "@j" + i).ToArray();
            await using DbCommand cmd = conn.Command(
                $"SELECT JobId, SlotIndex, IsEdit, State, ImageId FROM dbo.JobSlot " +
                $"WHERE JobId IN ({string.Join(",", names)}) ORDER BY SlotIndex ASC;");
            for (int i = 0; i < jobs.Count; i++) cmd.AddParam(names[i], jobs[i].JobId);
            await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                string jobId = reader.GetString(0);
                if (!byId.TryGetValue(jobId, out JobRecord? job)) continue;
                job.Slots.Add(new JobSlotRecord
                {
                    JobId = jobId,
                    SlotIndex = reader.AsInt32(1),
                    IsEdit = reader.AsBool(2),
                    State = (JobSlotState)reader.AsByte(3),
                    ImageId = reader.IsDBNull(4) ? null : reader.GetString(4),
                });
            }
        }

        // Privacy: decrypt only the viewer's OWN prompt (each key is per-user; another owner's prompt must never be
        // shown on this cross-user page). Everyone else's prompt is blanked so it never leaves the server in cleartext.
        foreach (JobRecord job in jobs)
            job.Prompt = job.UserId == viewerUserId ? await _cipher.DecryptAsync(job.UserId, job.Prompt, ct) : "";

        return new PagedResult<JobRecord>(jobs, total, page, pageSize);
    }

    public Task FailAsync(string jobId, string reason, CancellationToken ct) =>
        ResolveUnfinishedAsync(jobId, JobSlotState.Error, JobStatus.Error, reason, ct);

    public Task CancelAsync(string jobId, CancellationToken ct) =>
        ResolveUnfinishedAsync(jobId, JobSlotState.Cancelled, JobStatus.Cancelled, "cancelled", ct);

    /// <summary>Terminate a job's unfinished slots and finalize it, in the given terminal states. One implementation
    /// for failure and cancellation because the mechanics are identical and only the states differ — what must NOT be
    /// shared is the state itself, which is the whole distinction between "it broke" and "you stopped it".</summary>
    private async Task ResolveUnfinishedAsync(
        string jobId, JobSlotState slotState, JobStatus jobStatus, string reason, CancellationToken ct)
    {
        // Both statements are guarded on the non-terminal state, so this is idempotent and cannot stomp a slot that
        // actually produced an image (State=Done) if the job is resolved while a late result is landing.
        //
        // COALESCE, not T-SQL's ISNULL. FinishedAtUtc is stamped from the app clock rather than SYSUTCDATETIME(): the
        // engine-specific "now" function has no portable spelling, and the app already owns every other timestamp
        // written here. On a box where the database lives on a different machine this reads that machine's clock
        // instead of the database's -- both UTC, so it moves the value by whatever the two hosts' clock skew is.
        const string sql = @"
UPDATE dbo.JobSlot
   SET State = @slotState, Error = COALESCE(Error, @reason)
 WHERE JobId = @jobId AND State IN (0, 1);

UPDATE dbo.Job
   SET Status = @jobStatus, FinishedAtUtc = @finishedAt
 WHERE JobId = @jobId AND Status = 0;";

        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbTransaction tx = await conn.BeginTransactionAsync(ct);
        await using (DbCommand cmd = conn.Command(sql, tx))
        {
            cmd.AddParam("@jobId", jobId);
            cmd.AddParam("@reason", reason);
            cmd.AddParam("@slotState", (byte)slotState);
            cmd.AddParam("@jobStatus", (byte)jobStatus);
            cmd.AddParam("@finishedAt", _clock.GetUtcNow().UtcDateTime);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task SweepDeletedImageSlotsAsync(string jobId, CancellationToken ct)
    {
        // Only slots that produced a stored image are considered: ImageId IS NOT NULL means the render succeeded and
        // the bytes were written to dbo.ImageBlob, so their absence now means the user deleted the image.
        //
        // Plain `DELETE FROM <table>`, not T-SQL's `DELETE alias FROM` -- the aliased form is not ANSI and SQLite has no
        // equivalent. Dropping the alias costs the first statement its correlated NOT EXISTS (an unqualified ImageId
        // inside the subquery would bind to dbo.ImageBlob, silently matching everything), so it becomes an uncorrelated
        // NOT IN over the blob table's primary-key index. `b.ImageId IS NOT NULL` is not redundant paranoia about a PK:
        // it is what keeps NOT IN from evaluating to UNKNOWN -- and therefore deleting nothing at all -- if that column
        // ever becomes nullable.
        const string sql = @"
DELETE FROM dbo.JobSlot
WHERE JobId = @jobId
  AND ImageId IS NOT NULL
  AND ImageId NOT IN (SELECT b.ImageId FROM dbo.ImageBlob b WHERE b.ImageId IS NOT NULL);

DELETE FROM dbo.Job
WHERE JobId = @jobId
  AND NOT EXISTS (SELECT 1 FROM dbo.JobSlot s WHERE s.JobId = @jobId);";

        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbTransaction tx = await conn.BeginTransactionAsync(ct);
        await using (DbCommand cmd = conn.Command(sql, tx))
        {
            cmd.AddParam("@jobId", jobId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// The request that produced an image, as JSON for the caller to hand back.
    /// <para>It is ASSEMBLED from the typed columns and the reference child rows rather than read from one opaque
    /// stored blob — the same answer, but from a row a query can also filter, join and count instead of a string only
    /// a key could open.</para>
    /// </summary>
    public async Task<ImageRequestRecord?> GetRequestByImageAsync(string imageId, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);

        long userId;
        string jobId;
        int slotIndex;
        bool isEdit;
        string? workflow, prompt, negative, aspect, tagTypes, overrides, loras, source, mask, lastFrame;
        bool? randomArtist, randomPrompt;
        double? temperature;
        await using (DbCommand cmd = conn.Command(
            "SELECT j.UserId, s.JobId, s.SlotIndex, s.IsEdit, s.Workflow, s.Prompt, s.NegativePrompt, s.Aspect, " +
            "       s.RandomArtist, s.RandomPrompt, s.Temperature, s.TagTypesJson, s.OverridesJson, " +
            "       s.SourceImageId, s.MaskImageId, s.LastFrameImageId, s.LorasJson " +
            "FROM dbo.JobSlot s JOIN dbo.Job j ON j.JobId = s.JobId WHERE s.ImageId = @id;"))
        {
            cmd.AddParam("@id", imageId);
            await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            userId = reader.GetInt64(0);
            jobId = reader.GetString(1);
            slotIndex = reader.AsInt32(2);
            isEdit = reader.AsBool(3);
            workflow = reader.IsDBNull(4) ? null : reader.GetString(4);
            prompt = reader.IsDBNull(5) ? null : reader.GetString(5);
            negative = reader.IsDBNull(6) ? null : reader.GetString(6);
            aspect = reader.IsDBNull(7) ? null : reader.GetString(7);
            randomArtist = reader.AsNullableBool(8);
            randomPrompt = reader.AsNullableBool(9);
            temperature = reader.AsNullableDouble(10);
            tagTypes = reader.IsDBNull(11) ? null : reader.GetString(11);
            overrides = reader.IsDBNull(12) ? null : reader.GetString(12);
            source = reader.IsDBNull(13) ? null : reader.GetString(13);
            mask = reader.IsDBNull(14) ? null : reader.GetString(14);
            lastFrame = reader.IsDBNull(15) ? null : reader.GetString(15);
            loras = reader.IsDBNull(16) ? null : reader.GetString(16);
        }
        if (workflow is null) return null;   // a slot written before the spec had columns

        List<string> references = new List<string>();
        await using (DbCommand cmd = conn.Command(
            "SELECT ImageId FROM dbo.JobSlotReference WHERE JobId = @jobId AND SlotIndex = @idx ORDER BY Ordinal;"))
        {
            cmd.AddParam("@jobId", jobId);
            cmd.AddParam("@idx", slotIndex);
            await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                references.Add(reader.GetString(0));
        }

        // Only the two text fields were ever encrypted; decrypting is keyed by the owning user.
        string json = JsonSerializer.Serialize(new
        {
            kind = isEdit ? "edit" : "generate",
            workflow,
            prompt = await _cipher.DecryptNullableAsync(userId, prompt, ct),
            negativePrompt = await _cipher.DecryptNullableAsync(userId, negative, ct),
            aspect,
            randomArtist,
            randomPrompt,
            temperature,
            tagTypes = Raw(tagTypes),
            overrides = Raw(overrides),
            loras = Raw(loras),
            sourceImageId = source,
            maskImageId = mask,
            lastFrameImageId = lastFrame,
            referenceImageIds = references,
        });
        return new ImageRequestRecord(userId, json);

        // The two value bags are stored as JSON text; re-emit them as JSON rather than as a quoted string.
        static JsonElement? Raw(string? stored) =>
            string.IsNullOrWhiteSpace(stored) ? null : JsonDocument.Parse(stored).RootElement.Clone();
    }

    /// <summary>
    /// Decrypt the prompt-bearing columns of a fully-populated job (and its slots) in place, after the readers on this
    /// connection are closed. Slots carry no UserId of their own, so the job owner's id keys their decryption.
    /// </summary>
    private async Task DecryptInPlaceAsync(JobRecord job, CancellationToken ct)
    {
        job.Prompt = await _cipher.DecryptAsync(job.UserId, job.Prompt, ct);
        foreach (JobSlotRecord slot in job.Slots)
        {
            slot.EffectivePrompt = await _cipher.DecryptNullableAsync(job.UserId, slot.EffectivePrompt, ct);
            slot.RawPrompt = await _cipher.DecryptNullableAsync(job.UserId, slot.RawPrompt, ct);
            slot.RawNegativePrompt = await _cipher.DecryptNullableAsync(job.UserId, slot.RawNegativePrompt, ct);
            slot.Prompt = await _cipher.DecryptNullableAsync(job.UserId, slot.Prompt, ct);
            slot.NegativePrompt = await _cipher.DecryptNullableAsync(job.UserId, slot.NegativePrompt, ct);
        }
    }

    private static JobRecord MapJob(DbDataReader r) => new()
    {
        JobId = r.GetString(0),
        UserId = r.GetInt64(1),
        MachineName = r.GetString(2),
        Model = r.GetString(3),
        Prompt = r.GetString(4),
        Total = r.AsInt32(5),
        Status = (JobStatus)r.AsByte(6),
        CreatedAtUtc = DateTime.SpecifyKind(r.GetDateTime(7), DateTimeKind.Utc),
        FinishedAtUtc = r.IsDBNull(8) ? null : DateTime.SpecifyKind(r.GetDateTime(8), DateTimeKind.Utc),
    };

    /// <summary>Rewrite one slot's reference and mark rows to match the record. Delete-then-insert, because the whole
    /// slot is written through on every transition and both sets are small.</summary>
    private async Task ReplaceSlotChildrenAsync(
        DbConnection conn, DbTransaction tx, long userId, JobSlotRecord slot, CancellationToken ct)
    {
        await using (DbCommand del = conn.Command(
            "DELETE FROM dbo.JobSlotReference WHERE JobId = @jobId AND SlotIndex = @idx;" +
            "DELETE FROM dbo.JobSlotMark WHERE JobId = @jobId AND SlotIndex = @idx;", tx))
        {
            del.AddParam("@jobId", slot.JobId);
            del.AddParam("@idx", slot.SlotIndex);
            await del.ExecuteNonQueryAsync(ct);
        }

        // References belong to an edit slot; a generate carries none.
        List<string> references = slot.Edit?.ReferenceImageIds ?? [];
        for (int i = 0; i < references.Count; i++)
        {
            await using DbCommand cmd = conn.Command(
                "INSERT INTO dbo.JobSlotReference (JobId, SlotIndex, Ordinal, ImageId) VALUES (@jobId, @idx, @ord, @img);",
                tx);
            cmd.AddParam("@jobId", slot.JobId);
            cmd.AddParam("@idx", slot.SlotIndex);
            cmd.AddParam("@ord", i);
            cmd.AddParam("@img", references[i]);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Deterministic, so the token stays comparable — the whole reason marks are a table and not a blob. The
        // NOT EXISTS makes a repeated token a no-op rather than a primary-key violation.
        foreach (Mark mark in slot.Marks)
        {
            await using DbCommand cmd = conn.Command(
                "INSERT INTO dbo.JobSlotMark (JobId, SlotIndex, Token, Kind) " +
                "SELECT @jobId, @idx, @token, @kind WHERE NOT EXISTS (" +
                "  SELECT 1 FROM dbo.JobSlotMark WHERE JobId = @jobId AND SlotIndex = @idx AND Token = @token AND Kind = @kind);",
                tx);
            cmd.AddParam("@jobId", slot.JobId);
            cmd.AddParam("@idx", slot.SlotIndex);
            cmd.AddParam("@token", await _cipher.DeterministicAsync(userId, mark.Token, ct));
            cmd.AddParam("@kind", (byte)mark.Kind);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>Attach every slot's reference and mark rows to a loaded job, one read each.</summary>
    private async Task LoadSlotChildrenAsync(DbConnection conn, JobRecord job, CancellationToken ct)
    {
        if (job.Slots.Count == 0)
            return;
        Dictionary<int, JobSlotRecord> bySlot = job.Slots.ToDictionary(s => s.SlotIndex);

        await using (DbCommand cmd = conn.Command(
            "SELECT SlotIndex, ImageId FROM dbo.JobSlotReference WHERE JobId = @jobId ORDER BY SlotIndex, Ordinal;"))
        {
            cmd.AddParam("@jobId", job.JobId);
            await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                if (bySlot.TryGetValue(reader.AsInt32(0), out JobSlotRecord? slot) && slot.Edit is { } e)
                    e.ReferenceImageIds.Add(reader.GetString(1));
        }

        // Buffered before decrypting: the reader has to be closed before the cipher touches its own connection, which
        // it does on a cold key-cache miss. Same ordering MarkIo uses, for the same reason.
        List<(int Slot, string Token, TokenKind Kind)> raw = new List<(int Slot, string Token, TokenKind Kind)>();
        await using (DbCommand cmd = conn.Command(
            "SELECT SlotIndex, Token, Kind FROM dbo.JobSlotMark WHERE JobId = @jobId;"))
        {
            cmd.AddParam("@jobId", job.JobId);
            await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                raw.Add((reader.AsInt32(0), reader.GetString(1), (TokenKind)reader.AsByte(2)));
        }
        foreach ((int slotIndex, string? token, TokenKind kind) in raw)
            if (bySlot.TryGetValue(slotIndex, out JobSlotRecord? slot))
                slot.Marks.Add(new Mark(await _cipher.DecryptDeterministicAsync(job.UserId, token, ct), kind));
    }

    private static JobSlotRecord MapSlot(DbDataReader r)
    {
        bool isEdit = r.AsBool(2);
        return new()
        {
            JobId = r.GetString(0),
            SlotIndex = r.AsInt32(1),
            IsEdit = isEdit,
            State = (JobSlotState)r.AsByte(3),
            ComfyPromptId = r.IsDBNull(4) ? null : r.GetString(4),
            ImageId = r.IsDBNull(5) ? null : r.GetString(5),
            Width = r.AsNullableInt32(6),
            Height = r.AsNullableInt32(7),
            Error = r.IsDBNull(10) ? null : r.GetString(10),
            EffectivePrompt = r.IsDBNull(11) ? null : r.GetString(11),
            GenStartedAtUtc = r.IsDBNull(12) ? null : DateTime.SpecifyKind(r.GetDateTime(12), DateTimeKind.Utc),
            ExpectedGenSeconds = r.AsNullableDouble(13),
            RawPrompt = r.IsDBNull(14) ? null : r.GetString(14),
            RawNegativePrompt = r.IsDBNull(15) ? null : r.GetString(15),
            Workflow = r.IsDBNull(16) ? null : r.GetString(16),
            Prompt = r.IsDBNull(17) ? null : r.GetString(17),
            NegativePrompt = r.IsDBNull(18) ? null : r.GetString(18),
            OverridesJson = r.IsDBNull(24) ? null : r.GetString(24),
            LorasJson = r.IsDBNull(28) ? null : r.GetString(28),
            IsBackground = r.AsBool(29),
            // Exactly one mode group is populated, by IsEdit — each field read from its own (unchanged) column.
            Generate = isEdit ? null : new GenerateSlotData
            {
                Aspect = r.IsDBNull(19) ? null : r.GetString(19),
                RandomArtist = r.AsTriState(20),
                RandomPrompt = r.AsTriState(21),
                Temperature = r.AsNullableDouble(22),
                TagTypesJson = r.IsDBNull(23) ? null : r.GetString(23),
            },
            Edit = !isEdit ? null : new EditSlotData
            {
                Changed = r.AsBool(8),
                ChangeScore = r.AsNullableDouble(9),
                SourceImageId = r.IsDBNull(25) ? null : r.GetString(25),
                MaskImageId = r.IsDBNull(26) ? null : r.GetString(26),
                LastFrameImageId = r.IsDBNull(27) ? null : r.GetString(27),
            },
        };
    }
}
