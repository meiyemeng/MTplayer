using System.Globalization;
using System.Security.Claims;
using MTPlayer.Server.Admin;
using MTPlayer.Server.Auth;

namespace MTPlayer.Server.Devices;

public static class DeviceEndpoints
{
    public static IEndpointRouteBuilder MapDeviceEndpoints(this IEndpointRouteBuilder routes)
    {
        var tv = routes.MapGroup("/api/v1/auth/tv");
        tv.MapGet("/device-code", CreateDeviceCodeAsync)
            .AllowAnonymous()
            .RequireRateLimiting("device-code");
        tv.MapPost("/token", PollDeviceCodeAsync)
            .AllowAnonymous()
            .RequireRateLimiting("device-poll");
        tv.MapPost("/approve", ApproveDeviceCodeAsync)
            .RequireAuthorization()
            .RequireRateLimiting("device-approve");

        var devices = routes.MapGroup("/api/v1/devices").RequireAuthorization();
        devices.MapGet("/", ListDevicesAsync);
        devices.MapDelete("/{deviceId:guid}", RevokeDeviceAsync);
        devices.MapPost("/revoke-all", RevokeAllDevicesAsync);

        var admin = routes.MapGroup("/api/v1/admin/users")
            .RequireAuthorization(AdminAuthentication.ApiPolicy);
        admin.MapGet("/", ListUsersAsync);
        admin.MapPost("/{userId:guid}/disable", DisableUserAsync);
        admin.MapPost("/{userId:guid}/enable", EnableUserAsync);
        admin.MapPost("/{userId:guid}/revoke-all", AdminRevokeAllAsync);
        return routes;
    }

    private static async Task<IResult> CreateDeviceCodeAsync(
        string? serverName,
        DeviceService devices,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var result = await devices.CreateCodeAsync(serverName, cancellationToken);
        return result.Status switch
        {
            DeviceCodeStatus.Success => Results.Ok(result.DeviceCode),
            DeviceCodeStatus.PublicUrlNotConfigured => Problem(
                context,
                "public_url_not_configured",
                "管理员尚未配置服务公开地址。",
                StatusCodes.Status409Conflict),
            _ => Problem(
                context,
                "invalid_device_name",
                "电视名称不能为空且不能超过 200 个字符。",
                StatusCodes.Status400BadRequest),
        };
    }

    private static async Task<IResult> PollDeviceCodeAsync(
        TvTokenRequest request,
        DeviceService devices,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var result = await devices.PollAsync(request.DeviceCode, cancellationToken);
        if (result.Status == DeviceCodeStatus.SlowDown)
        {
            context.Response.Headers.RetryAfter = result.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        }

        return result.Status switch
        {
            DeviceCodeStatus.Success => Results.Ok(result.Tokens),
            DeviceCodeStatus.Pending => Problem(
                context,
                "authorization_pending",
                "等待用户确认电视登录。",
                StatusCodes.Status428PreconditionRequired),
            DeviceCodeStatus.SlowDown => Problem(
                context,
                "polling_too_fast",
                "轮询过于频繁，请稍后重试。",
                StatusCodes.Status429TooManyRequests),
            DeviceCodeStatus.Disabled => Problem(
                context,
                "account_disabled",
                "账号已被禁用。",
                StatusCodes.Status403Forbidden),
            _ => Problem(
                context,
                "invalid_or_expired_device_code",
                "电视设备码无效、已过期或已使用。",
                StatusCodes.Status400BadRequest),
        };
    }

    private static async Task<IResult> ApproveDeviceCodeAsync(
        TvApprovalRequest request,
        ClaimsPrincipal principal,
        DeviceService devices,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!CurrentUser.TryGetUserId(principal, out var userId))
        {
            return Results.Unauthorized();
        }

        var status = await devices.ApproveAsync(userId, request.UserCode, cancellationToken);
        return status == DeviceCodeStatus.Success
            ? Results.NoContent()
            : Problem(
                context,
                "invalid_or_expired_user_code",
                "电视确认码无效、已过期或已被其他账号确认。",
                StatusCodes.Status400BadRequest);
    }

    private static async Task<IResult> ListDevicesAsync(
        ClaimsPrincipal principal,
        DeviceService devices,
        CancellationToken cancellationToken) =>
        CurrentUser.TryGetUserId(principal, out var userId)
            ? Results.Ok(await devices.ListAsync(userId, cancellationToken))
            : Results.Unauthorized();

    private static async Task<IResult> RevokeDeviceAsync(
        Guid deviceId,
        ClaimsPrincipal principal,
        DeviceService devices,
        CancellationToken cancellationToken) =>
        CurrentUser.TryGetUserId(principal, out var userId) &&
        await devices.RevokeAsync(userId, deviceId, cancellationToken)
            ? Results.NoContent()
            : Results.NotFound();

    private static async Task<IResult> RevokeAllDevicesAsync(
        ClaimsPrincipal principal,
        DeviceService devices,
        CancellationToken cancellationToken) =>
        CurrentUser.TryGetUserId(principal, out var userId) &&
        await devices.RevokeAllAsync(userId, cancellationToken)
            ? Results.NoContent()
            : Results.NotFound();

    private static async Task<IResult> ListUsersAsync(
        DeviceService devices,
        CancellationToken cancellationToken) =>
        Results.Ok(await devices.ListUsersAsync(cancellationToken));

    private static Task<IResult> DisableUserAsync(
        Guid userId,
        ClaimsPrincipal principal,
        DeviceService devices,
        HttpContext context,
        CancellationToken cancellationToken) =>
        SetDisabledAsync(userId, disabled: true, principal, devices, context, cancellationToken);

    private static Task<IResult> EnableUserAsync(
        Guid userId,
        ClaimsPrincipal principal,
        DeviceService devices,
        HttpContext context,
        CancellationToken cancellationToken) =>
        SetDisabledAsync(userId, disabled: false, principal, devices, context, cancellationToken);

    private static async Task<IResult> SetDisabledAsync(
        Guid userId,
        bool disabled,
        ClaimsPrincipal principal,
        DeviceService devices,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (disabled && CurrentUser.TryGetUserId(principal, out var currentUserId) && currentUserId == userId)
        {
            return Problem(
                context,
                "cannot_disable_self",
                "不能禁用当前登录的管理员账号。",
                StatusCodes.Status400BadRequest);
        }

        return await devices.SetDisabledAsync(userId, disabled, cancellationToken)
            ? Results.NoContent()
            : Results.NotFound();
    }

    private static async Task<IResult> AdminRevokeAllAsync(
        Guid userId,
        DeviceService devices,
        CancellationToken cancellationToken) =>
        await devices.RevokeAllAsync(userId, cancellationToken)
            ? Results.NoContent()
            : Results.NotFound();

    private static IResult Problem(HttpContext context, string code, string title, int statusCode) =>
        Results.Problem(
            statusCode: statusCode,
            title: title,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code,
                ["traceId"] = context.TraceIdentifier,
            });
}

public sealed record TvTokenRequest(string DeviceCode)
{
    public override string ToString() => $"{nameof(TvTokenRequest)} {{ }}";
}

public sealed record TvApprovalRequest(string UserCode)
{
    public override string ToString() => $"{nameof(TvApprovalRequest)} {{ }}";
}
