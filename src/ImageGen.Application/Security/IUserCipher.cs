//TODO: CHECK FOR FALLBACKS
namespace ImageGen.Application.Security;

/// <summary>
/// Per-user encryption boundary used by the repositories: encrypt on write, decrypt on read, keyed by the owning
/// user's id. Loads (and lazily provisions) each user's key from <c>dbo.UserEncryptionKey</c> and caches the derived
/// subkeys in memory. <b>Randomized</b> methods are for free-text columns; <b>deterministic</b> methods are for
/// token/name columns that must stay equality-searchable (same plaintext+user → same ciphertext). Decrypt is tolerant
/// of legacy plaintext (unprefixed values pass through), so encryption can be rolled out without an upfront rewrite.
/// </summary>
public interface IUserCipher
{
    /// <summary>
    /// Loads and caches this user's key, provisioning it if this is their first encrypted write.
    ///
    /// <para><b>Call this before opening a transaction you are going to encrypt inside.</b> Provisioning a key is
    /// itself a database write on the cipher's OWN connection. Under SQL Server that is merely a second connection;
    /// under SQLite, which permits exactly one writer, it is a deadlock — the outer transaction holds the write lock,
    /// the cipher's insert waits for it, and the wait can only end in a timeout. Every method afterwards is a pure
    /// in-memory operation against the cached key, so hoisting this one call above the transaction removes the
    /// nested write entirely rather than papering over it.</para>
    ///
    /// <para>Idempotent and cheap to repeat: after the first call per user it is a dictionary hit.</para>
    /// </summary>
    Task EnsureKeyAsync(long userId, CancellationToken ct);

    /// <summary>Randomized-encrypt free text (fresh nonce each call).</summary>
    Task<string> EncryptAsync(long userId, string plaintext, CancellationToken ct);

    /// <summary>Randomized-encrypt nullable free text; null in → null out.</summary>
    Task<string?> EncryptNullableAsync(long userId, string? plaintext, CancellationToken ct);

    /// <summary>Decrypt a stored value (randomized, deterministic, or legacy plaintext — auto-detected).</summary>
    Task<string> DecryptAsync(long userId, string stored, CancellationToken ct);

    /// <summary>Decrypt a nullable stored value; null in → null out.</summary>
    Task<string?> DecryptNullableAsync(long userId, string? stored, CancellationToken ct);

    /// <summary>Deterministic-encrypt a token/name so equality filters and unique indexes keep working.</summary>
    Task<string> DeterministicAsync(long userId, string token, CancellationToken ct);

    /// <summary>Decrypt a deterministic (or legacy plaintext) token/name.</summary>
    Task<string> DecryptDeterministicAsync(long userId, string stored, CancellationToken ct);
}
