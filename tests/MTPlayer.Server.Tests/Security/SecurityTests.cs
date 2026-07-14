using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MTPlayer.Server.Security;
using Xunit;

namespace MTPlayer.Server.Tests.Security;

public sealed class SecurityTests
{
    private const string PostgreSqlConfigurationKey = "ConnectionStrings:PostgreSQL";
    private const string DataEncryptionKeyConfigurationKey = "DATA_ENCRYPTION_KEY";
    private const string TestPostgreSqlConnectionString =
        "Host=localhost;Database=mtplayer_tests;Username=mtplayer_tests";

    private static readonly string TestDataEncryptionKey = Convert.ToBase64String(
        Enumerable.Range(0, 32).Select(index => (byte)index).ToArray());

    [Fact]
    public void Secret_round_trip_uses_random_nonce_and_expected_envelope()
    {
        var protector = new AesGcmSecretProtector(TestDataEncryptionKey);

        var first = protector.Protect("smtp-password");
        var second = protector.Protect("smtp-password");
        var envelope = Convert.FromBase64String(first);

        Assert.NotEqual(first, second);
        Assert.Equal(1, envelope[0]);
        Assert.Equal(1 + 12 + 16 + Encoding.UTF8.GetByteCount("smtp-password"), envelope.Length);
        Assert.Equal("smtp-password", protector.Unprotect(first));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==")]
    [InlineData(" AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=")]
    [InlineData("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=\n")]
    public void Encryption_key_must_be_canonical_base64_for_exactly_32_bytes(string encodedKey)
    {
        var exception = Assert.Throws<ArgumentException>(() => new AesGcmSecretProtector(encodedKey));

        Assert.Contains("Base64 encoded 32-byte key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Wrong_key_and_tampered_ciphertext_are_rejected()
    {
        var protector = new AesGcmSecretProtector(TestDataEncryptionKey);
        var otherKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var encoded = protector.Protect("secret");
        var envelope = Convert.FromBase64String(encoded);
        envelope[^1] ^= 0x01;

        Assert.Throws<CryptographicException>(() =>
            new AesGcmSecretProtector(otherKey).Unprotect(encoded));
        Assert.Throws<CryptographicException>(() =>
            protector.Unprotect(Convert.ToBase64String(envelope)));
    }

    [Fact]
    public void Malformed_or_unsupported_envelopes_are_rejected()
    {
        var protector = new AesGcmSecretProtector(TestDataEncryptionKey);
        var tooShort = Convert.ToBase64String(new byte[29]);
        var unsupportedVersion = Convert.FromBase64String(protector.Protect("secret"));
        unsupportedVersion[0] = 2;

        Assert.Throws<CryptographicException>(() => protector.Unprotect("not-base64"));
        Assert.Throws<CryptographicException>(() => protector.Unprotect(tooShort));
        Assert.Throws<CryptographicException>(() =>
            protector.Unprotect(Convert.ToBase64String(unsupportedVersion)));
    }

    [Fact]
    public void Password_hash_uses_required_argon2id_parameters_and_random_salt()
    {
        var hasher = new PasswordHasher();

        var first = hasher.HashPassword("Correct-Horse-2026");
        var second = hasher.HashPassword("Correct-Horse-2026");

        Assert.NotEqual(first, second);
        Assert.StartsWith("$argon2id$v=19$m=65536,t=3,p=2$", first, StringComparison.Ordinal);
        Assert.True(hasher.VerifyPassword(first, "Correct-Horse-2026"));
        Assert.False(hasher.VerifyPassword(first, "wrong-password"));

        var fields = first.Split('$');
        Assert.Equal(16, Convert.FromBase64String(fields[4]).Length);
        Assert.Equal(32, Convert.FromBase64String(fields[5]).Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-password-hash")]
    [InlineData("$argon2i$v=19$m=65536,t=3,p=2$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("$argon2id$v=16$m=65536,t=3,p=2$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("$argon2id$v=19$m=2147483647,t=3,p=2$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("$argon2id$v=19$m=65536,t=3,p=2$bad-base64$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("$argon2id$v=19$m=65536,t=3,p=2$AAAAAAAAAAAAAAAAAAAAAA==$bad-base64")]
    [InlineData("$argon2id$v=19$m=65536,t=3,p=2$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=$extra")]
    public void Malformed_password_hashes_are_rejected_without_throwing(string encoded)
    {
        var hasher = new PasswordHasher();

        Assert.False(hasher.VerifyPassword(encoded, "Correct-Horse-2026"));
    }

    [Fact]
    public void Oversized_password_hash_is_rejected_without_expensive_parsing()
    {
        var hasher = new PasswordHasher();

        Assert.False(hasher.VerifyPassword(new string('A', 10_000), "Correct-Horse-2026"));
    }

    [Fact]
    public void Refresh_tokens_have_32_random_bytes_and_sha256_hashes()
    {
        var factory = new TokenFactory();

        var first = factory.CreateRefreshToken();
        var second = factory.CreateRefreshToken();
        var hash = factory.HashToken(first);

        Assert.NotEqual(first, second);
        Assert.Equal(32, Convert.FromBase64String(first).Length);
        Assert.Equal(
            SHA256.HashData(Encoding.UTF8.GetBytes(first)),
            Convert.FromBase64String(hash));
        Assert.True(factory.VerifyToken(first, hash));
        Assert.False(factory.VerifyToken(second, hash));
        Assert.False(factory.VerifyToken(first, "not-base64"));
    }

    [Fact]
    public void Security_services_are_registered_when_both_required_settings_are_valid()
    {
        using var factory = CreateFactory(TestPostgreSqlConnectionString, TestDataEncryptionKey);

        Assert.IsType<AesGcmSecretProtector>(factory.Services.GetRequiredService<ISecretProtector>());
        Assert.IsType<PasswordHasher>(factory.Services.GetRequiredService<PasswordHasher>());
        Assert.IsType<TokenFactory>(factory.Services.GetRequiredService<TokenFactory>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==")]
    public void Missing_or_invalid_data_encryption_key_fails_during_server_startup(string key)
    {
        using var factory = CreateFactory(TestPostgreSqlConnectionString, key);

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains(
            "DATA_ENCRYPTION_KEY must be a Base64 encoded 32-byte key",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Data_encryption_key_error_takes_priority_when_both_required_settings_are_missing()
    {
        using var factory = CreateFactory(string.Empty, string.Empty);

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains(
            "DATA_ENCRYPTION_KEY must be a Base64 encoded 32-byte key",
            exception.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(PostgreSqlConfigurationKey, exception.Message, StringComparison.Ordinal);
    }

    private static WebApplicationFactory<Program> CreateFactory(
        string connectionString,
        string dataEncryptionKey) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(PostgreSqlConfigurationKey, connectionString);
            builder.UseSetting(DataEncryptionKeyConfigurationKey, dataEncryptionKey);
        });
}
