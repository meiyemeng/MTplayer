using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MTPlayer.Server.Admin;
using MTPlayer.Server.Membership;

namespace MTPlayer.Server.Pages.Admin;

[Authorize(Policy = AdminAuthentication.PagePolicy)]
public sealed class PushesModel(MembershipService memberships) : PageModel
{
    public IReadOnlyList<MemberPushView> Pushes { get; private set; } = [];
    [TempData] public string? StatusMessage { get; set; }

    public async Task OnGetAsync(CancellationToken ct) => Pushes = await memberships.ListAllAsync(ct);

    public async Task<IActionResult> OnPostSaveAsync(Guid? id, string title, string minimumMembershipLevel,
        string? configurationSources, string? liveSources, bool enabled, CancellationToken ct)
    {
        try
        {
            await memberships.SaveAsync(id, new MemberPushUpdate(title, minimumMembershipLevel,
                ParseSources(configurationSources), ParseSources(liveSources), enabled), ct);
            StatusMessage = "会员推送已保存，客户端下次下载或双向同步时自动接收。";
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException) { StatusMessage = ex.Message; }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken ct)
    {
        await memberships.DeleteAsync(id, ct); StatusMessage = "推送已删除。"; return RedirectToPage();
    }

    public static string FormatSources(IEnumerable<MemberSource> values) =>
        string.Join(Environment.NewLine, values.Select(value => $"{value.Name}|{value.Address}"));

    private static MemberSource[] ParseSources(string? value) => (value ?? string.Empty)
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(line => line.Split('|', 2, StringSplitOptions.TrimEntries))
        .Where(parts => parts.Length == 2)
        .Select(parts => new MemberSource(parts[0], parts[1])).ToArray();
}
