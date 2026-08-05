using System.Security.Cryptography;
using System.Text;

namespace ImageGen.Application.Security;

/// <summary>
/// Per-user, column-level encryption primitives (pure BCL — no DB, no DI; unit-testable). A user's random 32-byte
/// master key is stretched (HKDF-SHA256) into three independent subkeys: one for <b>randomized</b> AES-GCM (free-text
/// columns never searched by value) and a key+MAC pair for <b>deterministic</b> AES-GCM (token/name columns that must
/// stay equality-searchable and keep UNIQUE constraints).
///
/// <para>Randomized mode uses a fresh random nonce, so the same plaintext encrypts differently every time. Deterministic
/// mode derives the nonce synthetically as <c>HMAC-SHA256(detMacKey, plaintext)[..12]</c> (SIV-style), so the same
/// plaintext under the same key always yields the same ciphertext — which is what lets equality filters and unique
/// indexes keep working. Deterministic mode therefore leaks equality (which rows share a still-secret token); that is
/// the accepted, intentional trade-off for searchability. Both modes frame the output as
/// <c>prefix + Base64(nonce(12) || tag(16) || ciphertext)</c>.</para>
/// </summary>
public static class UserCrypto
{
    public const string RandomizedPrefix = "enc:v1:";
    public const string DeterministicPrefix = "det:v1:";

    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int SubkeyBytes = 32;

    private const string RandInfoLabel = "imagegen:enc:rand:v1";
    private const string DetInfoLabel = "imagegen:enc:det:v1";
    private const string DetMacInfoLabel = "imagegen:enc:detmac:v1";

    private static readonly byte[] RandInfo = Encoding.UTF8.GetBytes(RandInfoLabel);
    private static readonly byte[] DetInfo = Encoding.UTF8.GetBytes(DetInfoLabel);
    private static readonly byte[] DetMacInfo = Encoding.UTF8.GetBytes(DetMacInfoLabel);

    /// <summary>The three subkeys derived from a user's master key. Treat as opaque and immutable.</summary>
    public sealed class UserKeys
    {
        public required byte[] Randomized { get; init; }
        public required byte[] Deterministic { get; init; }
        public required byte[] DeterministicMac { get; init; }
    }

    /// <summary>Stretch a user's random master key (any length, typically 32 bytes) into the three column subkeys.</summary>
    public static UserKeys DeriveSubkeys(byte[] masterKey) => new()
    {
        Randomized = HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, SubkeyBytes, salt: null, RandInfo),
        Deterministic = HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, SubkeyBytes, salt: null, DetInfo),
        DeterministicMac = HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, SubkeyBytes, salt: null, DetMacInfo),
    };

    /// <summary>Randomized AES-GCM (fresh nonce). For free-text columns never searched by value.</summary>
    public static string EncryptRandomized(UserKeys keys, string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        return RandomizedPrefix + Seal(keys.Randomized, nonce, plaintext);
    }

    /// <summary>Deterministic AES-GCM (synthetic nonce). For token/name columns that stay equality-searchable.</summary>
    public static string EncryptDeterministic(UserKeys keys, string plaintext)
    {
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var mac = HMACSHA256.HashData(keys.DeterministicMac, plain);
        var nonce = mac[..NonceBytes];
        return DeterministicPrefix + Seal(keys.Deterministic, nonce, plain);
    }

    /// <summary>
    /// Decrypt a stored value, tolerating BOTH encrypted forms and legacy plaintext: a value carrying the randomized
    /// or deterministic prefix is decrypted with the matching subkey; anything else is returned verbatim (so a gradual
    /// backfill and half-migrated tables read correctly).
    /// </summary>
    public static string DecryptTolerant(UserKeys keys, string stored)
    {
        if (stored.StartsWith(RandomizedPrefix, StringComparison.Ordinal))
            return Open(keys.Randomized, stored.AsSpan(RandomizedPrefix.Length));
        if (stored.StartsWith(DeterministicPrefix, StringComparison.Ordinal))
            return Open(keys.Deterministic, stored.AsSpan(DeterministicPrefix.Length));
        return stored;   // legacy plaintext
    }

    private static string Seal(byte[] key, byte[] nonce, string plaintext) =>
        Seal(key, nonce, Encoding.UTF8.GetBytes(plaintext));

    private static string Seal(byte[] key, byte[] nonce, byte[] plain)
    {
        var cipher = new byte[plain.Length];
        var tag = new byte[TagBytes];
        using (var aes = new AesGcm(key, TagBytes))
            aes.Encrypt(nonce, plain, cipher, tag);

        var frame = new byte[NonceBytes + TagBytes + cipher.Length];
        nonce.CopyTo(frame, 0);
        tag.CopyTo(frame, NonceBytes);
        cipher.CopyTo(frame, NonceBytes + TagBytes);
        return Convert.ToBase64String(frame);
    }

    private static string Open(byte[] key, ReadOnlySpan<char> base64)
    {
        var frame = Convert.FromBase64String(base64.ToString());
        var nonce = frame.AsSpan(0, NonceBytes);
        var tag = frame.AsSpan(NonceBytes, TagBytes);
        var cipher = frame.AsSpan(NonceBytes + TagBytes);
        var plain = new byte[cipher.Length];
        using (var aes = new AesGcm(key, TagBytes))
            aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}
