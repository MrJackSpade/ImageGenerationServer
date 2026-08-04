namespace ImageGen.Domain.Entities;

/// <summary>
/// One row of the per-user encrypted application log (<c>dbo.UserLog</c>). At rest <see cref="Payload"/> is randomized
/// ciphertext under the owning user's key; when read back through the repository it is decrypted in place.
/// </summary>
public sealed class UserLogEntry
{
    public long Id { get; init; }
    public long UserId { get; init; }

    /// <summary>Short, non-sensitive event label (e.g. "random_prompt", "submit").</summary>
    public required string Category { get; init; }

    /// <summary>The logged text. Ciphertext at rest; decrypted on read.</summary>
    public required string Payload { get; init; }

    public required DateTime CreatedAtUtc { get; init; }
}
