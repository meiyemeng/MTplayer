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

        return await db.Users.AnyAsync(
            user => user.Id == userId && user.EmailVerified && !user.Disabled,
            cancellationToken);
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
