using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace MTPlayer.Server.Security;

public sealed class PasswordHasher
{
    private const int MemorySizeKiB = 64 * 1024;
    private const int Iterations = 3;
    private const int Parallelism = 2;
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int MaximumEncodedLength = 256;
    private const string Prefix = "$argon2id$v=19$m=65536,t=3,p=2$";

    public string HashPassword(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = DeriveHash(password, salt);

        try
        {
            return $"{Prefix}{Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    public bool VerifyPassword(string encoded, string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        if (!TryParse(encoded, out var salt, out var expectedHash))
        {
            return false;
        }

        var actualHash = DeriveHash(password, salt);
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

    private static byte[] DeriveHash(string password, byte[] salt)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            var argon2 = new Argon2id(passwordBytes)
            {
                Salt = salt,
                MemorySize = MemorySizeKiB,
                Iterations = Iterations,
                DegreeOfParallelism = Parallelism,
            };

            return argon2.GetBytes(HashSize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    private static bool TryParse(string? encoded, out byte[] salt, out byte[] hash)
    {
        salt = [];
        hash = [];

        if (string.IsNullOrEmpty(encoded) ||
            encoded.Length > MaximumEncodedLength ||
            !encoded.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var fields = encoded.Split('$', StringSplitOptions.None);
        if (fields.Length != 6 ||
            fields[0].Length != 0 ||
            fields[1] != "argon2id" ||
            fields[2] != "v=19" ||
            fields[3] != "m=65536,t=3,p=2" ||
            !TryDecodeCanonicalBase64(fields[4], SaltSize, out salt) ||
            !TryDecodeCanonicalBase64(fields[5], HashSize, out hash))
        {
            salt = [];
            hash = [];
            return false;
        }

        return true;
    }

    private static bool TryDecodeCanonicalBase64(string encoded, int expectedLength, out byte[] decoded)
    {
        decoded = [];
        if (encoded.Any(char.IsWhiteSpace))
        {
            return false;
        }

        try
        {
            decoded = Convert.FromBase64String(encoded);
            if (decoded.Length != expectedLength ||
                !string.Equals(Convert.ToBase64String(decoded), encoded, StringComparison.Ordinal))
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
