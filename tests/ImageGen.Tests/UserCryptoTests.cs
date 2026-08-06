using ImageGen.Application.Security;
using System.Security.Cryptography;

namespace ImageGen.Tests;

/// <summary>Pure (no-DB) tests of the column cipher primitives: round-trip, determinism, isolation, tolerance.</summary>
public sealed class UserCryptoTests
{
    private static UserCrypto.UserKeys NewKeys() =>
        UserCrypto.DeriveSubkeys(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void Randomized_RoundTrips_AndIsNonDeterministic()
    {
        UserCrypto.UserKeys keys = NewKeys();
        const string plain = "1girl, solo, embarrassing tag, by some_artist";

        string a = UserCrypto.EncryptRandomized(keys, plain);
        string b = UserCrypto.EncryptRandomized(keys, plain);

        Assert.StartsWith(UserCrypto.Prefixes.Randomized, a);
        Assert.NotEqual(a, b);                                  // fresh nonce each call
        Assert.Equal(plain, UserCrypto.DecryptTolerant(keys, a));
        Assert.Equal(plain, UserCrypto.DecryptTolerant(keys, b));
    }

    [Fact]
    public void Deterministic_IsStable_ForSamePlaintextAndKey()
    {
        UserCrypto.UserKeys keys = NewKeys();

        string a = UserCrypto.EncryptDeterministic(keys, "long_hair");
        string b = UserCrypto.EncryptDeterministic(keys, "long_hair");

        Assert.StartsWith(UserCrypto.Prefixes.Deterministic, a);
        Assert.Equal(a, b);                                     // equality preserved → searchable / UNIQUE-safe
        Assert.NotEqual(a, UserCrypto.EncryptDeterministic(keys, "short_hair"));
        Assert.Equal("long_hair", UserCrypto.DecryptTolerant(keys, a));
    }

    [Fact]
    public void DifferentUsers_ProduceDifferentCiphertext_ForSameToken()
    {
        UserCrypto.UserKeys alice = NewKeys();
        UserCrypto.UserKeys bob = NewKeys();

        Assert.NotEqual(
            UserCrypto.EncryptDeterministic(alice, "long_hair"),
            UserCrypto.EncryptDeterministic(bob, "long_hair"));
    }

    [Fact]
    public void DecryptTolerant_PassesThroughLegacyPlaintext()
    {
        UserCrypto.UserKeys keys = NewKeys();
        Assert.Equal("not yet encrypted", UserCrypto.DecryptTolerant(keys, "not yet encrypted"));
    }

    [Fact]
    public void Decrypt_WithWrongKey_Throws()
    {
        string cipher = UserCrypto.EncryptRandomized(NewKeys(), "secret");
        _ = Assert.Throws<AuthenticationTagMismatchException>(() => UserCrypto.DecryptTolerant(NewKeys(), cipher));
    }
}
