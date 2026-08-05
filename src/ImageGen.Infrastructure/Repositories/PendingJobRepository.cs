using ImageGen.Application.Security;
using ImageGen.Domain.CodeAnalysis;
using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;
using ImageGen.Infrastructure.Database;
using System.Data.Common;

namespace ImageGen.Infrastructure.Repositories;

[AllowMagicStrings("SQL query text and its bound @parameter-name tokens")]
public sealed class PendingJobRepository(IDbConnectionFactory connectionFactory, IUserCipher cipher) : IPendingJobRepository
{
    private const string Columns = "Id, UserId, JobId, Prompt, ModelFriendly, ModelId, Aspect, CreatedAtUtc";

    private readonly IDbConnectionFactory _connectionFactory = connectionFactory;
    private readonly IUserCipher _cipher = cipher;

    public async Task AddAsync(PendingJob job, CancellationToken ct)
    {
        const string sql = @"
INSERT INTO dbo.PendingJob (UserId, JobId, Prompt, ModelFriendly, ModelId, Aspect, CreatedAtUtc)
SELECT @userId, @jobId, @prompt, @modelFriendly, @modelId, @aspect, @created
WHERE NOT EXISTS (SELECT 1 FROM dbo.PendingJob WHERE UserId = @userId AND JobId = @jobId);";

        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command(sql);
        cmd.AddParam("@userId", job.UserId);
        cmd.AddParam("@jobId", job.JobId);
        cmd.AddParam("@prompt", await _cipher.EncryptAsync(job.UserId, job.Prompt, ct));
        cmd.AddParam("@modelFriendly", job.ModelFriendly);
        cmd.AddParam("@modelId", job.ModelId);
        cmd.AddParam("@aspect", job.Aspect);
        cmd.AddParam("@created", job.CreatedAtUtc);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<PendingJob>> ListAllAsync(CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command(
            $"SELECT {Columns} FROM dbo.PendingJob ORDER BY CreatedAtUtc ASC, Id ASC;");
        return await ReadAllAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<PendingJob>> ListForUserAsync(long userId, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command(
            $"SELECT {Columns} FROM dbo.PendingJob WHERE UserId = @userId ORDER BY CreatedAtUtc ASC, Id ASC;");
        cmd.AddParam("@userId", userId);
        return await ReadAllAsync(cmd, ct);
    }

    public async Task RemoveAsync(long id, CancellationToken ct)
    {
        await using DbConnection conn = await _connectionFactory.OpenAsync(ct);
        await using DbCommand cmd = conn.Command("DELETE FROM dbo.PendingJob WHERE Id = @id;");
        cmd.AddParam("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<IReadOnlyList<PendingJob>> ReadAllAsync(DbCommand cmd, CancellationToken ct)
    {
        List<PendingJobRow> raw = new List<PendingJobRow>();
        await using (DbDataReader reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                raw.Add(new PendingJobRow(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.GetString(5), reader.GetString(6),
                    DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Utc)));

        List<PendingJob> rows = new List<PendingJob>(raw.Count);
        foreach (PendingJobRow r in raw)
            rows.Add(new PendingJob
            {
                Id = r.Id,
                UserId = r.UserId,
                JobId = r.JobId,
                Prompt = await _cipher.DecryptAsync(r.UserId, r.Prompt, ct),
                ModelFriendly = r.Friendly,
                ModelId = r.ModelId,
                Aspect = r.Aspect,
                CreatedAtUtc = r.Created,
            });
        return rows;
    }

    /// <summary>A raw pending-job row buffered with its still-encrypted prompt before deferred decryption.</summary>
    private readonly record struct PendingJobRow(
        long Id, long UserId, string JobId, string Prompt, string Friendly, string ModelId, string Aspect, DateTime Created);
}
