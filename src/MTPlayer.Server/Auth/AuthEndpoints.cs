using MTPlayer.Contracts;

namespace MTPlayer.Server.Auth;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/auth").AllowAnonymous();
        group.MapPost("/register", RegisterAsync).RequireRateLimiting("registration");
        group.MapPost("/verify-email", VerifyEmailAsync).RequireRateLimiting("email-token");
        group.MapPost("/login", LoginAsync).RequireRateLimiting("login");
        group.MapPost("/refresh", RefreshAsync).RequireRateLimiting("refresh");
        group.MapPost("/forgot-password", ForgotPasswordAsync).RequireRateLimiting("email-token");
        group.MapPost("/reset-password", ResetPasswordAsync).RequireRateLimiting("email-token");
        return routes;
    }

    private static async Task<IResult> RegisterAsync(
        RegisterRequest request,
        AuthService auth,
        HttpContext context,
        CancellationToken cancellationToken) =>
        await auth.RegisterAsync(request, cancellationToken) switch
        {
            AuthStatus.Accepted => Accepted(context, "如果该邮箱可注册，将收到验证邮件。"),
            AuthStatus.RegistrationClosed => Problem(context, "registration_disabled", "管理员已关闭新用户注册。", StatusCodes.Status403Forbidden),
            _ => Problem(context, "invalid_registration", "邮箱格式无效，或密码长度不在 10 到 128 个字符之间。", StatusCodes.Status400BadRequest),
        };

    private static async Task<IResult> VerifyEmailAsync(
        VerifyEmailRequest request,
        AuthService auth,
        HttpContext context,
        CancellationToken cancellationToken) =>
        await auth.VerifyEmailAsync(request.Token, cancellationToken) == AuthStatus.Success
            ? Results.NoContent()
            : Problem(context, "invalid_or_expired_token", "验证令牌无效、已使用或已过期。", StatusCodes.Status400BadRequest);

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        AuthService auth,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var result = await auth.LoginAsync(request, context, cancellationToken);
        return result.Status switch
        {
            AuthStatus.Success => Results.Ok(result.Tokens),
            AuthStatus.Disabled => Problem(context, "account_disabled", "账号已被禁用。", StatusCodes.Status403Forbidden),
            AuthStatus.VerificationRequired => Problem(context, "verification_required", "请先完成邮箱验证。", StatusCodes.Status403Forbidden),
            AuthStatus.InvalidInput => Problem(context, "invalid_login", "登录信息格式无效。", StatusCodes.Status400BadRequest),
            _ => Problem(context, "invalid_credentials", "邮箱或密码错误。", StatusCodes.Status401Unauthorized),
        };
    }

    private static async Task<IResult> RefreshAsync(
        RefreshRequest request,
        AuthService auth,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var result = await auth.RefreshAsync(request.RefreshToken, cancellationToken);
        return result.Status switch
        {
            AuthStatus.Success => Results.Ok(result.Tokens),
            AuthStatus.Disabled => Problem(context, "account_disabled", "账号已被禁用。", StatusCodes.Status403Forbidden),
            _ => Problem(context, "invalid_refresh_token", "刷新令牌无效、已使用或已过期。", StatusCodes.Status401Unauthorized),
        };
    }

    private static async Task<IResult> ForgotPasswordAsync(
        ForgotPasswordRequest request,
        AuthService auth,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        await auth.ForgotPasswordAsync(request.Email, cancellationToken);
        return Accepted(context, "如果该邮箱存在，将收到密码重置邮件。");
    }

    private static async Task<IResult> ResetPasswordAsync(
        ResetPasswordRequest request,
        AuthService auth,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var status = await auth.ResetPasswordAsync(request.Token, request.Password, cancellationToken);
        return status switch
        {
            AuthStatus.Success => Results.NoContent(),
            AuthStatus.PasswordResetClosed => Problem(context, "password_reset_disabled", "管理员已关闭密码重置。", StatusCodes.Status403Forbidden),
            AuthStatus.InvalidInput => Problem(context, "invalid_password", "密码长度必须为 10 到 128 个字符。", StatusCodes.Status400BadRequest),
            _ => Problem(context, "invalid_or_expired_token", "重置令牌无效、已使用或已过期。", StatusCodes.Status400BadRequest),
        };
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

    private static IResult Accepted(HttpContext context, string message) =>
        Results.Accepted(value: new
        {
            message,
            traceId = context.TraceIdentifier,
        });
}

public sealed record VerifyEmailRequest(string Token)
{
    public override string ToString() => $"{nameof(VerifyEmailRequest)} {{ }}";
}

public sealed record ForgotPasswordRequest(string Email);

public sealed record ResetPasswordRequest(string Token, string Password)
{
    public override string ToString() => $"{nameof(ResetPasswordRequest)} {{ }}";
}
