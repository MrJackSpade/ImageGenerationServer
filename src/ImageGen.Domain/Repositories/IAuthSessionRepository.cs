namespace ImageGen.Domain.Repositories;

/// <summary>
/// Server-side auth sessions. Each row is one signed-in session: an opaque key (the only thing the browser's
/// cookie carries) mapped to the serialized authentication ticket. Persisted so a session survives an app
/// restart — and dies with the database, which is the ghost-cookie guarantee: wiping the database wipes the
/// sessions that point into it.
/// </summary>
public interface IAuthSessionRepository
{
    /// <summary>Insert or replace the session at <paramref name="key"/>. A null <paramref name="expiresAtUtc"/>
    /// means the ticket carries no expiry and the row never lapses on its own.</summary>
    Task UpsertAsync(string key, byte[] ticket, DateTime? expiresAtUtc, CancellationToken ct);

    /// <summary>The serialized ticket at <paramref name="key"/>, or null when the row is absent or lapsed.</summary>
    Task<byte[]?> GetAsync(string key, CancellationToken ct);

    Task DeleteAsync(string key, CancellationToken ct);

    /// <summary>Drop every row whose expiry has passed. Called opportunistically on sign-in, so the table only
    /// ever holds live sessions plus whatever lapsed since the last one.</summary>
    Task DeleteExpiredAsync(DateTime nowUtc, CancellationToken ct);
}
