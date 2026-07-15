using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using MTPlayer.Server.Data;

namespace MTPlayer.Server.Auth;

public sealed class CurrentUser(ApiDbContext db)
{
    public static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId)
    {
        var subject = principal.FindFirstValue(ClaimTypes.NameIdentifier) ??
            principal.FindFirstValue("sub");
        return Guid.TryParse(subject, out userId);
    }

    public async Task<bool> CanSyncAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(principal, out var userId) ||
            !principal.HasClaim("email_verified", "true") ||
            !principal.HasClaim("scope", "sync"))
        {
            return false;
        }

        var account = await db.Users.AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => new { user.EmailVerified, user.Disabled })
            .SingleOrDefaultAsync(cancellationToken);
        var requireVerifiedValue = await db.SystemSettings.AsNoTracking()
            .Where(setting => setting.Key == "RequireVerifiedEmail" && !setting.IsEncrypted)
            .Select(setting => setting.Value)
            .SingleOrDefaultAsync(cancellationToken);
        var requireVerifiedEmail = !bool.TryParse(requireVerifiedValue, out var parsedRequireVerified) ||
            parsedRequireVerified;
        return account is not null &&
            !account.Disabled &&
            (account.EmailVerified || !requireVerifiedEmail);
    }
}

public sealed class SyncAccessRequirement : IAuthorizationRequirement;

public sealed class SyncAccessHandler(CurrentUser currentUser) : AuthorizationHandler<SyncAccessRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        SyncAccessRequirement requirement)
    {
        if (await currentUser.CanSyncAsync(context.User, CancellationToken.None))
        {
            context.Succeed(requirement);
        }
    }
}
