using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using MTPlayer.Server.Data;
using MTPlayer.Server.Security;

namespace MTPlayer.Server.Maintenance;

public sealed class KeyRotationService(
    IDbContextFactory<ApiDbContext> contextFactory,
    ISecretProtector currentProtector,
    TimeProvider timeProvider)
{
    private const long RotationAdvisoryLock = 4_873_942_019;

    public async Task RotateAsync(string newEncodedKey, CancellationToken cancellationToken = default)
    {
        AesGcmSecretProtector.ValidateKey(newEncodedKey);
        using var replacementProtector = new AesGcmSecretProtector(newEncodedKey);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({RotationAdvisoryLock})",
            cancellationToken);
        var encryptedSettings = await db.SystemSettings
            .FromSqlRaw("SELECT * FROM system_settings WHERE \"IsEncrypted\" ORDER BY \"Key\" FOR UPDATE")
            .ToListAsync(cancellationToken);
        var plaintext = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var setting in encryptedSettings)
        {
            if (setting.Value is not null)
            {
                plaintext[setting.Key] = currentProtector.Unprotect(setting.Value);
            }
        }

        var now = timeProvider.GetUtcNow();
        foreach (var setting in encryptedSettings)
        {
            if (setting.Value is null)
            {
                continue;
            }

            var expected = plaintext[setting.Key];
            var replacement = replacementProtector.Protect(expected);
            if (!string.Equals(replacementProtector.Unprotect(replacement), expected, StringComparison.Ordinal))
            {
                throw new CryptographicException("Replacement key verification failed.");
            }

            setting.Value = replacement;
            setting.UpdatedAtUtc = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
        var storedReplacement = await db.SystemSettings.AsNoTracking()
            .Where(setting => plaintext.Keys.Contains(setting.Key))
            .ToDictionaryAsync(setting => setting.Key, StringComparer.Ordinal, cancellationToken);
        foreach (var item in plaintext)
        {
            if (!storedReplacement.TryGetValue(item.Key, out var setting) ||
                setting.Value is null ||
                !string.Equals(replacementProtector.Unprotect(setting.Value), item.Value, StringComparison.Ordinal))
            {
                throw new CryptographicException("Stored replacement ciphertext verification failed.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public static string CreateKey()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            return Convert.ToBase64String(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
