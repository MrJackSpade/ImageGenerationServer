using System.Security.Cryptography;
using ImageGen.Application.Security;

namespace ImageGen.Tests;

/// <summary>Pure (no-DB) tests of the column cipher primitives: round-trip, determinism, isolation, tolerance.</summary>
public sealed class UserCryptoTests
{
    private static UserCrypto.UserKeys NewKeys() =>
        UserCrypto.DeriveSubkeys(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void Randomized_RoundTrips_AndIsNonDeterministic()
    {
        var keys = NewKeys();
        const string plain = "1girl, solo, embarrassing tag, by some_artist";

        var a = UserCrypto.EncryptRandomized(keys, plain);
        var b = UserCrypto.EncryptRandomized(keys, plain);

        Assert.StartsWith(UserCrypto.RandomizedPrefix, a);
        Assert.NotEqual(a, b);                                  // fresh nonce each call
        Assert.Equal(plain, UserCrypto.DecryptTolerant(keys, a));
        Assert.Equal(plain, UserCrypto.DecryptTolerant(keys, b));
    }

    [Fact]
    public void Deterministic_IsStable_ForSamePlaintextAndKey()
    {
        var keys = NewKeys();

        var a = UserCrypto.EncryptDeterministic(keys, "long_hair");
        var b = UserCrypto.EncryptDeterministic(keys, "long_hair");

        Assert.StartsWith(UserCrypto.DeterministicPrefix, a);
        Assert.Equal(a, b);                                     // equality preserved → searchable / UNIQUE-safe
        Assert.NotEqual(a, UserCrypto.EncryptDeterministic(keys, "short_hair"));
        Assert.Equal("long_hair", UserCrypto.DecryptTolerant(keys, a));
    }

    [Fact]
    public void DifferentUsers_ProduceDifferentCiphertext_ForSameToken()
    {
        var alice = NewKeys();
        var bob = NewKeys();

        Assert.NotEqual(
            UserCrypto.EncryptDeterministic(alice, "long_hair"),
            UserCrypto.EncryptDeterministic(bob, "long_hair"));
    }

    [Fact]
    public void DecryptTolerant_PassesThroughLegacyPlaintext()
    {
        var keys = NewKeys();
        Assert.Equal("not yet encrypted", UserCrypto.DecryptTolerant(keys, "not yet encrypted"));
    }

    [Fact]
    public void Decrypt_WithWrongKey_Throws()
    {
        var cipher = UserCrypto.EncryptRandomized(NewKeys(), "secret");
        Assert.Throws<AuthenticationTagMismatchException>(() => UserCrypto.DecryptTolerant(NewKeys(), cipher));
    }
}
