using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MTPlayer.Server.Admin;

namespace MTPlayer.Server.Pages.Admin;

[Authorize(Policy = AdminAuthentication.PagePolicy)]
public sealed class LogoutModel : PageModel
{
    public IActionResult OnGet() => NotFound();

    public async Task<IActionResult> OnPostAsync()
    {
        await HttpContext.SignOutAsync(AdminAuthentication.CookieScheme);
        return RedirectToPage("/Admin/Login");
    }
}
