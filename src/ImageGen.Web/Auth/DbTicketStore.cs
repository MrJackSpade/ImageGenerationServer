using ImageGen.Domain.Repositories;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace ImageGen.Web.Auth;

/// <summary>
/// Server-side session state for the auth cookie, persisted in the database. Set as the cookie handler's
/// <see cref="CookieAuthenticationOptions.SessionStore"/>, it moves the identity OFF the cookie: the cookie carries
/// only an opaque session key, and the dbo.AuthSession row holds the actual ticket.
///
/// <para>That indirection is the fix, not an optimisation. A default cookie is self-contained — it IS the signed
/// assertion "I am user 1" — so it keeps meaning that for as long as its signature verifies, which outlives the
/// account it names. Checking "does a user with this id still exist" does not save you, because ids are
/// <c>BIGINT IDENTITY</c> and a re-created first account takes id 1 again — the ghost cookie then authenticates as
/// whoever now holds its id. A session key cannot be reasoned about that way: it names a row in THIS database, and
/// after a wipe there is no such row, so the request is simply anonymous and the normal login runs. Logout removes
/// the row, so signing out actually ends the session server-side rather than just asking the browser to forget a
/// cookie that still works.</para>
///
/// <para>Being in the database (rather than the in-process cache this replaced), a session now survives an app
/// restart or redeploy, bounded by the cookie's own expiry; the durable revocation point is the database itself —
/// wiping it ends every session, exactly because the sessions live nowhere else.</para>
/// </summary>
public sealed class DbTicketStore(IAuthSessionRepository sessions) : ITicketStore
{
    private readonly IAuthSessionRepository _sessions = sessions;

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        // A fresh random key per sign-in — unique and unguessable, and never reused across sessions. Sign-in is
        // also the sweep point for rows whose expiry has passed, so the table never accretes dead sessions.
        await _sessions.DeleteExpiredAsync(DateTime.UtcNow, CancellationToken.None);
        string key = Guid.NewGuid().ToString("N");
        await RenewAsync(key, ticket);
        return key;
    }

    /// <summary>Honours the ticket's own expiry so a session cannot outlive the cookie that points at it.
    /// SlidingExpiration renews the cookie past the halfway mark and calls back here with a later ExpiresUtc,
    /// which extends the row too.</summary>
    public Task RenewAsync(string key, AuthenticationTicket ticket) =>
        _sessions.UpsertAsync(
            key,
            TicketSerializer.Default.Serialize(ticket),
            ticket.Properties.ExpiresUtc?.UtcDateTime,
            CancellationToken.None);

    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        byte[]? ticket = await _sessions.GetAsync(key, CancellationToken.None);
        return ticket is null ? null : TicketSerializer.Default.Deserialize(ticket);
    }

    public Task RemoveAsync(string key) => _sessions.DeleteAsync(key, CancellationToken.None);
}
