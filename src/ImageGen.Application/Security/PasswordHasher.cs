//TODO: CHECK FOR FALLBACKS
using System.Security.Cryptography;

namespace ImageGen.Application.Security;

/// <summary>
/// Self-contained PBKDF2 password hashing (pure BCL — no Identity/EF). The stored string carries the
/// algorithm, iteration count, salt and hash, so it is self-describing and future-proof:
/// <c>PBKDF2$&lt;iterations&gt;$&lt;saltBase64&gt;$&lt;hashBase64&gt;</c>.
/// </summary>
public static class PasswordHasher
{
    private const int Iterations = 200_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA256;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, Algorithm, HashBytes);
        return $"PBKDF2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>Check <paramref name="password"/> against a stored hash. The ONLY thing false means is that the
    /// password does not match.
    /// <para>A defect in <paramref name="stored"/> itself THROWS. Every branch below used to answer false for it,
    /// which reported a corrupt credential row as "wrong password": the account could never be logged into, the
    /// person was told the one thing that was not true and would keep retrying, and nothing anywhere recorded that
    /// the record was damaged. A row that cannot be evaluated is a server fault and reads as one — a 500 the
    /// operator can see and fix, not a login failure the user is blamed for. Messages describe the defect only;
    /// the stored value is credential material and never appears in them.</para></summary>
    public static bool Verify(string password, string stored)
    {
        if (string.IsNullOrEmpty(stored))
            throw new InvalidOperationException("Stored password hash is missing.");

        var parts = stored.Split('$');
        if (parts.Length != 4)
            throw new InvalidOperationException(
                $"Stored password hash is malformed: expected 4 '$'-separated fields, found {parts.Length}.");
        if (parts[0] != "PBKDF2")
            throw new InvalidOperationException("Stored password hash names an unknown algorithm (expected PBKDF2).");
        if (!int.TryParse(parts[1], out var iterations) || iterations <= 0)
            throw new InvalidOperationException("Stored password hash carries an unreadable iteration count.");

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Stored password hash has a corrupt salt or digest (not valid base64).", ex);
        }
        if (salt.Length == 0 || expected.Length == 0)
            throw new InvalidOperationException("Stored password hash has an empty salt or digest.");

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, Algorithm, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
