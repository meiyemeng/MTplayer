using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Mail;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using MTPlayer.Contracts;
using MTPlayer.Server.Data;
using MTPlayer.Server.Security;
using Npgsql;

namespace MTPlayer.Server.Auth;

public enum AuthStatus
{
    Success,
    Accepted,
    InvalidInput,
    DuplicateEmail,
    InvalidCredentials,
    InvalidToken,
    Disabled,
}

public sealed record AuthResult(AuthStatus Status, TokenResponse? Tokens = null);

public sealed class AuthService(
    ApiDbContext db,
    PasswordHasher passwordHasher,
    TokenFactory tokenFactory,
    JwtOptions jwtOptions,
    TimeProvider timeProvider)
{
    internal const string VerificationPurpose = "verify";
    internal const string ResetPurpose = "reset";
    internal const string VerificationExpirySetting = "EmailVerificationTokenExpiryMinutes";
    internal const string ResetExpirySetting = "PasswordResetTokenExpiryMinutes";
    internal const int DefaultVerificationExpiryMinutes = 60;
    internal const int DefaultResetExpiryMinutes = 30;

    public async Task<AuthStatus> RegisterAsync(
        RegisterRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeEmail(request.Email, out var email, out var normalizedEmail) ||
            !HasValidPasswordLength(request.Password))
        {
            return AuthStatus.InvalidInput;
        }

        var now = timeProvider.GetUtcNow();
        var passwordHash = passwordHasher.HashPassword(request.Password);
        var user = new UserEntity
        {
            Id = Guid.NewGuid(),
            Email = email,
            NormalizedEmail = normalizedEmail,
            PasswordHash = passwordHash,
            CreatedAtUtc = now,
        };
        db.Users.Add(user);
        await AddEmailTokenAndOutboxAsync(
            user,
            VerificationPurpose,
            VerificationExpirySetting,
            DefaultVerificationExpiryMinutes,
            now,
            cancellationToken);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return AuthStatus.Accepted;
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: "IX_users_NormalizedEmail",
            })
        {
            return AuthStatus.DuplicateEmail;
        }
    }

    public async Task<AuthStatus> VerifyEmailAsync(string token, CancellationToken cancellationToken)
    {
        if (!TryHashToken(token, out var tokenHash))
        {
            return AuthStatus.InvalidToken;
        }

        var now = timeProvider.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var candidate = await db.EmailTokens
            .AsNoTracking()
            .Where(value => value.TokenHash == tokenHash && value.Purpose == VerificationPurpose)
            .Select(value => new { value.Id, value.UserId })
            .SingleOrDefaultAsync(cancellationToken);
        if (candidate is null)
        {
            return AuthStatus.InvalidToken;
        }

        var consumed = await db.EmailTokens
            .Where(value => value.Id == candidate.Id && value.UsedAtUtc == null && value.ExpiresAtUtc > now)
            .ExecuteUpdateAsync(
                update => update.SetProperty(value => value.UsedAtUtc, now),
                cancellationToken);
        if (consumed != 1)
        {
            return AuthStatus.InvalidToken;
        }

        await db.Users
            .Where(user => user.Id == candidate.UserId)
            .ExecuteUpdateAsync(
                update => update.SetProperty(user => user.EmailVerified, true),
                cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return AuthStatus.Success;
    }

    public async Task<AuthResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        if (!TryNormalizeEmail(request.Email, out _, out var normalizedEmail) ||
            !HasValidPasswordLength(request.Password) ||
            !TryNormalizeDevice(request.DeviceName, 200, out var deviceName) ||
            !TryNormalizeDevice(request.Platform, 50, out var platform))
        {
            return new AuthResult(AuthStatus.InvalidInput);
        }

        var user = await db.Users.SingleOrDefaultAsync(
            value => value.NormalizedEmail == normalizedEmail,
            cancellationToken);
        if (user is null || !passwordHasher.VerifyPassword(user.PasswordHash, request.Password))
        {
            return new AuthResult(AuthStatus.InvalidCredentials);
        }

        if (user.Disabled)
        {
            return new AuthResult(AuthStatus.Disabled);
        }

        var now = timeProvider.GetUtcNow();
        if (!user.EmailVerified)
        {
            await AddEmailTokenAndOutboxAsync(
                user,
                VerificationPurpose,
                VerificationExpirySetting,
                DefaultVerificationExpiryMinutes,
                now,
                cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return new AuthResult(AuthStatus.Success, CreateTokenResponse(user, string.Empty, now));
        }

        var refreshToken = tokenFactory.CreateRefreshToken();
        db.DeviceSessions.Add(new DeviceSessionEntity
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            DeviceName = deviceName,
            Platform = platform,
            RefreshTokenHash = tokenFactory.HashToken(refreshToken),
            CreatedAtUtc = now,
            LastActivityAtUtc = now,
            ExpiresAtUtc = now.Add(JwtOptions.RefreshTokenLifetime),
        });
        await db.SaveChangesAsync(cancellationToken);
        return new AuthResult(AuthStatus.Success, CreateTokenResponse(user, refreshToken, now));
    }

    public async Task<AuthResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        if (!TryHashToken(refreshToken, out var oldHash))
        {
            return new AuthResult(AuthStatus.InvalidCredentials);
        }

        var session = await db.DeviceSessions
            .AsNoTracking()
            .Where(value => value.RefreshTokenHash == oldHash && value.RevokedAtUtc == null)
            .Select(value => new
            {
                value.Id,
                value.UserId,
                value.ExpiresAtUtc,
                User = value.User!,
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (session is null)
        {
            return new AuthResult(AuthStatus.InvalidCredentials);
        }

        var now = timeProvider.GetUtcNow();
        if (session.User.Disabled)
        {
            await db.DeviceSessions
                .Where(value => value.Id == session.Id && value.RevokedAtUtc == null)
                .ExecuteUpdateAsync(
                    update => update.SetProperty(value => value.RevokedAtUtc, now),
                    cancellationToken);
            return new AuthResult(AuthStatus.Disabled);
        }

        if (session.ExpiresAtUtc <= now || !session.User.EmailVerified)
        {
            await db.DeviceSessions
                .Where(value => value.Id == session.Id && value.RevokedAtUtc == null)
                .ExecuteUpdateAsync(
                    update => update.SetProperty(value => value.RevokedAtUtc, now),
                    cancellationToken);
            return new AuthResult(AuthStatus.InvalidCredentials);
        }

        var newRefreshToken = tokenFactory.CreateRefreshToken();
        var newHash = tokenFactory.HashToken(newRefreshToken);
        var rotated = await db.DeviceSessions
            .Where(value => value.Id == session.Id &&
                value.RefreshTokenHash == oldHash &&
                value.RevokedAtUtc == null &&
                value.ExpiresAtUtc > now)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(value => value.RefreshTokenHash, newHash)
                    .SetProperty(value => value.LastActivityAtUtc, now)
                    .SetProperty(value => value.ExpiresAtUtc, now.Add(JwtOptions.RefreshTokenLifetime)),
                cancellationToken);
        if (rotated != 1)
        {
            return new AuthResult(AuthStatus.InvalidCredentials);
        }

        return new AuthResult(AuthStatus.Success, CreateTokenResponse(session.User, newRefreshToken, now));
    }

    public async Task ForgotPasswordAsync(string emailInput, CancellationToken cancellationToken)
    {
        if (!TryNormalizeEmail(emailInput, out _, out var normalizedEmail))
        {
            return;
        }

        var user = await db.Users.SingleOrDefaultAsync(
            value => value.NormalizedEmail == normalizedEmail && !value.Disabled,
            cancellationToken);
        if (user is null)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        await AddEmailTokenAndOutboxAsync(
            user,
            ResetPurpose,
            ResetExpirySetting,
            DefaultResetExpiryMinutes,
            now,
            cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<AuthStatus> ResetPasswordAsync(
        string token,
        string password,
        CancellationToken cancellationToken)
    {
        if (!HasValidPasswordLength(password))
        {
            return AuthStatus.InvalidInput;
        }

        if (!TryHashToken(token, out var tokenHash))
        {
            return AuthStatus.InvalidToken;
        }

        var now = timeProvider.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var candidate = await db.EmailTokens
            .AsNoTracking()
            .Where(value => value.TokenHash == tokenHash && value.Purpose == ResetPurpose)
            .Select(value => new { value.Id, value.UserId })
            .SingleOrDefaultAsync(cancellationToken);
        if (candidate is null)
        {
            return AuthStatus.InvalidToken;
        }

        var consumed = await db.EmailTokens
            .Where(value => value.Id == candidate.Id && value.UsedAtUtc == null && value.ExpiresAtUtc > now)
            .ExecuteUpdateAsync(
                update => update.SetProperty(value => value.UsedAtUtc, now),
                cancellationToken);
        if (consumed != 1)
        {
            return AuthStatus.InvalidToken;
        }

        var passwordHash = passwordHasher.HashPassword(password);
        await db.Users
            .Where(user => user.Id == candidate.UserId)
            .ExecuteUpdateAsync(
                update => update.SetProperty(user => user.PasswordHash, passwordHash),
                cancellationToken);
        await db.DeviceSessions
            .Where(session => session.UserId == candidate.UserId && session.RevokedAtUtc == null)
            .ExecuteUpdateAsync(
                update => update.SetProperty(session => session.RevokedAtUtc, now),
                cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return AuthStatus.Success;
    }

    private TokenResponse CreateTokenResponse(UserEntity user, string refreshToken, DateTimeOffset now)
    {
        var expires = now.Add(JwtOptions.AccessTokenLifetime);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString("D", CultureInfo.InvariantCulture)),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture)),
            new("role", user.Role),
            new("email_verified", user.EmailVerified ? "true" : "false"),
            new("scope", user.EmailVerified ? "sync" : "verify-email"),
        };
        var jwt = new JwtSecurityToken(
            JwtOptions.Issuer,
            JwtOptions.Audience,
            claims,
            now.UtcDateTime,
            expires.UtcDateTime,
            jwtOptions.SigningCredentials);
        var accessToken = new JwtSecurityTokenHandler().WriteToken(jwt);
        return new TokenResponse(accessToken, refreshToken, expires, user.EmailVerified);
    }

    private async Task AddEmailTokenAndOutboxAsync(
        UserEntity user,
        string purpose,
        string settingKey,
        int defaultExpiryMinutes,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var expiryMinutes = await ReadExpiryMinutesAsync(
            settingKey,
            defaultExpiryMinutes,
            cancellationToken);
        var token = tokenFactory.CreateRefreshToken();
        db.EmailTokens.Add(new EmailTokenEntity
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = tokenFactory.HashToken(token),
            Purpose = purpose,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddMinutes(expiryMinutes),
        });
        db.MailOutbox.Add(new MailOutboxEntity
        {
            RecipientEmail = user.Email,
            Subject = purpose == VerificationPurpose ? "验证邮箱" : "重置密码",
            BodyHtml = $"{purpose}:{token}",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });
    }

    private async Task<int> ReadExpiryMinutesAsync(
        string key,
        int safeDefault,
        CancellationToken cancellationToken)
    {
        var value = await db.SystemSettings
            .AsNoTracking()
            .Where(setting => setting.Key == key && !setting.IsEncrypted)
            .Select(setting => setting.Value)
            .SingleOrDefaultAsync(cancellationToken);
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) &&
            parsed is >= 1 and <= 10_080
                ? parsed
                : safeDefault;
    }

    private bool TryHashToken(string token, out string hash)
    {
        try
        {
            hash = tokenFactory.HashToken(token);
            return true;
        }
        catch (ArgumentException)
        {
            hash = string.Empty;
            return false;
        }
    }

    private static bool HasValidPasswordLength(string? password) =>
        password is not null && password.Length is >= 10 and <= 128;

    private static bool TryNormalizeEmail(
        string? input,
        out string email,
        out string normalizedEmail)
    {
        email = input?.Trim() ?? string.Empty;
        normalizedEmail = string.Empty;
        if (email.Length is 0 or > 320 ||
            !MailAddress.TryCreate(email, out var address) ||
            !string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        normalizedEmail = email.ToUpperInvariant();
        return true;
    }

    private static bool TryNormalizeDevice(string? input, int maximumLength, out string normalized)
    {
        normalized = input?.Trim() ?? string.Empty;
        return normalized.Length is > 0 && normalized.Length <= maximumLength;
    }
}
