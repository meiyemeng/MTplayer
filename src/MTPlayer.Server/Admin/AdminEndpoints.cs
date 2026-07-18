using System.Net.Mail;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using MTPlayer.Server.Mail;
using MTPlayer.Server.Membership;
using MTPlayer.Server.Settings;
using MTPlayer.Server.Auth;

namespace MTPlayer.Server.Admin;

public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/admin")
            .RequireAuthorization(AdminAuthentication.ApiPolicy);
        group.MapGet("/settings", GetSettingsAsync);
        group.MapGet("/client-config", GetClientConfig);
        group.MapPut("/settings", UpdateSettingsAsync);
        group.MapPost("/email/test", SendTestEmailAsync).RequireRateLimiting("email-token");
        group.MapGet("/member-pushes", (MembershipService memberships, CancellationToken ct) =>
            memberships.ListAllAsync(ct));
        group.MapPost("/member-pushes", async (MemberPushUpdate update, MembershipService memberships, HttpContext context, CancellationToken ct) =>
            await SavePushAsync(null, update, memberships, context, ct));
        group.MapPut("/member-pushes/{id:guid}", async (Guid id, MemberPushUpdate update, MembershipService memberships, HttpContext context, CancellationToken ct) =>
            await SavePushAsync(id, update, memberships, context, ct));
        group.MapDelete("/member-pushes/{id:guid}", async (Guid id, MembershipService memberships, CancellationToken ct) =>
            await memberships.DeleteAsync(id, ct) == 1 ? Results.NoContent() : Results.NotFound());
        group.MapPut("/members/{userId:guid}", async (Guid userId, MembershipUpdate update, MembershipService memberships, HttpContext context, CancellationToken ct) =>
        {
            try { return await memberships.SetMembershipAsync(userId, update, ct) ? Results.NoContent() : Results.NotFound(); }
            catch (ArgumentException exception) { return Problem(context, "invalid_membership", exception.Message, StatusCodes.Status400BadRequest); }
        });

        routes.MapGet("/api/v1/member/pushes", async (HttpContext context, MembershipService memberships, CancellationToken ct) =>
            CurrentUser.TryGetUserId(context.User, out var userId)
                ? Results.Ok(await memberships.ListForUserAsync(userId, ct))
                : Results.Unauthorized())
            .RequireAuthorization("sync-access");
        return routes;
    }

    private static async Task<IResult> SavePushAsync(
        Guid? id,
        MemberPushUpdate update,
        MembershipService memberships,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        try { return Results.Ok(await memberships.SaveAsync(id, update, cancellationToken)); }
        catch (ArgumentException exception) { return Problem(context, "invalid_member_push", exception.Message, StatusCodes.Status400BadRequest); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }

    private static IResult GetClientConfig(HttpContext context)
    {
        var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}";
        return Results.Ok(new
        {
            apiBaseUrl = baseUrl.TrimEnd('/'),
            accountApiVersion = "v1",
            syncEnabled = true,
        });
    }

    private static Task<AdminSettingsView> GetSettingsAsync(
        SystemSettingsService settings,
        CancellationToken cancellationToken) =>
        settings.GetAdminViewAsync(cancellationToken);

    private static async Task<IResult> UpdateSettingsAsync(
        AdminSettingsUpdate update,
        SystemSettingsService settings,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            await settings.UpdateAsync(update, cancellationToken);
            return Results.NoContent();
        }
        catch (SettingsValidationException exception)
        {
            return Problem(context, "invalid_settings", exception.Message, StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> SendTestEmailAsync(
        TestEmailRequest request,
        SystemSettingsService settings,
        MailOutboxService outbox,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var email = request.RecipientEmail?.Trim() ?? string.Empty;
        if (!MailAddress.TryCreate(email, out var address) ||
            !string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase))
        {
            return Problem(context, "invalid_email", "测试收件邮箱格式无效。", StatusCodes.Status400BadRequest);
        }

        var snapshot = await settings.GetSnapshotAsync(cancellationToken);
        if (!snapshot.MailConfigurationComplete)
        {
            return Problem(context, "mail_not_configured", "SMTP 与公开地址尚未完整配置。", StatusCodes.Status409Conflict);
        }

        var id = await outbox.EnqueueProtectedAsync(email, "test:", cancellationToken);
        return Results.Accepted(value: new { id, message = "测试邮件已加入发件箱。" });
    }

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

public sealed record TestEmailRequest(string? RecipientEmail);
