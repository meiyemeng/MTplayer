using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using MTPlayer.Server.Security;

[assembly: InternalsVisibleTo("MTPlayer.Server.Tests")]

namespace MTPlayer.Server.Auth;

public sealed class JwtOptions : IDisposable
{
    public const string Issuer = "MTPlayer.Server";
    public const string Audience = "MTPlayer.Clients";
    public static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(30);
    private const string SigningContext = "mtplayer-jwt-signing-v1";
    private byte[]? _signingKey;

    private JwtOptions(byte[] signingKey)
    {
        _signingKey = signingKey;
        SigningCredentials = new SigningCredentials(
            new SymmetricSecurityKey(signingKey),
            SecurityAlgorithms.HmacSha256);
        ValidationKey = new SymmetricSecurityKey(signingKey.ToArray());
    }

    internal SigningCredentials SigningCredentials { get; }

    internal SecurityKey ValidationKey { get; }

    public static JwtOptions FromDataEncryptionKey(string encodedKey) =>
        new(DeriveSigningKey(encodedKey));

    internal static byte[] DeriveSigningKey(string encodedKey)
    {
        AesGcmSecretProtector.ValidateKey(encodedKey);
        var sourceKey = Convert.FromBase64String(encodedKey);
        try
        {
            using var hmac = new HMACSHA256(sourceKey);
            return hmac.ComputeHash(Encoding.UTF8.GetBytes(SigningContext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sourceKey);
        }
    }

    public void Dispose()
    {
        if (_signingKey is not null)
        {
            CryptographicOperations.ZeroMemory(_signingKey);
            _signingKey = null;
        }

        if (ValidationKey is SymmetricSecurityKey validationKey)
        {
            CryptographicOperations.ZeroMemory(validationKey.Key);
        }
    }
}
