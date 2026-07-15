using System.Text.RegularExpressions;

namespace MTPlayer.Server.Diagnostics;

public sealed partial class RequestIdMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var supplied = context.Request.Headers["X-Request-ID"].ToString();
        var requestId = supplied.Length is > 0 and <= 128 && SafeRequestId().IsMatch(supplied)
            ? supplied
            : Guid.NewGuid().ToString("N");
        context.TraceIdentifier = requestId;
        context.Response.Headers["X-Request-ID"] = requestId;
        await next(context);
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeRequestId();
}
