using MTPlayer.Server.Settings;

namespace MTPlayer.Server.Mail;

public sealed class MailOutboxWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<MailOutboxWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception> LogWorkerError = LoggerMessage.Define(
        LogLevel.Error,
        new EventId(1, "MailOutboxWorkerError"),
        "邮件发件箱后台任务发生错误。");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<MailOutboxDispatcher>();
                var sentOrFailed = await dispatcher.DispatchBatchAsync(stoppingToken);
                await Task.Delay(sentOrFailed == 0 ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(250), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogWorkerError(logger, exception);
                await Task.Delay(TimeSpan.FromSeconds(10), timeProvider, stoppingToken);
            }
        }
    }
}

public sealed class MailOutboxDispatcher(
    MailOutboxService outbox,
    SystemSettingsService settingsService,
    ISmtpEmailSender sender)
{
    // SMTP operations are sequential and may each consume several socket timeouts.
    // Claim one row at a time so no queued row can outlive the ten-minute lease
    // while waiting behind other messages in this worker.
    internal const int DispatchClaimSize = 1;

    public async Task<int> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsService.GetSnapshotAsync(cancellationToken);
        if (!settings.MailConfigurationComplete)
        {
            return 0;
        }

        var claimed = await outbox.ClaimBatchAsync(DispatchClaimSize, cancellationToken);
        foreach (var message in claimed)
        {
            try
            {
                var rendered = Render(message, settings);
                await sender.SendAsync(
                    settings,
                    message.RecipientEmail,
                    rendered.Subject,
                    rendered.Body,
                    cancellationToken);
                await outbox.MarkSentAsync(message.Id, message.ClaimToken, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                await outbox.MarkFailedAsync(
                    message.Id,
                    message.ClaimToken,
                    RedactError(exception, message, settings),
                    cancellationToken);
            }
        }

        return claimed.Count;
    }

    private RenderedMail Render(ClaimedMail message, SystemSettingsSnapshot settings)
    {
        var payload = outbox.UnprotectPayload(message.BodyHtml);
        var separator = payload.IndexOf(':', StringComparison.Ordinal);
        if (separator < 0)
        {
            throw new InvalidOperationException("邮件发件箱负载格式无效。");
        }

        var purpose = payload[..separator];
        var token = payload[(separator + 1)..];
        return purpose switch
        {
            "verify" => RenderVerification(message.RecipientEmail, token, settings),
            "reset" => RenderReset(message.RecipientEmail, token, settings),
            "test" when token.Length == 0 => RenderTest(message.RecipientEmail, settings),
            _ => throw new InvalidOperationException("邮件发件箱用途无效。"),
        };
    }

    private static RenderedMail RenderVerification(
        string email,
        string token,
        SystemSettingsSnapshot settings)
    {
        var url = BuildUrl(settings.PublicBaseUrl!, "verify-email", token);
        var values = new MailTemplateValues(email, url, null, settings.EmailVerificationTokenExpiryMinutes);
        return new RenderedMail(
            RenderSubject(settings.VerificationSubjectTemplate, values, MailTemplateRenderer.VerificationTokens),
            MailTemplateRenderer.Render(settings.VerificationBodyTemplate, values, MailTemplateRenderer.VerificationTokens));
    }

    private static RenderedMail RenderReset(string email, string token, SystemSettingsSnapshot settings)
    {
        var url = BuildUrl(settings.PublicBaseUrl!, "reset-password", token);
        var values = new MailTemplateValues(email, null, url, settings.PasswordResetTokenExpiryMinutes);
        return new RenderedMail(
            RenderSubject(settings.ResetSubjectTemplate, values, MailTemplateRenderer.ResetTokens),
            MailTemplateRenderer.Render(settings.ResetBodyTemplate, values, MailTemplateRenderer.ResetTokens));
    }

    private static RenderedMail RenderTest(string email, SystemSettingsSnapshot settings)
    {
        var values = new MailTemplateValues(email, null, null, 0);
        return new RenderedMail(
            RenderSubject(settings.TestSubjectTemplate, values, MailTemplateRenderer.TestTokens),
            MailTemplateRenderer.Render(settings.TestBodyTemplate, values, MailTemplateRenderer.TestTokens));
    }

    private static string RenderSubject(
        string template,
        MailTemplateValues values,
        IReadOnlySet<string> allowedTokens) =>
        MailTemplateRenderer.RenderPlainText(template, values, allowedTokens);

    private static string RedactError(
        Exception exception,
        ClaimedMail message,
        SystemSettingsSnapshot settings)
    {
        var redacted = exception.Message;
        foreach (var sensitive in new[]
        {
            settings.SmtpPassword,
            settings.SmtpUsername,
            settings.PublicBaseUrl,
            message.RecipientEmail,
        })
        {
            if (!string.IsNullOrEmpty(sensitive))
            {
                redacted = redacted.Replace(sensitive, "[已隐藏]", StringComparison.OrdinalIgnoreCase);
            }
        }

        return $"{exception.GetType().Name}: {redacted}";
    }

    private static string BuildUrl(string publicBaseUrl, string path, string token) =>
        $"{publicBaseUrl}/{path}?token={Uri.EscapeDataString(token)}";

    private sealed record RenderedMail(string Subject, string Body);
}
