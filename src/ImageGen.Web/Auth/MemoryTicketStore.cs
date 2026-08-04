//TODO: CHECK FOR FALLBACKS
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Memory;

namespace ImageGen.Web.Auth;

/// <summary>
/// Server-side session state for the auth cookie. Set as the cookie handler's
/// <see cref="CookieAuthenticationOptions.SessionStore"/>, it moves the identity OFF the cookie and into the
/// process: the cookie carries only an opaque session key, and this holds the actual ticket.
///
/// <para>That indirection is the fix, not an optimisation. A default cookie is self-contained — it IS the signed
/// assertion "I am user 1" — so it keeps meaning that for as long as its signature verifies, which outlives the
/// account it names. The Data Protection keys that sign it live in the OS user profile, not the database, so wiping
/// the database (or reinstalling) leaves a still-valid cookie for a user that no longer exists; checking "does a
/// user with this id still exist" does not save you, because ids are <c>BIGINT IDENTITY</c> and a re-created first
/// account takes id 1 again — the ghost cookie then authenticates as whoever now holds its id. A session key cannot
/// be reasoned about that way: it names a row in THIS store, and after a restart (which a wipe or a redeploy is)
/// there is no such row, so the request is simply anonymous and the normal login runs. Logout removes the entry, so
/// signing out actually ends the session server-side rather than just asking the browser to forget a cookie that
/// still works.</para>
///
/// <para>The store is in-memory and owns its own cache, kept apart from the shared image/thumbnail cache so neither
/// one's eviction can log anyone out or evict a thumbnail. Sessions therefore live for the process lifetime bounded
/// by the cookie's own expiry; a restart clears them, which is the intended and only durable revocation point for a
/// single-box app with no session-fanout to coordinate.</para>
/// </summary>
public sealed class MemoryTicketStore : ITicketStore, IDisposable
{
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());

    public Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        // A fresh random key per sign-in — unique and unguessable, and never reused across sessions.
        var key = Guid.NewGuid().ToString("N");
        RenewAsync(key, ticket);   // completes synchronously — nothing to await
        return Task.FromResult(key);
    }

    public Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        var options = new MemoryCacheEntryOptions();
        // Honour the ticket's own expiry so a session cannot outlive the cookie that points at it. SlidingExpiration
        // renews the cookie past the halfway mark and calls back here with a later ExpiresUtc, which extends this too.
        if (ticket.Properties.ExpiresUtc is { } expires)
            options.AbsoluteExpiration = expires;
        _cache.Set(key, ticket, options);
        return Task.CompletedTask;
    }

    public Task<AuthenticationTicket?> RetrieveAsync(string key) =>
        Task.FromResult(_cache.Get<AuthenticationTicket>(key));

    public Task RemoveAsync(string key)
    {
        _cache.Remove(key);
        return Task.CompletedTask;
    }

    public void Dispose() => (_cache as IDisposable)?.Dispose();
}
