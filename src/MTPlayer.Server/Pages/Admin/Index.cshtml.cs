using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MTPlayer.Server.Admin;

namespace MTPlayer.Server.Pages.Admin;

[AllowAnonymous]
[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class IndexModel(AdminSetupService setup) : PageModel
{
    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!await setup.IsCompletedAsync(cancellationToken))
        {
            return Redirect("/admin/setup");
        }

        var authentication = await HttpContext.AuthenticateAsync(AdminAuthentication.CookieScheme);
        return authentication.Succeeded && authentication.Principal?.IsInRole("admin") == true
            ? Redirect("/admin/settings")
            : Redirect("/admin/login");
    }
}
