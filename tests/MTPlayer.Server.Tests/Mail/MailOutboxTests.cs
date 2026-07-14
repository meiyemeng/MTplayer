using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MTPlayer.Server.Data;
using MTPlayer.Server.Mail;
using MTPlayer.Server.Tests.Auth;
using Xunit;

namespace MTPlayer.Server.Tests.Mail;

public sealed class MailOutboxTests(PostgreSqlAuthFixture fixture) : IClassFixture<PostgreSqlAuthFixture>
{
    [DockerFact]
    public async Task PostgreSQL_claim_is_durable_exclusive_and_retries_stop_after_eight_attempts()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<MailOutboxService>();
        var ids = new List<long>();
        for (var index = 0; index < 12; index++)
        {
            ids.Add(await outbox.EnqueueAsync(
                $"recipient-{index}@example.com",
                "测试",
                "enc:v1:test-payload",
                CancellationToken.None));
        }

        await using var firstScope = fixture.Factory.Services.CreateAsyncScope();
        await using var secondScope = fixture.Factory.Services.CreateAsyncScope();
        var first = firstScope.ServiceProvider.GetRequiredService<MailOutboxService>();
        var second = secondScope.ServiceProvider.GetRequiredService<MailOutboxService>();
        var claims = await Task.WhenAll(
            first.ClaimBatchAsync(8, CancellationToken.None),
            second.ClaimBatchAsync(8, CancellationToken.None));
        var claimed = claims.SelectMany(batch => batch).ToArray();
        Assert.Equal(12, claimed.Length);
        Assert.Equal(12, claimed.Select(message => message.Id).Distinct().Count());

        var retry = claimed[0];
        var expectedDelays = new[] { 1, 5, 15, 60, 360, 360, 360 };
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            var failedAt = DateTimeOffset.UtcNow;
            await outbox.MarkFailedAsync(retry.Id, retry.ClaimToken, "smtp failed", CancellationToken.None);
            await using var db = fixture.CreateDbContext();
            var row = await db.MailOutbox.AsNoTracking().SingleAsync(message => message.Id == retry.Id);
            if (attempt == 8)
            {
                Assert.Equal("failed", row.Status);
                break;
            }

            Assert.InRange(
                row.NextAttemptAtUtc,
                failedAt.AddMinutes(expectedDelays[attempt - 1]),
                DateTimeOffset.UtcNow.AddMinutes(expectedDelays[attempt - 1]).AddSeconds(1));

            await db.MailOutbox.Where(message => message.Id == retry.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(message => message.NextAttemptAtUtc, DateTimeOffset.UtcNow.AddSeconds(-1)));
            retry = (await outbox.ClaimBatchAsync(1, CancellationToken.None)).Single();
        }

        await using var restartedFactory = fixture.CreateFactory(useHighRateLimits: true);
        await using var restartScope = restartedFactory.Services.CreateAsyncScope();
        var restartedDb = restartScope.ServiceProvider.GetRequiredService<ApiDbContext>();
        Assert.True(await restartedDb.MailOutbox.AnyAsync(message => ids.Contains(message.Id)));
    }

    [Fact]
    public void Template_renderer_allows_only_documented_tokens_and_encodes_user_values()
    {
        var rendered = MailTemplateRenderer.Render(
            "<p>{email}</p><a href=\"{verificationUrl}\">验证</a><p>{expiresMinutes}</p>",
            new MailTemplateValues(
                "a<script>@example.com",
                "https://example.com/verify?token=<bad>",
                null,
                60));
        Assert.DoesNotContain("<script>", rendered, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", rendered, StringComparison.Ordinal);
        Assert.Contains("&lt;bad&gt;", rendered, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => MailTemplateRenderer.Validate("{unknown}"));
        Assert.Throws<ArgumentException>(() => MailTemplateRenderer.Validate("{email"));
    }

    [DockerFact]
    public async Task Dispatcher_decrypts_Task4_payload_applies_template_and_marks_sent()
    {
        await using (var cleanup = fixture.CreateDbContext())
        {
            await cleanup.MailOutbox.ExecuteDeleteAsync();
            await cleanup.SystemSettings.ExecuteDeleteAsync();
        }

        var sender = new CapturingSmtpSender();
        await using var factory = fixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Mail:WorkerEnabled", "false");
            builder.ConfigureTestServices(services => services.AddSingleton<ISmtpEmailSender>(sender));
        });
        await using var scope = factory.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<MTPlayer.Server.Settings.SystemSettingsService>();
        await settings.UpdateAsync(new MTPlayer.Server.Settings.AdminSettingsUpdate
        {
            PublicBaseUrl = "https://mail.example.com",
            SmtpHost = "smtp.example.com",
            SmtpPort = 587,
            SmtpUsername = "mailer@example.com",
            NewSmtpPassword = "smtp-secret",
            SmtpFromName = "MT播放器",
            SmtpFromAddress = "mailer@example.com",
            SmtpUseTls = true,
            RegistrationEnabled = true,
            RequireVerifiedEmail = true,
            PasswordResetEnabled = true,
            EmailVerificationTokenExpiryMinutes = 60,
            PasswordResetTokenExpiryMinutes = 30,
            VerificationSubjectTemplate = "验证 {email}",
            VerificationBodyTemplate = "<a href=\"{verificationUrl}\">{email}</a><p>{expiresMinutes}</p>",
            ResetSubjectTemplate = "重置 {email}",
            ResetBodyTemplate = "<a href=\"{resetUrl}\">{email}</a><p>{expiresMinutes}</p>",
            TestSubjectTemplate = "测试 {email}",
            TestBodyTemplate = "<p>{email}</p>",
        }, CancellationToken.None);

        var outbox = scope.ServiceProvider.GetRequiredService<MailOutboxService>();
        var token = Convert.ToBase64String(Enumerable.Range(0, 32).Select(index => (byte)index).ToArray());
        var id = await outbox.EnqueueProtectedAsync("recipient@example.com", $"verify:{token}", CancellationToken.None);
        var dispatcher = scope.ServiceProvider.GetRequiredService<MailOutboxDispatcher>();
        Assert.Equal(1, await dispatcher.DispatchBatchAsync(CancellationToken.None));
        var delivered = Assert.Single(sender.Messages);
        Assert.Equal("smtp-secret", delivered.Settings.SmtpPassword);
        Assert.Contains("https://mail.example.com/verify-email?token=", delivered.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("recipient@example.com", delivered.BodyHtml, StringComparison.Ordinal);
        await using var db = fixture.CreateDbContext();
        var stored = await db.MailOutbox.SingleAsync(message => message.Id == id);
        Assert.Equal("sent", stored.Status);
        Assert.NotNull(stored.SentAtUtc);
    }

    private sealed class CapturingSmtpSender : ISmtpEmailSender
    {
        public List<DeliveredMail> Messages { get; } = [];

        public Task SendAsync(
            MTPlayer.Server.Settings.SystemSettingsSnapshot settings,
            string recipientEmail,
            string subject,
            string bodyHtml,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Messages.Add(new DeliveredMail(settings, recipientEmail, subject, bodyHtml));
            return Task.CompletedTask;
        }
    }

    private sealed record DeliveredMail(
        MTPlayer.Server.Settings.SystemSettingsSnapshot Settings,
        string RecipientEmail,
        string Subject,
        string BodyHtml);
}
