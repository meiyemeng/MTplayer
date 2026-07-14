using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using MTPlayer.Server.Settings;

namespace MTPlayer.Server.Mail;

public interface ISmtpEmailSender
{
    Task SendAsync(
        SystemSettingsSnapshot settings,
        string recipientEmail,
        string subject,
        string bodyHtml,
        CancellationToken cancellationToken);
}

public sealed class SmtpEmailSender : ISmtpEmailSender
{
    public async Task SendAsync(
        SystemSettingsSnapshot settings,
        string recipientEmail,
        string subject,
        string bodyHtml,
        CancellationToken cancellationToken)
    {
        if (!settings.MailConfigurationComplete)
        {
            throw new InvalidOperationException("SMTP 与公开地址尚未完整配置。");
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(settings.SmtpFromName, settings.SmtpFromAddress));
        message.To.Add(MailboxAddress.Parse(recipientEmail));
        message.Subject = subject;
        message.Body = new BodyBuilder { HtmlBody = bodyHtml }.ToMessageBody();

        using var client = new SmtpClient { Timeout = 30_000 };
        var socketOptions = settings.SmtpUseTls
            ? settings.SmtpPort == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls
            : SecureSocketOptions.None;
        await client.ConnectAsync(settings.SmtpHost, settings.SmtpPort, socketOptions, cancellationToken);
        await client.AuthenticateAsync(settings.SmtpUsername, settings.SmtpPassword!, cancellationToken);
        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }
}
