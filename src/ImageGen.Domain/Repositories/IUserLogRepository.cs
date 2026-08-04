using ImageGen.Domain.Entities;

namespace ImageGen.Domain.Repositories;

/// <summary>Storage for the per-user encrypted application log (<c>dbo.UserLog</c>).</summary>
public interface IUserLogRepository
{
    /// <summary>Append one row. <paramref name="encryptedPayload"/> is already-encrypted ciphertext.</summary>
    Task AddAsync(long userId, string category, string encryptedPayload, DateTime createdAtUtc, CancellationToken ct);

    /// <summary>Most-recent entries for a user, newest first, with payloads decrypted.</summary>
    Task<IReadOnlyList<UserLogEntry>> GetRecentAsync(long userId, int limit, CancellationToken ct);
}
