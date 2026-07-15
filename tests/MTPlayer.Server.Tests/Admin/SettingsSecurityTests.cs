using System.Net;
using MTPlayer.Server.Mail;
using MTPlayer.Server.Settings;
using Xunit;

namespace MTPlayer.Server.Tests.Admin;

public sealed class SettingsSecurityTests
{
    [Fact]
    public void Settings_string_representations_never_include_sensitive_values()
    {
        const string publicUrl = "https://private-control.example";
        const string smtpPassword = "smtp-secret-value";
        var update = new AdminSettingsUpdate
        {
            PublicBaseUrl = publicUrl,
            NewSmtpPassword = smtpPassword,
        };
        var snapshot = new SystemSettingsSnapshot(
            PublicBaseUrl: publicUrl,
            SmtpHost: "smtp.example.com",
            SmtpPort: 587,
            SmtpUsername: "mailer@example.com",
            SmtpPassword: smtpPassword,
            SmtpFromName: "MT播放器",
            SmtpFromAddress: "mailer@example.com",
            SmtpUseTls: true,
            RegistrationEnabled: true,
            RequireVerifiedEmail: true,
            PasswordResetEnabled: true,
            EmailVerificationTokenExpiryMinutes: 60,
            PasswordResetTokenExpiryMinutes: 30,
            VerificationSubjectTemplate: "验证",
            VerificationBodyTemplate: "{verificationUrl}",
            ResetSubjectTemplate: "重置",
            ResetBodyTemplate: "{resetUrl}",
            TestSubjectTemplate: "测试",
            TestBodyTemplate: "{email}");

        Assert.DoesNotContain(publicUrl, update.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(smtpPassword, update.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(publicUrl, snapshot.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(smtpPassword, snapshot.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("100.64.0.1")]
    [InlineData("169.254.1.1")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("::1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("2001:db8::1")]
    public void Public_base_url_probe_rejects_non_public_addresses(string value) =>
        Assert.False(PublicBaseUrlProbe.IsPublicAddress(IPAddress.Parse(value)));

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("2606:4700:4700::1111")]
    public void Public_base_url_probe_accepts_public_addresses(string value) =>
        Assert.True(PublicBaseUrlProbe.IsPublicAddress(IPAddress.Parse(value)));

    [Theory]
    [InlineData("https://example.com:8443")]
    [InlineData("http://example.com")]
    [InlineData("https://example.com/path")]
    public void Public_base_url_requires_default_https_origin(string value) =>
        Assert.False(SystemSettingsService.TryNormalizePublicBaseUrl(value, out _));

    [Fact]
    public void Worker_claims_only_one_message_per_sequential_dispatch() =>
        Assert.Equal(1, MailOutboxDispatcher.DispatchClaimSize);
}
