using System.Security.Cryptography;

namespace MTPlayer.Server.Security;

public sealed class TokenFactory
{
    private const int TokenSize = 32;
    private const int HashSize = 32;

    public string CreateRefreshToken()
    {
        var rawToken = RandomNumberGenerator.GetBytes(TokenSize);
        try
        {
            return Convert.ToBase64String(rawToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rawToken);
        }
    }

    public string HashToken(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (!TryDecodeCanonicalBase64(token, TokenSize, out var rawToken))
        {
            throw new ArgumentException(
                "Token must be canonical Base64 encoding exactly 32 bytes.",
                nameof(token));
        }

        byte[]? hash = null;
        try
        {
            hash = SHA256.HashData(rawToken);
            return Convert.ToBase64String(hash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rawToken);
            if (hash is not null)
            {
                CryptographicOperations.ZeroMemory(hash);
            }
        }
    }

    public bool VerifyToken(string token, string encodedHash)
    {
        if (!TryDecodeCanonicalBase64(token, TokenSize, out var rawToken))
        {
            return false;
        }

        if (!TryDecodeCanonicalBase64(encodedHash, HashSize, out var expectedHash))
        {
            CryptographicOperations.ZeroMemory(rawToken);
            return false;
        }

        byte[]? actualHash = null;
        try
        {
            actualHash = SHA256.HashData(rawToken);
            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rawToken);
            CryptographicOperations.ZeroMemory(expectedHash);
            if (actualHash is not null)
            {
                CryptographicOperations.ZeroMemory(actualHash);
            }
        }
    }

    private static bool TryDecodeCanonicalBase64(
        string? encoded,
        int expectedLength,
        out byte[] decoded)
    {
        decoded = [];
        var expectedEncodedLength = ((expectedLength + 2) / 3) * 4;
        if (encoded is null || encoded.Length != expectedEncodedLength)
        {
            return false;
        }

        try
        {
            decoded = Convert.FromBase64String(encoded);
            if (decoded.Length != expectedLength ||
                !string.Equals(Convert.ToBase64String(decoded), encoded, StringComparison.Ordinal))
            {
                CryptographicOperations.ZeroMemory(decoded);
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
