using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MTPlayer.Server.Data;
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

    private JwtOptions(byte[] signingKey) => _signingKey = signingKey;

    internal SigningCredentials SigningCredentials
    {
        get
        {
            ObjectDisposedException.ThrowIf(_signingKey is null, this);
            return new SigningCredentials(
                new SymmetricSecurityKey(_signingKey),
                SecurityAlgorithms.HmacSha256);
        }
    }

    internal SecurityKey CreateValidationKey()
    {
        ObjectDisposedException.ThrowIf(_signingKey is null, this);
        return new SymmetricSecurityKey(_signingKey);
    }

    internal bool IsOwnedDerivedKeyArrayCleared => _signingKey is null;

    internal bool IsDisposed => _signingKey is null;

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

    }
}

public sealed class ConfigureJwtBearerOptions(JwtOptions jwtOptions) : IConfigureNamedOptions<JwtBearerOptions>
{
    public void Configure(JwtBearerOptions options) =>
        Configure(JwtBearerDefaults.AuthenticationScheme, options);

    public void Configure(string? name, JwtBearerOptions options)
    {
        if (!string.Equals(name, JwtBearerDefaults.AuthenticationScheme, StringComparison.Ordinal))
        {
            return;
        }

        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = JwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = JwtOptions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeyResolver = (_, _, _, _) => [jwtOptions.CreateValidationKey()],
            RequireExpirationTime = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "sub",
            RoleClaimType = "role",
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var subject = context.Principal?.FindFirst("sub")?.Value;
                if (!Guid.TryParse(subject, out var userId))
                {
                    context.Fail("account_not_active");
                    return;
                }

                var tokenRole = context.Principal?.FindFirst("role")?.Value;
                var db = context.HttpContext.RequestServices.GetRequiredService<ApiDbContext>();
                var active = tokenRole is not null && await db.Users.AnyAsync(
                    user =>
                        user.Id == userId &&
                        user.EmailVerified &&
                        !user.Disabled &&
                        user.Role == tokenRole,
                    context.HttpContext.RequestAborted);
                if (!active)
                {
                    context.Fail("account_not_active");
                }
            },
        };
    }
}
