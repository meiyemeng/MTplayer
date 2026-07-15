using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MTPlayer.Server.Admin;
using MTPlayer.Server.Auth;
using MTPlayer.Server.Devices;

namespace MTPlayer.Server.Pages.Admin;

[Authorize(Policy = AdminAuthentication.PagePolicy)]
public sealed class UsersModel(DeviceService devices) : PageModel
{
    public IReadOnlyList<AdminUserSummary> Users { get; private set; } = [];

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        Users = await devices.ListUsersAsync(cancellationToken);

    public Task<IActionResult> OnPostDisableAsync(Guid userId, CancellationToken cancellationToken) =>
        SetDisabledAsync(userId, disabled: true, cancellationToken);

    public Task<IActionResult> OnPostEnableAsync(Guid userId, CancellationToken cancellationToken) =>
        SetDisabledAsync(userId, disabled: false, cancellationToken);

    public async Task<IActionResult> OnPostRevokeAllAsync(Guid userId, CancellationToken cancellationToken)
    {
        StatusMessage = await devices.RevokeAllAsync(userId, cancellationToken)
            ? "已撤销该用户的全部设备会话。"
            : "未找到用户。";
        return RedirectToPage();
    }

    private async Task<IActionResult> SetDisabledAsync(
        Guid userId,
        bool disabled,
        CancellationToken cancellationToken)
    {
        if (disabled && CurrentUser.TryGetUserId(User, out var currentUserId) && currentUserId == userId)
        {
            StatusMessage = "不能禁用当前登录的管理员账号。";
            return RedirectToPage();
        }

        StatusMessage = await devices.SetDisabledAsync(userId, disabled, cancellationToken)
            ? disabled ? "用户已禁用，全部设备会话已撤销。" : "用户已启用。"
            : "未找到用户。";
        return RedirectToPage();
    }
}
