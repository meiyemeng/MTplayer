using System.Text.Json;

namespace MTPlayer.Server.WebClient;

public static class WebClientEndpoints
{
    public static IEndpointRouteBuilder MapWebClientEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/", () => Results.Redirect("/player"));
        routes.MapGet("/app", () => Results.Redirect("/player"));

        // The web player remains useful without an account, matching the desktop and
        // Android clients. Authentication is required only by the sync endpoints.
        var web = routes.MapGroup("/api/v1/web").AllowAnonymous().RequireRateLimiting("web-catalogue");
        web.MapPost("/config/inspect", async (WebConfigRequest request, WebClientGateway gateway, CancellationToken ct) =>
            await ExecuteAsync(() => gateway.InspectAsync(request, ct)));
        web.MapPost("/live/inspect", async (WebLiveInspectRequest request, WebClientGateway gateway, CancellationToken ct) =>
            await ExecuteAsync(() => gateway.InspectLiveAsync(request, ct)));
        web.MapPost("/catalogue/latest", async (WebCatalogueRequest request, WebClientGateway gateway, CancellationToken ct) =>
            await ExecuteAsync(() => gateway.LatestAsync(request, ct)));
        web.MapPost("/catalogue/search", async (WebCatalogueRequest request, WebClientGateway gateway, CancellationToken ct) =>
            await ExecuteAsync(() => gateway.SearchAsync(request, ct)));
        web.MapPost("/catalogue/detail", async (WebDetailRequest request, WebClientGateway gateway, CancellationToken ct) =>
            await ExecuteAsync(() => gateway.DetailAsync(request, ct)));
        web.MapPost("/media/sign", (WebSignRequest request, WebClientGateway gateway) =>
        {
            try { return Results.Ok(new { url = gateway.SignMedia(request.Url) }); }
            catch (Exception exception) when (exception is ArgumentException or UriFormatException)
            { return Results.BadRequest(new { message = exception.Message }); }
        });

        routes.MapGet("/api/v1/web/proxy/{kind}", async (string kind, string token, HttpContext context, WebClientGateway gateway, CancellationToken ct) =>
            await gateway.ProxyAsync(context, kind, token, ct))
            .AllowAnonymous().RequireRateLimiting("web-proxy");
        return routes;
    }

    private static async Task<IResult> ExecuteAsync<T>(Func<Task<T>> action)
    {
        try { return Results.Ok(await action()); }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or JsonException)
        { return Results.BadRequest(new { message = exception.Message }); }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
        { return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "远程资源读取失败。", detail: exception.Message); }
    }
}
