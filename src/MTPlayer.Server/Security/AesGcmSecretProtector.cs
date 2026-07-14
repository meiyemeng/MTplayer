using System.Security.Cryptography;
using System.Text;

namespace MTPlayer.Server.Security;

public sealed class AesGcmSecretProtector : ISecretProtector
{
    private const byte EnvelopeVersion = 1;
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int EnvelopeHeaderSize = 1 + NonceSize + TagSize;
    private const int MinimumEnvelopeSize = EnvelopeHeaderSize + 1;
    private const string KeyErrorMessage = "DATA_ENCRYPTION_KEY must be a Base64 encoded 32-byte key";
    private const string EnvelopeErrorMessage = "Encrypted value has an invalid AES-GCM envelope.";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly byte[] _key;

    public AesGcmSecretProtector(string encodedKey)
    {
        if (!TryDecodeCanonicalBase64(encodedKey, KeySize, out _key))
        {
            throw new ArgumentException(KeyErrorMessage, nameof(encodedKey));
        }
    }

    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        if (plaintext.Length == 0)
        {
            throw new ArgumentException("Plaintext cannot be empty.", nameof(plaintext));
        }

        var plaintextBytes = StrictUtf8.GetBytes(plaintext);
        var envelope = new byte[EnvelopeHeaderSize + plaintextBytes.Length];
        envelope[0] = EnvelopeVersion;

        var nonce = envelope.AsSpan(1, NonceSize);
        var tag = envelope.AsSpan(1 + NonceSize, TagSize);
        var ciphertext = envelope.AsSpan(EnvelopeHeaderSize);
        RandomNumberGenerator.Fill(nonce);

        try
        {
            using var aes = new AesGcm(_key, TagSize);
            aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);
            return Convert.ToBase64String(envelope);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }
    }

    public string Unprotect(string encoded)
    {
        if (!TryDecodeCanonicalBase64(encoded, out var envelope) ||
            envelope.Length < MinimumEnvelopeSize ||
            envelope[0] != EnvelopeVersion)
        {
            throw new CryptographicException(EnvelopeErrorMessage);
        }

        var nonce = envelope.AsSpan(1, NonceSize);
        var tag = envelope.AsSpan(1 + NonceSize, TagSize);
        var ciphertext = envelope.AsSpan(EnvelopeHeaderSize);
        var plaintext = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return StrictUtf8.GetString(plaintext);
        }
        catch (CryptographicException exception)
        {
            throw new CryptographicException(EnvelopeErrorMessage, exception);
        }
        catch (DecoderFallbackException exception)
        {
            throw new CryptographicException(EnvelopeErrorMessage, exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static bool TryDecodeCanonicalBase64(
        string? encoded,
        int expectedLength,
        out byte[] decoded)
    {
        if (!TryDecodeCanonicalBase64(encoded, out decoded) || decoded.Length != expectedLength)
        {
            decoded = [];
            return false;
        }

        return true;
    }

    private static bool TryDecodeCanonicalBase64(string? encoded, out byte[] decoded)
    {
        decoded = [];
        if (string.IsNullOrEmpty(encoded) || encoded.Any(char.IsWhiteSpace))
        {
            return false;
        }

        try
        {
            decoded = Convert.FromBase64String(encoded);
            if (!string.Equals(Convert.ToBase64String(decoded), encoded, StringComparison.Ordinal))
            {
                decoded = [];
                return false;
            }

            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
