using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using MTPlayer.Contracts;
using MTPlayer.Server.Auth;

namespace MTPlayer.Server.Sync;

public static class SyncEndpoints
{
    public static IEndpointRouteBuilder MapSyncEndpoints(this IEndpointRouteBuilder routes)
    {
        var sync = routes.MapGroup("/api/v1/sync").RequireAuthorization("sync-access");
        sync.MapPost("/push", PushAsync).RequireAuthorization("sync-access")
            .WithMetadata(new RequestSizeLimitAttribute(SyncPayloadValidator.MaximumRequestBytes));
        sync.MapGet("/pull", PullAsync).RequireAuthorization("sync-access");
        return routes;
    }

    private static async Task<IResult> PushAsync(
        SyncPushRequest request,
        ClaimsPrincipal principal,
        SyncService sync,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!TryIdentity(principal, out var userId, out var sessionId))
        {
            return Results.Unauthorized();
        }

        if (JsonSerializer.SerializeToUtf8Bytes(request).Length > SyncPayloadValidator.MaximumRequestBytes)
        {
            return Problem(context, "payload_too_large", StatusCodes.Status413PayloadTooLarge);
        }

        try
        {
            return Results.Ok(await sync.PushAsync(userId, sessionId, request, cancellationToken));
        }
        catch (SyncRequestException exception)
        {
            return Problem(context, exception.Code, StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> PullAsync(
        long cursor,
        int limit,
        ClaimsPrincipal principal,
        SyncService sync,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!TryIdentity(principal, out var userId, out var sessionId))
        {
            return Results.Unauthorized();
        }

        try
        {
            return Results.Ok(await sync.PullAsync(
                userId,
                sessionId,
                cursor,
                limit,
                cancellationToken));
        }
        catch (SyncRequestException exception)
        {
            return Problem(context, exception.Code, StatusCodes.Status400BadRequest);
        }
    }

    private static bool TryIdentity(ClaimsPrincipal principal, out Guid userId, out Guid sessionId)
    {
        sessionId = default;
        return CurrentUser.TryGetUserId(principal, out userId) &&
            Guid.TryParse(principal.FindFirstValue("sid"), out sessionId);
    }

    private static IResult Problem(HttpContext context, string code, int status) => Results.Problem(
        statusCode: status,
        title: "同步请求无效。",
        extensions: new Dictionary<string, object?>
        {
            ["code"] = code,
            ["traceId"] = context.TraceIdentifier,
        });
}
