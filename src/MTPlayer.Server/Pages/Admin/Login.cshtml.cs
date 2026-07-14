using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using MTPlayer.Server.Admin;

namespace MTPlayer.Server.Pages.Admin;

[AllowAnonymous]
[EnableRateLimiting("login")]
public sealed class LoginModel(AdminAuthenticationService authentication) : PageModel
{
    [BindProperty]
    public LoginInput Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public IActionResult OnGet() => User.IsInRole("admin") ? RedirectToPage("/Admin/Settings") : Page();

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        if (!await authentication.SignInAsync(
                HttpContext,
                Input.Email,
                Input.Password,
                Input.RememberMe,
                cancellationToken))
        {
            ModelState.AddModelError(string.Empty, "邮箱或密码错误，或该账号不是可用管理员。");
            return Page();
        }

        return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/admin/settings");
    }

    public sealed class LoginInput
    {
        [Required]
        public string? Email { get; set; }

        [Required]
        public string? Password { get; set; }

        public bool RememberMe { get; set; }
    }
}
