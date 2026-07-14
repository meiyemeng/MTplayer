using System.Buffers.Binary;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
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
    InvalidCredentials,
    InvalidToken,
    Disabled,
    VerificationRequired,
}

public sealed record AuthResult(AuthStatus Status, TokenResponse? Tokens = null);

public sealed class AuthService(
    ApiDbContext db,
    Argon2PasswordService passwords,
    TokenFactory tokenFactory,
    JwtOptions jwtOptions,
    TimeProvider timeProvider,
    ISecretProtector secretProtector,
    AuthTiming timing)
{
    internal const string VerificationPurpose = "verify";
    internal const string ResetPurpose = "reset";
    internal const string VerificationExpirySetting = "EmailVerificationTokenExpiryMinutes";
    internal const string ResetExpirySetting = "PasswordResetTokenExpiryMinutes";
    internal const string TokenCooldownSetting = "EmailTokenCooldownSeconds";
    internal const int DefaultVerificationExpiryMinutes = 60;
    internal const int DefaultResetExpiryMinutes = 30;
    internal const int DefaultTokenCooldownSeconds = 60;
    private const string EncryptedOutboxPrefix = "enc:v1:";

    public async Task<AuthStatus> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken)
    {
        var startedAt = timing.GetTimestamp();
        try
        {
            if (!TryNormalizeEmail(request.Email, out var email, out var normalizedEmail) ||
                !HasValidPasswordLength(request.Password))
            {
                return AuthStatus.InvalidInput;
            }

            var now = timeProvider.GetUtcNow();
            var passwordHash = await passwords.HashAsync(request.Password, cancellationToken);
            var prepared = await PrepareEmailTokenAsync(
                VerificationPurpose,
                VerificationExpirySetting,
                DefaultVerificationExpiryMinutes,
                now,
                cancellationToken);
            var user = new UserEntity
            {
                Id = Guid.NewGuid(),
                Email = email,
                NormalizedEmail = normalizedEmail,
                PasswordHash = passwordHash,
                CreatedAtUtc = now,
            };
            db.Users.Add(user);
            AddPreparedEmail(user.Id, user.Email, VerificationPurpose, prepared, now);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (
                exception.InnerException is PostgresException
                {
                    SqlState: PostgresErrorCodes.UniqueViolation,
                    ConstraintName: "IX_users_NormalizedEmail",
                })
            {
                db.ChangeTracker.Clear();
            }

            return AuthStatus.Accepted;
        }
        finally
        {
            await timing.CompleteAsync(startedAt, cancellationToken);
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
        var candidate = await db.EmailTokens.AsNoTracking()
            .Where(value => value.TokenHash == tokenHash && value.Purpose == VerificationPurpose)
            .Select(value => new { value.Id, value.UserId })
            .SingleOrDefaultAsync(cancellationToken);
        if (candidate is null)
        {
            return AuthStatus.InvalidToken;
        }

        var consumed = await db.EmailTokens
            .Where(value => value.Id == candidate.Id && value.UsedAtUtc == null && value.ExpiresAtUtc > now)
            .ExecuteUpdateAsync(update => update.SetProperty(value => value.UsedAtUtc, now), cancellationToken);
        if (consumed != 1)
        {
            return AuthStatus.InvalidToken;
        }

        await db.Users.Where(user => user.Id == candidate.UserId)
            .ExecuteUpdateAsync(update => update.SetProperty(user => user.EmailVerified, true), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return AuthStatus.Success;
    }

    public async Task<AuthResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        var startedAt = timing.GetTimestamp();
        try
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
            var passwordMatches = await passwords.VerifyOrDummyAsync(
                user?.PasswordHash,
                request.Password,
                cancellationToken);
            if (user is null || !passwordMatches)
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
                var prepared = await PrepareEmailTokenAsync(
                    VerificationPurpose,
                    VerificationExpirySetting,
                    DefaultVerificationExpiryMinutes,
                    now,
                    cancellationToken);
                await TryIssueEmailTokenAsync(user.Id, user.Email, VerificationPurpose, prepared, now, cancellationToken);
                return new AuthResult(AuthStatus.VerificationRequired);
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
        finally
        {
            await timing.CompleteAsync(startedAt, cancellationToken);
        }
    }

    public async Task<AuthResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        if (!TryHashToken(refreshToken, out var oldHash))
        {
            return new AuthResult(AuthStatus.InvalidCredentials);
        }

        var now = timeProvider.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var consumed = await db.ConsumedRefreshTokens.AsNoTracking()
            .SingleOrDefaultAsync(token => token.TokenHash == oldHash, cancellationToken);
        if (consumed is not null)
        {
            await RevokeSessionAsync(consumed.SessionId, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new AuthResult(AuthStatus.InvalidCredentials);
        }

        var session = await db.DeviceSessions
            .FromSqlInterpolated($"SELECT * FROM device_sessions WHERE \"RefreshTokenHash\" = {oldHash} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (session is null)
        {
            consumed = await db.ConsumedRefreshTokens.AsNoTracking()
                .SingleOrDefaultAsync(token => token.TokenHash == oldHash, cancellationToken);
            if (consumed is not null)
            {
                await RevokeSessionAsync(consumed.SessionId, now, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }

            return new AuthResult(AuthStatus.InvalidCredentials);
        }

        var user = await db.Users
            .FromSqlInterpolated($"SELECT * FROM users WHERE \"Id\" = {session.UserId} FOR UPDATE")
            .SingleAsync(cancellationToken);
        if (user.Disabled)
        {
            session.RevokedAtUtc = now;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new AuthResult(AuthStatus.Disabled);
        }

        if (!user.EmailVerified || session.ExpiresAtUtc <= now || session.RevokedAtUtc is not null)
        {
            session.RevokedAtUtc = now;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new AuthResult(AuthStatus.InvalidCredentials);
        }

        var newRefreshToken = tokenFactory.CreateRefreshToken();
        db.ConsumedRefreshTokens.Add(new ConsumedRefreshTokenEntity
        {
            TokenHash = oldHash,
            SessionId = session.Id,
            ConsumedAtUtc = now,
            ExpiresAtUtc = session.ExpiresAtUtc,
        });
        session.RefreshTokenHash = tokenFactory.HashToken(newRefreshToken);
        session.LastActivityAtUtc = now;
        session.ExpiresAtUtc = now.Add(JwtOptions.RefreshTokenLifetime);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AuthResult(AuthStatus.Success, CreateTokenResponse(user, newRefreshToken, now));
    }

    public async Task ForgotPasswordAsync(string emailInput, CancellationToken cancellationToken)
    {
        var startedAt = timing.GetTimestamp();
        try
        {
            var validEmail = TryNormalizeEmail(emailInput, out _, out var normalizedEmail);
            var now = timeProvider.GetUtcNow();
            var prepared = await PrepareEmailTokenAsync(
                ResetPurpose,
                ResetExpirySetting,
                DefaultResetExpiryMinutes,
                now,
                cancellationToken);
            var user = validEmail
                ? await db.Users.AsNoTracking().SingleOrDefaultAsync(
                    value => value.NormalizedEmail == normalizedEmail && !value.Disabled,
                    cancellationToken)
                : null;
            if (user is not null)
            {
                await TryIssueEmailTokenAsync(user.Id, user.Email, ResetPurpose, prepared, now, cancellationToken);
            }
        }
        finally
        {
            await timing.CompleteAsync(startedAt, cancellationToken);
        }
    }

    public async Task<AuthStatus> ResetPasswordAsync(string token, string password, CancellationToken cancellationToken)
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
        var candidate = await db.EmailTokens.AsNoTracking()
            .Where(value => value.TokenHash == tokenHash && value.Purpose == ResetPurpose)
            .Select(value => new { value.Id, value.UserId })
            .SingleOrDefaultAsync(cancellationToken);
        if (candidate is null)
        {
            return AuthStatus.InvalidToken;
        }

        var consumed = await db.EmailTokens
            .Where(value => value.Id == candidate.Id && value.UsedAtUtc == null && value.ExpiresAtUtc > now)
            .ExecuteUpdateAsync(update => update.SetProperty(value => value.UsedAtUtc, now), cancellationToken);
        if (consumed != 1)
        {
            return AuthStatus.InvalidToken;
        }

        var passwordHash = await passwords.HashAsync(password, cancellationToken);
        await db.Users.Where(user => user.Id == candidate.UserId)
            .ExecuteUpdateAsync(update => update.SetProperty(user => user.PasswordHash, passwordHash), cancellationToken);
        await db.EmailTokens
            .Where(value => value.UserId == candidate.UserId && value.Purpose == ResetPurpose && value.UsedAtUtc == null)
            .ExecuteUpdateAsync(update => update.SetProperty(value => value.UsedAtUtc, now), cancellationToken);
        await db.DeviceSessions
            .Where(session => session.UserId == candidate.UserId && session.RevokedAtUtc == null)
            .ExecuteUpdateAsync(update => update.SetProperty(session => session.RevokedAtUtc, now), cancellationToken);
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
            new("email_verified", "true"),
            new("scope", "sync"),
        };
        var jwt = new JwtSecurityToken(
            JwtOptions.Issuer,
            JwtOptions.Audience,
            claims,
            now.UtcDateTime,
            expires.UtcDateTime,
            jwtOptions.SigningCredentials);
        return new TokenResponse(
            new JwtSecurityTokenHandler().WriteToken(jwt),
            refreshToken,
            expires,
            true);
    }

    private async Task<PreparedEmailToken> PrepareEmailTokenAsync(
        string purpose,
        string expirySetting,
        int defaultExpiryMinutes,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var expiryMinutes = await ReadBoundedIntSettingAsync(
            expirySetting,
            defaultExpiryMinutes,
            1,
            10_080,
            cancellationToken);
        var cooldownSeconds = await ReadBoundedIntSettingAsync(
            TokenCooldownSetting,
            DefaultTokenCooldownSeconds,
            10,
            3_600,
            cancellationToken);
        var token = tokenFactory.CreateRefreshToken();
        return new PreparedEmailToken(
            tokenFactory.HashToken(token),
            EncryptedOutboxPrefix + secretProtector.Protect($"{purpose}:{token}"),
            now.AddMinutes(expiryMinutes),
            TimeSpan.FromSeconds(cooldownSeconds));
    }

    private async Task<bool> TryIssueEmailTokenAsync(
        Guid userId,
        string email,
        string purpose,
        PreparedEmailToken prepared,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var lockKey = CreateAdvisoryLockKey(userId, purpose);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({lockKey})",
            cancellationToken);
        var newestCreatedAt = await db.EmailTokens.AsNoTracking()
            .Where(token => token.UserId == userId && token.Purpose == purpose)
            .MaxAsync(token => (DateTimeOffset?)token.CreatedAtUtc, cancellationToken);
        if (newestCreatedAt is not null && newestCreatedAt > now.Subtract(prepared.Cooldown))
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        await db.EmailTokens
            .Where(token => token.UserId == userId && token.Purpose == purpose && token.UsedAtUtc == null)
            .ExecuteUpdateAsync(update => update.SetProperty(token => token.UsedAtUtc, now), cancellationToken);
        AddPreparedEmail(userId, email, purpose, prepared, now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private void AddPreparedEmail(
        Guid userId,
        string email,
        string purpose,
        PreparedEmailToken prepared,
        DateTimeOffset now)
    {
        db.EmailTokens.Add(new EmailTokenEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = prepared.TokenHash,
            Purpose = purpose,
            CreatedAtUtc = now,
            ExpiresAtUtc = prepared.ExpiresAtUtc,
        });
        db.MailOutbox.Add(new MailOutboxEntity
        {
            RecipientEmail = email,
            Subject = purpose == VerificationPurpose ? "验证邮箱" : "重置密码",
            BodyHtml = prepared.EncryptedBody,
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });
    }

    private async Task<int> ReadBoundedIntSettingAsync(
        string key,
        int safeDefault,
        int minimum,
        int maximum,
        CancellationToken cancellationToken)
    {
        var value = await db.SystemSettings.AsNoTracking()
            .Where(setting => setting.Key == key && !setting.IsEncrypted)
            .Select(setting => setting.Value)
            .SingleOrDefaultAsync(cancellationToken);
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) &&
            parsed >= minimum && parsed <= maximum
                ? parsed
                : safeDefault;
    }

    private async Task RevokeSessionAsync(Guid sessionId, DateTimeOffset now, CancellationToken cancellationToken) =>
        _ = await db.DeviceSessions
            .Where(session => session.Id == sessionId && session.RevokedAtUtc == null)
            .ExecuteUpdateAsync(update => update.SetProperty(session => session.RevokedAtUtc, now), cancellationToken);

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

    private static long CreateAdvisoryLockKey(Guid userId, string purpose)
    {
        var purposeBytes = Encoding.UTF8.GetBytes(purpose);
        Span<byte> value = stackalloc byte[16 + purposeBytes.Length];
        userId.TryWriteBytes(value);
        purposeBytes.CopyTo(value[16..]);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(value, hash);
        return BinaryPrimitives.ReadInt64LittleEndian(hash);
    }

    private static bool HasValidPasswordLength(string? password) =>
        password is not null && password.Length is >= 10 and <= 128;

    private static bool TryNormalizeEmail(string? input, out string email, out string normalizedEmail)
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

    private sealed record PreparedEmailToken(
        string TokenHash,
        string EncryptedBody,
        DateTimeOffset ExpiresAtUtc,
        TimeSpan Cooldown);
}
