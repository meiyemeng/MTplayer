using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MTPlayer.Server.Auth;
using MTPlayer.Server.Admin;
using MTPlayer.Server.Data;
using MTPlayer.Server.Devices;
using MTPlayer.Server.Diagnostics;
using MTPlayer.Server.Mail;
using MTPlayer.Server.Maintenance;
using MTPlayer.Server.Security;
using MTPlayer.Server.Settings;
using MTPlayer.Server.Sync;
using MTPlayer.Server.WebClient;

var builder = WebApplication.CreateBuilder(args);
const string postgreSqlConnectionStringKey = "ConnectionStrings:PostgreSQL";
const string dataEncryptionKeyConfigurationKey = "DATA_ENCRYPTION_KEY";
var command = MaintenanceCommandLine.Parse(args);
if (command.ExportOpenApiPath is not null)
{
    builder.Configuration[postgreSqlConnectionStringKey] =
        "Host=127.0.0.1;Port=1;Database=openapi_export;Username=openapi_export;Password=openapi_export;Pooling=false";
    builder.Configuration[dataEncryptionKeyConfigurationKey] =
        "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
    builder.Configuration["Mail:WorkerEnabled"] = "false";
}

if (command.RotateKey || command.ExportOpenApiPath is not null)
{
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
}

var dataEncryptionKey = builder.Configuration[dataEncryptionKeyConfigurationKey] ?? string.Empty;
try
{
    AesGcmSecretProtector.ValidateKey(dataEncryptionKey);
}
catch (ArgumentException exception)
{
    throw new InvalidOperationException(
        "DATA_ENCRYPTION_KEY must be a Base64 encoded 32-byte key.",
        exception);
}

var postgreSqlConnectionString = builder.Configuration[postgreSqlConnectionStringKey];
if (string.IsNullOrWhiteSpace(postgreSqlConnectionString))
{
    throw new InvalidOperationException(
        $"Configuration value '{postgreSqlConnectionStringKey}' is required and cannot be empty.");
}

builder.Services.AddDbContextFactory<ApiDbContext>(options =>
    options.UseNpgsql(postgreSqlConnectionString));
builder.Services.Configure<ForwardedHeadersOptions>(options =>
    ForwardedHeaderConfiguration.Apply(options, builder.Configuration));
builder.Services.AddSingleton<ISecretProtector>(
    _ => new AesGcmSecretProtector(dataEncryptionKey));
builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton<TokenFactory>();
builder.Services.AddSingleton(_ => JwtOptions.FromDataEncryptionKey(dataEncryptionKey));
builder.Services.AddSingleton<IConfigureOptions<JwtBearerOptions>, ConfigureJwtBearerOptions>();
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer()
    .AddCookie(AdminAuthentication.CookieScheme, options =>
    {
        options.Cookie.Name = "__Host-MTPlayerAdmin";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.Path = "/";
        options.ExpireTimeSpan = TimeSpan.FromHours(2);
        options.SlidingExpiration = true;
        options.EventsType = typeof(AdminCookieEvents);
    });
builder.Services.AddAuthorization(options =>
{
    options.DefaultPolicy = new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()
        .Build();
    options.AddPolicy("sync-access", policy =>
    {
        policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme);
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new SyncAccessRequirement());
    });
    options.AddPolicy(AdminAuthentication.ApiPolicy, policy =>
    {
        policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme);
        policy.RequireAuthenticatedUser();
        policy.RequireRole("admin");
    });
    options.AddPolicy(AdminAuthentication.PagePolicy, policy =>
    {
        policy.AddAuthenticationSchemes(AdminAuthentication.CookieScheme);
        policy.RequireAuthenticatedUser();
        policy.RequireRole("admin");
    });
});
builder.Services.AddScoped<IAuthorizationHandler, SyncAccessHandler>();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<DeviceService>();
builder.Services.AddScoped<SyncService>();
builder.Services.AddSingleton<WebProxySigner>();
builder.Services.AddHttpClient<WebClientGateway>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(25);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36 MTPlayer-Web/1.2");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.7");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json,text/plain,image/*,video/*,*/*");
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(10),
    });
builder.Services.AddScoped<KeyRotationService>();
builder.Services.AddScoped<AdminAuthenticationService>();
builder.Services.AddScoped<AdminCookieEvents>();
builder.Services.AddSingleton<AdminSetupService>();
builder.Services.AddSingleton<IPublicBaseUrlProbe, PublicBaseUrlProbe>();
builder.Services.AddSingleton<SystemSettingsService>();
builder.Services.AddSingleton<MailOutboxService>();
builder.Services.AddScoped<MailOutboxDispatcher>();
builder.Services.AddSingleton<ISmtpEmailSender, SmtpEmailSender>();
if (builder.Configuration.GetValue("Mail:WorkerEnabled", true))
{
    builder.Services.AddHostedService<MailOutboxWorker>();
}
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(serviceProvider => new Argon2PasswordService(
    serviceProvider.GetRequiredService<PasswordHasher>(),
    Math.Clamp(builder.Configuration.GetValue<int?>("Security:Argon2MaxConcurrency") ?? 2, 1, 4)));
builder.Services.AddSingleton(serviceProvider => new AuthTiming(
    serviceProvider.GetRequiredService<TimeProvider>(),
    TimeSpan.FromMilliseconds(Math.Clamp(
        builder.Configuration.GetValue<int?>("Security:MinimumAuthResponseMilliseconds") ?? 100,
        25,
        1_000))));
builder.Services.AddProblemDetails();
builder.Services.AddRazorPages();
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseReadinessHealthCheck>("postgresql", tags: ["ready"]);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        var httpContext = context.HttpContext;
        if (httpContext.Response.HasStarted)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var traceId = httpContext.TraceIdentifier;
        httpContext.Response.Headers["X-Request-ID"] = traceId;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            var retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
            httpContext.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        }

        await Results.Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "请求过于频繁，请稍后再试。",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "rate_limit_exceeded",
                    ["traceId"] = traceId,
                })
            .ExecuteAsync(httpContext);
    };
    AddFixedWindowPolicy(options, builder.Configuration, "registration", 5, TimeSpan.FromMinutes(10));
    AddFixedWindowPolicy(options, builder.Configuration, "login", 10, TimeSpan.FromMinutes(1));
    AddFixedWindowPolicy(options, builder.Configuration, "refresh", 30, TimeSpan.FromMinutes(1));
    AddFixedWindowPolicy(options, builder.Configuration, "email-token", 5, TimeSpan.FromMinutes(10));
    AddFixedWindowPolicy(options, builder.Configuration, "device-code", 10, TimeSpan.FromMinutes(10));
    AddFixedWindowPolicy(options, builder.Configuration, "device-poll", 120, TimeSpan.FromMinutes(10));
    AddFixedWindowPolicy(options, builder.Configuration, "device-approve", 20, TimeSpan.FromMinutes(10));
    AddFixedWindowPolicy(options, builder.Configuration, "web-catalogue", 180, TimeSpan.FromMinutes(1));
    AddFixedWindowPolicy(options, builder.Configuration, "web-proxy", 600, TimeSpan.FromMinutes(1));
});

var app = builder.Build();
_ = app.Services.GetRequiredService<ISecretProtector>();
_ = app.Services.GetRequiredService<Argon2PasswordService>();

await DatabaseStartup.ApplyMigrationsAsync(app.Services, app.Configuration, app.Lifetime.ApplicationStopping);
app.UseForwardedHeaders();
app.UseMiddleware<RequestIdMiddleware>();
app.UseExceptionHandler();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapAuthEndpoints();
app.MapAdminEndpoints();
app.MapDeviceEndpoints();
app.MapSyncEndpoints();
app.MapWebClientEndpoints();
app.MapMtPlayerHealthChecks();
app.MapRazorPages();
if (command.ExportOpenApiPath is not null)
{
    await OpenApiExporter.ExportAsync(app, command.ExportOpenApiPath, app.Lifetime.ApplicationStopping);
    return;
}

if (command.RotateKey)
{
    await using var scope = app.Services.CreateAsyncScope();
    var rotation = scope.ServiceProvider.GetRequiredService<KeyRotationService>();
    var newKey = command.NewEncryptionKey ?? KeyRotationService.CreateKey();
    await rotation.RotateAsync(newKey, app.Lifetime.ApplicationStopping);
    Console.WriteLine(newKey);
    return;
}

app.Run();

static void AddFixedWindowPolicy(
    RateLimiterOptions options,
    IConfiguration configuration,
    string policyName,
    int defaultPermitLimit,
    TimeSpan window)
{
    var configuredLimit = configuration.GetValue<int?>(
        $"RateLimiting:{policyName}:PermitLimit");
    var permitLimit = Math.Clamp(configuredLimit ?? defaultPermitLimit, 1, 10_000);
    options.AddPolicy(policyName, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = permitLimit,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                Window = window,
            }));
}

public partial class Program
{
}
