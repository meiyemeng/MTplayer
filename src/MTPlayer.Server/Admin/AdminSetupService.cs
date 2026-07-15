using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MTPlayer.Server.Auth;
using MTPlayer.Server.Data;

namespace MTPlayer.Server.Admin;

public enum AdminSetupStatus
{
    Success,
    NotFound,
    InvalidToken,
    InvalidInput,
    DuplicateEmail,
}

public sealed record AdminSetupResult(AdminSetupStatus Status, string? Email = null);

public sealed class AdminSetupService(
    IDbContextFactory<ApiDbContext> dbContextFactory,
    Argon2PasswordService passwords,
    IConfiguration configuration,
    TimeProvider timeProvider)
{
    private const int MaximumSetupTokenLength = 4_096;
    public const string CompletedSettingKey = "AdminSetupCompleted";
    private const long SetupAdvisoryLock = 7_095_419_832_766_712_381;
    private readonly string _configuredToken = configuration["ADMIN_SETUP_TOKEN"] ?? string.Empty;

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
        => !await IsCompletedAsync(cancellationToken) && !string.IsNullOrEmpty(_configuredToken);

    public async Task<bool> IsCompletedAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.SystemSettings.AsNoTracking().AnyAsync(
            setting => setting.Key == CompletedSettingKey && setting.Value == "true",
            cancellationToken);
    }

    public async Task<AdminSetupResult> CreateAsync(
        string? suppliedToken,
        string? emailInput,
        string? password,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({SetupAdvisoryLock})",
            cancellationToken);
        if (await db.SystemSettings.AnyAsync(
                setting => setting.Key == CompletedSettingKey && setting.Value == "true",
                cancellationToken))
        {
            return new AdminSetupResult(AdminSetupStatus.NotFound);
        }

        if (!FixedTimeTokenEquals(_configuredToken, suppliedToken ?? string.Empty))
        {
            return new AdminSetupResult(AdminSetupStatus.InvalidToken);
        }

        if (!TryNormalizeEmail(emailInput, out var email, out var normalizedEmail) ||
            password is null || password.Length is < 10 or > 128)
        {
            return new AdminSetupResult(AdminSetupStatus.InvalidInput);
        }

        if (await db.Users.AnyAsync(user => user.NormalizedEmail == normalizedEmail, cancellationToken))
        {
            return new AdminSetupResult(AdminSetupStatus.DuplicateEmail);
        }

        var now = timeProvider.GetUtcNow();
        db.Users.Add(new UserEntity
        {
            Id = Guid.NewGuid(),
            Email = email,
            NormalizedEmail = normalizedEmail,
            PasswordHash = await passwords.HashAsync(password, cancellationToken),
            EmailVerified = true,
            Role = "admin",
            CreatedAtUtc = now,
        });
        db.SystemSettings.Add(new SystemSettingEntity
        {
            Key = CompletedSettingKey,
            Value = "true",
            IsEncrypted = false,
            UpdatedAtUtc = now,
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AdminSetupResult(AdminSetupStatus.Success, email);
    }

    private static bool FixedTimeTokenEquals(string expected, string supplied)
    {
        var expectedLengthValid = expected.Length is > 0 and <= MaximumSetupTokenLength;
        var suppliedLengthValid = supplied.Length <= MaximumSetupTokenLength;
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expectedLengthValid ? expected : string.Empty));
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(suppliedLengthValid ? supplied : string.Empty));
        try
        {
            return expectedLengthValid &&
                suppliedLengthValid &&
                CryptographicOperations.FixedTimeEquals(expectedHash, suppliedHash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedHash);
            CryptographicOperations.ZeroMemory(suppliedHash);
        }
    }

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
}
