using System.Net.Mail;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using MTPlayer.Server.Auth;
using MTPlayer.Server.Data;

namespace MTPlayer.Server.Admin;

public static class AdminAuthentication
{
    public const string CookieScheme = "admin-cookie";
    public const string ApiPolicy = "admin-api";
    public const string PagePolicy = "admin-page";
}

public sealed class AdminAuthenticationService(
    IDbContextFactory<ApiDbContext> dbContextFactory,
    Argon2PasswordService passwords)
{
    public async Task<bool> SignInAsync(
        HttpContext context,
        string? emailInput,
        string? password,
        bool rememberMe,
        CancellationToken cancellationToken)
    {
        var normalizedEmail = NormalizeEmail(emailInput);
        var validPassword = password is not null && password.Length is >= 10 and <= 128;
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var user = normalizedEmail is null
            ? null
            : await db.Users.AsNoTracking()
                .SingleOrDefaultAsync(value => value.NormalizedEmail == normalizedEmail, cancellationToken);
        var passwordMatches = validPassword && await passwords.VerifyOrDummyAsync(
            user?.PasswordHash,
            password!,
            cancellationToken);
        if (!passwordMatches || user is null || user.Disabled || !user.EmailVerified || user.Role != "admin")
        {
            return false;
        }

        var identity = new ClaimsIdentity(
        [
            new Claim("sub", user.Id.ToString("D", System.Globalization.CultureInfo.InvariantCulture)),
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString("D", System.Globalization.CultureInfo.InvariantCulture)),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Role, "admin"),
        ], AdminAuthentication.CookieScheme);
        await context.SignInAsync(
            AdminAuthentication.CookieScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IsPersistent = rememberMe,
                AllowRefresh = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(rememberMe ? 12 : 2),
            });
        return true;
    }

    private static string? NormalizeEmail(string? input)
    {
        var email = input?.Trim() ?? string.Empty;
        return email.Length is > 0 and <= 320 &&
            MailAddress.TryCreate(email, out var address) &&
            string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase)
                ? email.ToUpperInvariant()
                : null;
    }
}

public sealed class AdminCookieEvents(IDbContextFactory<ApiDbContext> dbContextFactory) : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var subject = context.Principal?.FindFirstValue("sub");
        if (!Guid.TryParse(subject, out var userId))
        {
            await RejectAsync(context);
            return;
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(context.HttpContext.RequestAborted);
        var activeAdmin = await db.Users.AsNoTracking().AnyAsync(
            user => user.Id == userId && user.EmailVerified && !user.Disabled && user.Role == "admin",
            context.HttpContext.RequestAborted);
        if (!activeAdmin)
        {
            await RejectAsync(context);
        }
    }

    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.Redirect($"/admin/login?returnUrl={Uri.EscapeDataString(context.Request.PathBase + context.Request.Path + context.Request.QueryString)}");
        return Task.CompletedTask;
    }

    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(AdminAuthentication.CookieScheme);
    }
}
