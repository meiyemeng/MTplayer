using System.ComponentModel.DataAnnotations;
using System.Net.Mail;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MTPlayer.Server.Admin;
using MTPlayer.Server.Mail;
using MTPlayer.Server.Settings;

namespace MTPlayer.Server.Pages.Admin;

[Authorize(Policy = AdminAuthentication.PagePolicy)]
public sealed class SettingsModel(SystemSettingsService settings, MailOutboxService outbox) : PageModel
{
    [BindProperty]
    public AdminSettingsUpdate Input { get; set; } = new();

    [BindProperty]
    [EmailAddress(ErrorMessage = "测试收件邮箱格式无效。")]
    public string? TestRecipientEmail { get; set; }

    [TempData]
    public string? StatusMessage { get; set; }

    public bool PublicBaseUrlConfigured { get; private set; }
    public bool SmtpPasswordConfigured { get; private set; }
    public bool MailConfigurationComplete { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadAsync(cancellationToken);

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            await LoadFlagsAsync(cancellationToken);
            return Page();
        }

        try
        {
            await settings.UpdateAsync(Input, cancellationToken);
            StatusMessage = "系统设置已保存。";
            return RedirectToPage();
        }
        catch (SettingsValidationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            await LoadFlagsAsync(cancellationToken);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostTestEmailAsync(CancellationToken cancellationToken)
    {
        var email = TestRecipientEmail?.Trim() ?? string.Empty;
        var snapshot = await settings.GetSnapshotAsync(cancellationToken);
        if (!MailAddress.TryCreate(email, out var address) ||
            !string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(nameof(TestRecipientEmail), "测试收件邮箱格式无效。");
        }
        else if (!snapshot.MailConfigurationComplete)
        {
            ModelState.AddModelError(string.Empty, "请先完整配置公开地址和 SMTP。");
        }
        else
        {
            await outbox.EnqueueProtectedAsync(email, "test:", cancellationToken);
            StatusMessage = "测试邮件已加入发件箱。";
            return RedirectToPage();
        }

        await LoadAsync(cancellationToken);
        return Page();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var view = await settings.GetAdminViewAsync(cancellationToken);
        ApplyFlags(view);
        Input = new AdminSettingsUpdate
        {
            PublicBaseUrl = null,
            SmtpHost = view.SmtpHost,
            SmtpPort = view.SmtpPort,
            SmtpUsername = view.SmtpUsername,
            NewSmtpPassword = null,
            SmtpFromName = view.SmtpFromName,
            SmtpFromAddress = view.SmtpFromAddress,
            SmtpUseTls = view.SmtpUseTls,
            RegistrationEnabled = view.RegistrationEnabled,
            RequireVerifiedEmail = view.RequireVerifiedEmail,
            PasswordResetEnabled = view.PasswordResetEnabled,
            EmailVerificationTokenExpiryMinutes = view.EmailVerificationTokenExpiryMinutes,
            PasswordResetTokenExpiryMinutes = view.PasswordResetTokenExpiryMinutes,
            VerificationSubjectTemplate = view.VerificationSubjectTemplate,
            VerificationBodyTemplate = view.VerificationBodyTemplate,
            ResetSubjectTemplate = view.ResetSubjectTemplate,
            ResetBodyTemplate = view.ResetBodyTemplate,
            TestSubjectTemplate = view.TestSubjectTemplate,
            TestBodyTemplate = view.TestBodyTemplate,
        };
    }

    private async Task LoadFlagsAsync(CancellationToken cancellationToken) =>
        ApplyFlags(await settings.GetAdminViewAsync(cancellationToken));

    private void ApplyFlags(AdminSettingsView view)
    {
        PublicBaseUrlConfigured = view.PublicBaseUrlConfigured;
        SmtpPasswordConfigured = view.SmtpPasswordConfigured;
        MailConfigurationComplete = view.MailConfigurationComplete;
    }
}
