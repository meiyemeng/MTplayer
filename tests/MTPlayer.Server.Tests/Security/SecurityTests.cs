using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MTPlayer.Server.Auth;
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
    public void Disposed_secret_protector_rejects_further_use()
    {
        var protector = new AesGcmSecretProtector(TestDataEncryptionKey);
        var encoded = protector.Protect("secret");

        protector.Dispose();

        Assert.Throws<ObjectDisposedException>(() => protector.Protect("secret"));
        Assert.Throws<ObjectDisposedException>(() => protector.Unprotect(encoded));
        protector.Dispose();
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

    [Fact]
    public void Password_hash_accepts_10_and_128_character_boundaries()
    {
        var hasher = new PasswordHasher();
        var minimum = new string('a', 10);
        var maximum = new string('z', 128);

        var minimumHash = hasher.HashPassword(minimum);
        var maximumHash = hasher.HashPassword(maximum);

        Assert.True(hasher.VerifyPassword(minimumHash, minimum));
        Assert.True(hasher.VerifyPassword(maximumHash, maximum));
    }

    [Fact]
    public void Password_hash_rejects_null_and_out_of_range_passwords()
    {
        var hasher = new PasswordHasher();

        Assert.Throws<ArgumentNullException>(() => hasher.HashPassword(null!));
        foreach (var invalid in new[]
                 {
                     string.Empty,
                     new string('a', 9),
                     new string('a', 129),
                 })
        {
            var exception = Assert.Throws<ArgumentException>(() => hasher.HashPassword(invalid));
            Assert.Contains("between 10 and 128 characters", exception.Message, StringComparison.Ordinal);
        }

        var oversized = new string('a', 1_000_000);
        var stopwatch = Stopwatch.StartNew();
        Assert.Throws<ArgumentException>(() => hasher.HashPassword(oversized));
        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public void Password_verify_rejects_out_of_range_passwords_without_derivation()
    {
        var hasher = new PasswordHasher();
        var validHash = hasher.HashPassword("0123456789");

        Assert.Throws<ArgumentNullException>(() => hasher.VerifyPassword(validHash, null!));
        Assert.False(hasher.VerifyPassword(validHash, string.Empty));
        Assert.False(hasher.VerifyPassword(validHash, new string('a', 9)));
        Assert.False(hasher.VerifyPassword(validHash, new string('a', 129)));

        var oversized = new string('a', 1_000_000);
        var stopwatch = Stopwatch.StartNew();
        Assert.False(hasher.VerifyPassword(validHash, oversized));
        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(500));
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
        var firstBytes = Convert.FromBase64String(first);

        Assert.NotEqual(first, second);
        Assert.Equal(32, firstBytes.Length);
        Assert.Equal(
            SHA256.HashData(firstBytes),
            Convert.FromBase64String(hash));
        Assert.True(factory.VerifyToken(first, hash));
        Assert.False(factory.VerifyToken(second, hash));
        Assert.False(factory.VerifyToken(first, "not-base64"));
    }

    [Fact]
    public void Jwt_signing_key_is_stably_domain_separated_from_the_data_encryption_key()
    {
        var first = JwtOptions.DeriveSigningKey(TestDataEncryptionKey);
        var second = JwtOptions.DeriveSigningKey(TestDataEncryptionKey);
        var source = Convert.FromBase64String(TestDataEncryptionKey);
        using var hmac = new HMACSHA256(source);
        var expected = hmac.ComputeHash(Encoding.UTF8.GetBytes("mtplayer-jwt-signing-v1"));

        Assert.Equal(expected, first);
        Assert.Equal(first, second);
        Assert.NotEqual(source, first);
        Assert.NotEqual(SHA256.HashData(source), first);
        Assert.Equal(32, first.Length);
        CryptographicOperations.ZeroMemory(first);
        CryptographicOperations.ZeroMemory(second);
        CryptographicOperations.ZeroMemory(source);
        CryptographicOperations.ZeroMemory(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==")]
    public void Jwt_key_derivation_rejects_invalid_data_encryption_keys(string encodedKey)
    {
        Assert.Throws<ArgumentException>(() => JwtOptions.FromDataEncryptionKey(encodedKey));
    }

    [Fact]
    public void Token_hash_uses_decoded_32_byte_fixture()
    {
        var factory = new TokenFactory();
        var rawToken = Enumerable.Range(0, 32).Select(index => (byte)index).ToArray();
        var token = Convert.ToBase64String(rawToken);
        var expectedHash = Convert.ToBase64String(SHA256.HashData(rawToken));

        Assert.Equal(expectedHash, factory.HashToken(token));
        Assert.True(factory.VerifyToken(token, expectedHash));
    }

    [Fact]
    public void Token_hash_rejects_noncanonical_wrong_length_and_oversized_tokens()
    {
        var factory = new TokenFactory();
        var canonical = Convert.ToBase64String(new byte[32]);

        Assert.Throws<ArgumentNullException>(() => factory.HashToken(null!));
        foreach (var invalid in new[]
                 {
                     string.Empty,
                     "not-base64",
                     canonical[..^1],
                     $" {canonical}",
                     Convert.ToBase64String(new byte[31]),
                     Convert.ToBase64String(new byte[33]),
                     new string('A', 10_000),
                 })
        {
            Assert.Throws<ArgumentException>(() => factory.HashToken(invalid));
        }
    }

    [Fact]
    public void Token_verify_quickly_rejects_invalid_tokens_and_hashes()
    {
        var factory = new TokenFactory();
        var token = factory.CreateRefreshToken();
        var hash = factory.HashToken(token);
        var invalidValues = new[]
        {
            string.Empty,
            "not-base64",
            token[..^1],
            $"{token}\n",
            Convert.ToBase64String(new byte[31]),
            Convert.ToBase64String(new byte[33]),
            new string('A', 10_000),
        };

        Assert.False(factory.VerifyToken(null!, hash));
        Assert.False(factory.VerifyToken(token, null!));
        foreach (var invalid in invalidValues)
        {
            Assert.False(factory.VerifyToken(invalid, hash));
            Assert.False(factory.VerifyToken(token, invalid));
        }
    }

    [Fact]
    public void Security_services_are_registered_when_both_required_settings_are_valid()
    {
        using var factory = CreateFactory(TestPostgreSqlConnectionString, TestDataEncryptionKey);

        Assert.IsType<AesGcmSecretProtector>(factory.Services.GetRequiredService<ISecretProtector>());
        Assert.IsType<PasswordHasher>(factory.Services.GetRequiredService<PasswordHasher>());
        Assert.IsType<TokenFactory>(factory.Services.GetRequiredService<TokenFactory>());
    }

    [Fact]
    public void Dependency_injection_disposes_the_secret_protector_on_shutdown()
    {
        using var factory = CreateFactory(TestPostgreSqlConnectionString, TestDataEncryptionKey);
        var protector = factory.Services.GetRequiredService<ISecretProtector>();

        factory.Dispose();

        Assert.Throws<ObjectDisposedException>(() => protector.Protect("secret"));
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
