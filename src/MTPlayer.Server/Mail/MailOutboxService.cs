using Microsoft.EntityFrameworkCore;
using System.Net.Mail;
using MTPlayer.Server.Data;
using MTPlayer.Server.Security;

namespace MTPlayer.Server.Mail;

public sealed record ClaimedMail(
    long Id,
    Guid ClaimToken,
    string RecipientEmail,
    string Subject,
    string BodyHtml,
    int AttemptCount);

public sealed class MailOutboxService(
    IDbContextFactory<ApiDbContext> dbContextFactory,
    ISecretProtector secretProtector,
    TimeProvider timeProvider)
{
    public const string EncryptedPayloadPrefix = "enc:v1:";
    public const int MaximumAttempts = 8;
    private static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(60),
        TimeSpan.FromMinutes(360),
    ];

    public async Task<long> EnqueueAsync(
        string recipientEmail,
        string subject,
        string bodyHtml,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientEmail);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(bodyHtml);
        var normalizedRecipient = recipientEmail.Trim();
        if (normalizedRecipient.Length > 320 ||
            !MailAddress.TryCreate(normalizedRecipient, out var address) ||
            !string.Equals(address.Address, normalizedRecipient, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Recipient email is invalid.", nameof(recipientEmail));
        }

        if (subject.Length > 500 || subject.Contains('\r', StringComparison.Ordinal) || subject.Contains('\n', StringComparison.Ordinal))
        {
            throw new ArgumentException("Subject is invalid.", nameof(subject));
        }

        var now = timeProvider.GetUtcNow();
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var message = new MailOutboxEntity
        {
            RecipientEmail = normalizedRecipient,
            Subject = subject,
            BodyHtml = bodyHtml,
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        };
        db.MailOutbox.Add(message);
        await db.SaveChangesAsync(cancellationToken);
        return message.Id;
    }

    public Task<long> EnqueueProtectedAsync(
        string recipientEmail,
        string purposePayload,
        CancellationToken cancellationToken) =>
        EnqueueAsync(
            recipientEmail,
            purposePayload.StartsWith("verify:", StringComparison.Ordinal) ? "验证邮箱" :
                purposePayload.StartsWith("reset:", StringComparison.Ordinal) ? "重置密码" : "SMTP 测试",
            EncryptedPayloadPrefix + secretProtector.Protect(purposePayload),
            cancellationToken);

    public async Task<IReadOnlyList<ClaimedMail>> ClaimBatchAsync(
        int batchSize,
        CancellationToken cancellationToken)
    {
        if (batchSize is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        var now = timeProvider.GetUtcNow();
        var staleBefore = now.Subtract(ClaimLease);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var rows = await db.MailOutbox
            .FromSqlInterpolated($$"""
                SELECT * FROM mail_outbox
                WHERE "AttemptCount" < {{MaximumAttempts}}
                  AND (
                    ("Status" = 'pending' AND "NextAttemptAtUtc" <= {{now}})
                    OR ("Status" = 'processing' AND "ClaimedAtUtc" < {{staleBefore}})
                  )
                ORDER BY "NextAttemptAtUtc", "Id"
                FOR UPDATE SKIP LOCKED
                LIMIT {{batchSize}}
                """)
            .ToListAsync(cancellationToken);
        var claimed = new List<ClaimedMail>(rows.Count);
        foreach (var row in rows)
        {
            row.Status = "processing";
            row.ClaimedAtUtc = now;
            row.ClaimToken = Guid.NewGuid();
            row.AttemptCount++;
            claimed.Add(new ClaimedMail(
                row.Id,
                row.ClaimToken.Value,
                row.RecipientEmail,
                row.Subject,
                row.BodyHtml,
                row.AttemptCount));
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return claimed;
    }

    public async Task<bool> MarkSentAsync(
        long id,
        Guid claimToken,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.MailOutbox
            .Where(message =>
                message.Id == id &&
                message.Status == "processing" &&
                message.ClaimToken == claimToken)
            .ExecuteUpdateAsync(update => update
                .SetProperty(message => message.Status, "sent")
                .SetProperty(message => message.SentAtUtc, now)
                .SetProperty(message => message.ClaimedAtUtc, (DateTimeOffset?)null)
                .SetProperty(message => message.ClaimToken, (Guid?)null)
                .SetProperty(message => message.LastError, (string?)null), cancellationToken) == 1;
    }

    public async Task<bool> MarkFailedAsync(
        long id,
        Guid claimToken,
        string error,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var row = await db.MailOutbox
            .FromSqlInterpolated($"SELECT * FROM mail_outbox WHERE \"Id\" = {id} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null || row.Status != "processing" || row.ClaimToken != claimToken)
        {
            return false;
        }

        row.LastError = SanitizeError(error);
        row.ClaimedAtUtc = null;
        row.ClaimToken = null;
        if (row.AttemptCount >= MaximumAttempts)
        {
            row.Status = "failed";
        }
        else
        {
            row.Status = "pending";
            var delayIndex = Math.Min(row.AttemptCount - 1, RetryDelays.Length - 1);
            row.NextAttemptAtUtc = now.Add(RetryDelays[delayIndex]);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public string UnprotectPayload(string bodyHtml)
    {
        if (!bodyHtml.StartsWith(EncryptedPayloadPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Mail outbox payload is not encrypted.");
        }

        return secretProtector.Unprotect(bodyHtml[EncryptedPayloadPrefix.Length..]);
    }

    private static string SanitizeError(string error)
    {
        var normalized = string.IsNullOrWhiteSpace(error) ? "SMTP 发送失败。" : error.Replace('\r', ' ').Replace('\n', ' ');
        return normalized.Length <= 2_000 ? normalized : normalized[..2_000];
    }
}
