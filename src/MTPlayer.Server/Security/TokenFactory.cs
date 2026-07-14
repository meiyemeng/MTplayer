using System.Security.Cryptography;
using System.Text;

namespace MTPlayer.Server.Security;

public sealed class TokenFactory
{
    private const int TokenSize = 32;
    private const int HashSize = 32;

    public string CreateRefreshToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenSize));

    public string HashToken(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    public bool VerifyToken(string token, string encodedHash)
    {
        ArgumentNullException.ThrowIfNull(token);

        if (!TryDecodeHash(encodedHash, out var expectedHash))
        {
            return false;
        }

        var actualHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        try
        {
            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actualHash);
            CryptographicOperations.ZeroMemory(expectedHash);
        }
    }

    private static bool TryDecodeHash(string? encodedHash, out byte[] hash)
    {
        hash = [];
        if (string.IsNullOrEmpty(encodedHash) || encodedHash.Any(char.IsWhiteSpace))
        {
            return false;
        }

        try
        {
            hash = Convert.FromBase64String(encodedHash);
            if (hash.Length != HashSize ||
                !string.Equals(Convert.ToBase64String(hash), encodedHash, StringComparison.Ordinal))
            {
                hash = [];
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
