using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MTPlayer.Server.Auth;
using MTPlayer.Server.Data;
using MTPlayer.Server.Security;

var builder = WebApplication.CreateBuilder(args);
const string postgreSqlConnectionStringKey = "ConnectionStrings:PostgreSQL";
const string dataEncryptionKeyConfigurationKey = "DATA_ENCRYPTION_KEY";
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

builder.Services.AddDbContext<ApiDbContext>(options =>
    options.UseNpgsql(postgreSqlConnectionString));
builder.Services.AddSingleton<ISecretProtector>(
    _ => new AesGcmSecretProtector(dataEncryptionKey));
builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton<TokenFactory>();
builder.Services.AddSingleton(_ => JwtOptions.FromDataEncryptionKey(dataEncryptionKey));
builder.Services.AddSingleton<IConfigureOptions<JwtBearerOptions>, ConfigureJwtBearerOptions>();
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();
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
});
builder.Services.AddScoped<IAuthorizationHandler, SyncAccessHandler>();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<AuthService>();
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
});

var app = builder.Build();
_ = app.Services.GetRequiredService<ISecretProtector>();
_ = app.Services.GetRequiredService<Argon2PasswordService>();

app.UseExceptionHandler();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapAuthEndpoints();
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
