using System.Globalization;
using System.Net.Mail;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using MTPlayer.Server.Data;
using MTPlayer.Server.Mail;
using MTPlayer.Server.Security;

namespace MTPlayer.Server.Settings;

public sealed record AdminSettingsUpdate
{
    public string? PublicBaseUrl { get; init; }
    public string? SmtpHost { get; init; }
    public int SmtpPort { get; init; } = 587;
    public string? SmtpUsername { get; init; }
    public string? NewSmtpPassword { get; init; }
    public string? SmtpFromName { get; init; }
    public string? SmtpFromAddress { get; init; }
    public bool SmtpUseTls { get; init; } = true;
    public bool RegistrationEnabled { get; init; } = true;
    public bool RequireVerifiedEmail { get; init; } = true;
    public bool PasswordResetEnabled { get; init; } = true;
    public int EmailVerificationTokenExpiryMinutes { get; init; } = 60;
    public int PasswordResetTokenExpiryMinutes { get; init; } = 30;
    public string? VerificationSubjectTemplate { get; init; }
    public string? VerificationBodyTemplate { get; init; }
    public string? ResetSubjectTemplate { get; init; }
    public string? ResetBodyTemplate { get; init; }
    public string? TestSubjectTemplate { get; init; }
    public string? TestBodyTemplate { get; init; }
    public bool ClearPublicBaseUrl { get; init; }
    public bool ClearSmtpPassword { get; init; }

    public override string ToString() =>
        $"{nameof(AdminSettingsUpdate)} {{ PublicBaseUrl = [已隐藏], NewSmtpPassword = [已隐藏] }}";
}

public sealed record AdminSettingsView(
    bool PublicBaseUrlConfigured,
    string SmtpHost,
    int SmtpPort,
    string SmtpUsername,
    bool SmtpPasswordConfigured,
    string SmtpFromName,
    string SmtpFromAddress,
    bool SmtpUseTls,
    bool RegistrationEnabled,
    bool RequireVerifiedEmail,
    bool PasswordResetEnabled,
    int EmailVerificationTokenExpiryMinutes,
    int PasswordResetTokenExpiryMinutes,
    string VerificationSubjectTemplate,
    string VerificationBodyTemplate,
    string ResetSubjectTemplate,
    string ResetBodyTemplate,
    string TestSubjectTemplate,
    string TestBodyTemplate,
    bool MailConfigurationComplete);

public sealed record SystemSettingsSnapshot(
    string? PublicBaseUrl,
    string SmtpHost,
    int SmtpPort,
    string SmtpUsername,
    string? SmtpPassword,
    string SmtpFromName,
    string SmtpFromAddress,
    bool SmtpUseTls,
    bool RegistrationEnabled,
    bool RequireVerifiedEmail,
    bool PasswordResetEnabled,
    int EmailVerificationTokenExpiryMinutes,
    int PasswordResetTokenExpiryMinutes,
    string VerificationSubjectTemplate,
    string VerificationBodyTemplate,
    string ResetSubjectTemplate,
    string ResetBodyTemplate,
    string TestSubjectTemplate,
    string TestBodyTemplate)
{
    public bool MailConfigurationComplete =>
        PublicBaseUrl is not null &&
        !string.IsNullOrWhiteSpace(SmtpHost) &&
        SmtpPort is >= 1 and <= 65_535 &&
        !string.IsNullOrWhiteSpace(SmtpUsername) &&
        !string.IsNullOrWhiteSpace(SmtpPassword) &&
        !string.IsNullOrWhiteSpace(SmtpFromName) &&
        MailAddress.TryCreate(SmtpFromAddress, out _);

    public override string ToString() =>
        $"{nameof(SystemSettingsSnapshot)} {{ PublicBaseUrl = [已隐藏], SmtpPassword = [已隐藏], MailConfigurationComplete = {MailConfigurationComplete} }}";
}

public sealed class SettingsValidationException(string message) : ArgumentException(message);

public sealed class SystemSettingsService(
    IDbContextFactory<ApiDbContext> dbContextFactory,
    ISecretProtector secretProtector,
    IPublicBaseUrlProbe publicBaseUrlProbe,
    TimeProvider timeProvider)
{
    public const string PublicBaseUrlKey = "PublicBaseUrl";
    public const string SmtpPasswordKey = "SmtpPassword";
    private const long SettingsAdvisoryLock = 5_565_341_580_466_121_733;
    private const string DefaultVerificationSubject = "验证邮箱";
    private const string DefaultVerificationBody = "<p>您好，{email}。</p><p><a href=\"{verificationUrl}\">验证邮箱</a></p><p>链接将在 {expiresMinutes} 分钟后失效。</p>";
    private const string DefaultResetSubject = "重置密码";
    private const string DefaultResetBody = "<p>您好，{email}。</p><p><a href=\"{resetUrl}\">重置密码</a></p><p>链接将在 {expiresMinutes} 分钟后失效。</p>";
    private const string DefaultTestSubject = "MT播放器 SMTP 测试";
    private const string DefaultTestBody = "<p>这是一封发送给 {email} 的 SMTP 测试邮件。</p>";

    public async Task<AdminSettingsView> GetAdminViewAsync(CancellationToken cancellationToken)
    {
        var settings = await GetSnapshotAsync(cancellationToken);
        return new AdminSettingsView(
            settings.PublicBaseUrl is not null,
            settings.SmtpHost,
            settings.SmtpPort,
            settings.SmtpUsername,
            settings.SmtpPassword is not null,
            settings.SmtpFromName,
            settings.SmtpFromAddress,
            settings.SmtpUseTls,
            settings.RegistrationEnabled,
            settings.RequireVerifiedEmail,
            settings.PasswordResetEnabled,
            settings.EmailVerificationTokenExpiryMinutes,
            settings.PasswordResetTokenExpiryMinutes,
            settings.VerificationSubjectTemplate,
            settings.VerificationBodyTemplate,
            settings.ResetSubjectTemplate,
            settings.ResetBodyTemplate,
            settings.TestSubjectTemplate,
            settings.TestBodyTemplate,
            settings.MailConfigurationComplete);
    }

    public async Task<SystemSettingsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var values = await db.SystemSettings.AsNoTracking().ToDictionaryAsync(
            setting => setting.Key,
            setting => setting,
            StringComparer.Ordinal,
            cancellationToken);
        return ReadSnapshot(values);
    }

    public async Task<string?> GetPublicBaseUrlAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var setting = await db.SystemSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Key == PublicBaseUrlKey, cancellationToken);
        if (setting is null || string.IsNullOrEmpty(setting.Value))
        {
            return null;
        }

        if (!setting.IsEncrypted)
        {
            throw new InvalidOperationException($"Sensitive setting '{PublicBaseUrlKey}' is not encrypted.");
        }

        return secretProtector.Unprotect(setting.Value);
    }

    public async Task UpdateAsync(AdminSettingsUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        var normalized = ValidateAndNormalize(update);
        if (normalized.PublicBaseUrl is not null)
        {
            try
            {
                await publicBaseUrlProbe.EnsureReachableAsync(
                    new Uri(normalized.PublicBaseUrl, UriKind.Absolute),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is HttpRequestException or IOException or SocketException or TaskCanceledException or InvalidOperationException)
            {
                throw new SettingsValidationException("公开地址无法通过安全的 HTTPS 连通性检查。");
            }
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({SettingsAdvisoryLock})",
            cancellationToken);
        var stored = await db.SystemSettings.ToDictionaryAsync(
            setting => setting.Key,
            StringComparer.Ordinal,
            cancellationToken);
        var current = ReadSnapshot(stored);
        var now = timeProvider.GetUtcNow();

        var publicBaseUrl = update.ClearPublicBaseUrl
            ? null
            : normalized.PublicBaseUrl ?? current.PublicBaseUrl;
        var smtpPassword = update.ClearSmtpPassword
            ? null
            : normalized.NewSmtpPassword ?? current.SmtpPassword;

        Set(stored, db, PublicBaseUrlKey, ProtectNullable(publicBaseUrl), true, now);
        Set(stored, db, SmtpPasswordKey, ProtectNullable(smtpPassword), true, now);
        Set(stored, db, "SmtpHost", normalized.SmtpHost, false, now);
        Set(stored, db, "SmtpPort", normalized.SmtpPort.ToString(CultureInfo.InvariantCulture), false, now);
        Set(stored, db, "SmtpUsername", normalized.SmtpUsername, false, now);
        Set(stored, db, "SmtpFromName", normalized.SmtpFromName, false, now);
        Set(stored, db, "SmtpFromAddress", normalized.SmtpFromAddress, false, now);
        Set(stored, db, "SmtpUseTls", normalized.SmtpUseTls.ToString(CultureInfo.InvariantCulture), false, now);
        Set(stored, db, "RegistrationEnabled", normalized.RegistrationEnabled.ToString(CultureInfo.InvariantCulture), false, now);
        Set(stored, db, "RequireVerifiedEmail", normalized.RequireVerifiedEmail.ToString(CultureInfo.InvariantCulture), false, now);
        Set(stored, db, "PasswordResetEnabled", normalized.PasswordResetEnabled.ToString(CultureInfo.InvariantCulture), false, now);
        Set(stored, db, "EmailVerificationTokenExpiryMinutes", normalized.EmailVerificationTokenExpiryMinutes.ToString(CultureInfo.InvariantCulture), false, now);
        Set(stored, db, "PasswordResetTokenExpiryMinutes", normalized.PasswordResetTokenExpiryMinutes.ToString(CultureInfo.InvariantCulture), false, now);
        Set(stored, db, "VerificationSubjectTemplate", normalized.VerificationSubjectTemplate, false, now);
        Set(stored, db, "VerificationBodyTemplate", normalized.VerificationBodyTemplate, false, now);
        Set(stored, db, "ResetSubjectTemplate", normalized.ResetSubjectTemplate, false, now);
        Set(stored, db, "ResetBodyTemplate", normalized.ResetBodyTemplate, false, now);
        Set(stored, db, "TestSubjectTemplate", normalized.TestSubjectTemplate, false, now);
        Set(stored, db, "TestBodyTemplate", normalized.TestBodyTemplate, false, now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public static bool TryNormalizePublicBaseUrl(string? input, out string? normalized)
    {
        normalized = null;
        var value = input?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort ||
            string.IsNullOrEmpty(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.AbsolutePath != "/" ||
            value.Contains('?', StringComparison.Ordinal) ||
            value.Contains('#', StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        normalized = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }

    private SystemSettingsSnapshot ReadSnapshot(IReadOnlyDictionary<string, SystemSettingEntity> values) =>
        new(
            ReadEncrypted(values, PublicBaseUrlKey),
            Read(values, "SmtpHost", string.Empty),
            ReadInt(values, "SmtpPort", 587, 1, 65_535),
            Read(values, "SmtpUsername", string.Empty),
            ReadEncrypted(values, SmtpPasswordKey),
            Read(values, "SmtpFromName", string.Empty),
            Read(values, "SmtpFromAddress", string.Empty),
            ReadBool(values, "SmtpUseTls", true),
            ReadBool(values, "RegistrationEnabled", true),
            ReadBool(values, "RequireVerifiedEmail", true),
            ReadBool(values, "PasswordResetEnabled", true),
            ReadInt(values, "EmailVerificationTokenExpiryMinutes", 60, 1, 10_080),
            ReadInt(values, "PasswordResetTokenExpiryMinutes", 30, 1, 10_080),
            Read(values, "VerificationSubjectTemplate", DefaultVerificationSubject),
            Read(values, "VerificationBodyTemplate", DefaultVerificationBody),
            Read(values, "ResetSubjectTemplate", DefaultResetSubject),
            Read(values, "ResetBodyTemplate", DefaultResetBody),
            Read(values, "TestSubjectTemplate", DefaultTestSubject),
            Read(values, "TestBodyTemplate", DefaultTestBody));

    private static NormalizedSettings ValidateAndNormalize(AdminSettingsUpdate update)
    {
        if (!TryNormalizePublicBaseUrl(update.PublicBaseUrl, out var publicBaseUrl))
        {
            throw new SettingsValidationException("公开地址必须是无路径、查询参数或片段的绝对 HTTPS 地址。");
        }

        var smtpHost = NormalizeOptional(update.SmtpHost, 255, "SMTP 主机") ?? string.Empty;
        var smtpUsername = NormalizeOptional(update.SmtpUsername, 320, "SMTP 用户名") ?? string.Empty;
        var smtpFromName = NormalizeOptional(update.SmtpFromName, 200, "发件人名称") ?? string.Empty;
        var smtpFromAddress = NormalizeOptional(update.SmtpFromAddress, 320, "发件邮箱") ?? string.Empty;
        if (update.SmtpPort is < 1 or > 65_535 ||
            (smtpFromAddress.Length > 0 && !MailAddress.TryCreate(smtpFromAddress, out _)))
        {
            throw new SettingsValidationException("SMTP 端口或发件邮箱格式无效。");
        }

        var newPassword = NormalizeOptional(update.NewSmtpPassword, 1_024, "SMTP 密码");
        if (update.EmailVerificationTokenExpiryMinutes is < 1 or > 10_080 ||
            update.PasswordResetTokenExpiryMinutes is < 1 or > 10_080)
        {
            throw new SettingsValidationException("邮件令牌有效时间必须在 1 到 10080 分钟之间。");
        }

        var verificationSubject = ValidateTemplate(update.VerificationSubjectTemplate, 500, "验证邮件标题", false, MailTemplateRenderer.VerificationTokens);
        var verificationBody = ValidateTemplate(update.VerificationBodyTemplate, 100_000, "验证邮件正文", true, MailTemplateRenderer.VerificationTokens);
        var resetSubject = ValidateTemplate(update.ResetSubjectTemplate, 500, "重置邮件标题", false, MailTemplateRenderer.ResetTokens);
        var resetBody = ValidateTemplate(update.ResetBodyTemplate, 100_000, "重置邮件正文", true, MailTemplateRenderer.ResetTokens);
        var testSubject = ValidateTemplate(update.TestSubjectTemplate, 500, "测试邮件标题", false, MailTemplateRenderer.TestTokens);
        var testBody = ValidateTemplate(update.TestBodyTemplate, 100_000, "测试邮件正文", true, MailTemplateRenderer.TestTokens);
        return new NormalizedSettings(
            publicBaseUrl,
            smtpHost,
            update.SmtpPort,
            smtpUsername,
            newPassword,
            smtpFromName,
            smtpFromAddress,
            update.SmtpUseTls,
            update.RegistrationEnabled,
            update.RequireVerifiedEmail,
            update.PasswordResetEnabled,
            update.EmailVerificationTokenExpiryMinutes,
            update.PasswordResetTokenExpiryMinutes,
            verificationSubject,
            verificationBody,
            resetSubject,
            resetBody,
            testSubject,
            testBody);
    }

    private static string ValidateTemplate(
        string? value,
        int maximumLength,
        string name,
        bool html,
        IReadOnlySet<string> allowedTokens)
    {
        var normalized = NormalizeRequired(value, maximumLength, name);
        if (!html && (normalized.Contains('\r', StringComparison.Ordinal) || normalized.Contains('\n', StringComparison.Ordinal)))
        {
            throw new SettingsValidationException($"{name}不能包含换行符。");
        }

        try
        {
            MailTemplateRenderer.Validate(normalized, allowedTokens);
        }
        catch (ArgumentException exception)
        {
            throw new SettingsValidationException($"{name}包含未知或不完整的模板标记：{exception.Message}");
        }

        return normalized;
    }

    private static string NormalizeRequired(string? value, int maximumLength, string name)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is 0 || normalized.Length > maximumLength || normalized.Contains('\0', StringComparison.Ordinal))
        {
            throw new SettingsValidationException($"{name}不能为空且不能超过 {maximumLength} 个字符。");
        }

        return normalized;
    }

    private static string? NormalizeOptional(string? value, int maximumLength, string name)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (value.Length > maximumLength || value.Contains('\0', StringComparison.Ordinal))
        {
            throw new SettingsValidationException($"{name}不能超过 {maximumLength} 个字符。");
        }

        return value;
    }

    private string? ReadEncrypted(IReadOnlyDictionary<string, SystemSettingEntity> values, string key)
    {
        if (!values.TryGetValue(key, out var setting) || string.IsNullOrEmpty(setting.Value))
        {
            return null;
        }

        if (!setting.IsEncrypted)
        {
            throw new InvalidOperationException($"Sensitive setting '{key}' is not encrypted.");
        }

        return secretProtector.Unprotect(setting.Value);
    }

    private string? ProtectNullable(string? value) => value is null ? null : secretProtector.Protect(value);

    private static string Read(IReadOnlyDictionary<string, SystemSettingEntity> values, string key, string fallback) =>
        values.TryGetValue(key, out var setting) && !setting.IsEncrypted && setting.Value is not null
            ? setting.Value
            : fallback;

    private static bool ReadBool(IReadOnlyDictionary<string, SystemSettingEntity> values, string key, bool fallback) =>
        bool.TryParse(Read(values, key, fallback.ToString(CultureInfo.InvariantCulture)), out var parsed) ? parsed : fallback;

    private static int ReadInt(
        IReadOnlyDictionary<string, SystemSettingEntity> values,
        string key,
        int fallback,
        int minimum,
        int maximum) =>
        int.TryParse(Read(values, key, fallback.ToString(CultureInfo.InvariantCulture)), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) &&
        parsed >= minimum && parsed <= maximum
            ? parsed
            : fallback;

    private static void Set(
        Dictionary<string, SystemSettingEntity> stored,
        ApiDbContext db,
        string key,
        string? value,
        bool encrypted,
        DateTimeOffset now)
    {
        if (!stored.TryGetValue(key, out var setting))
        {
            setting = new SystemSettingEntity { Key = key };
            stored.Add(key, setting);
            db.SystemSettings.Add(setting);
        }

        setting.Value = value;
        setting.IsEncrypted = encrypted;
        setting.UpdatedAtUtc = now;
    }

    private sealed record NormalizedSettings(
        string? PublicBaseUrl,
        string SmtpHost,
        int SmtpPort,
        string SmtpUsername,
        string? NewSmtpPassword,
        string SmtpFromName,
        string SmtpFromAddress,
        bool SmtpUseTls,
        bool RegistrationEnabled,
        bool RequireVerifiedEmail,
        bool PasswordResetEnabled,
        int EmailVerificationTokenExpiryMinutes,
        int PasswordResetTokenExpiryMinutes,
        string VerificationSubjectTemplate,
        string VerificationBodyTemplate,
        string ResetSubjectTemplate,
        string ResetBodyTemplate,
        string TestSubjectTemplate,
        string TestBodyTemplate);
}
