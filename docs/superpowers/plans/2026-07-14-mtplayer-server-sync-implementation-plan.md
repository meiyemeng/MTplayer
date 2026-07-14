# MT播放器服务端与同步 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 构建可部署到群晖 Docker、通过用户现有 Cloudflare Tunnel 访问的账号、邮件、设备管理、网页后台和增量同步服务。

**Architecture:** ASP.NET Core 8 单体服务承载 REST API、Razor Pages 管理后台和数据库发件箱后台任务，PostgreSQL 保存账号与同步数据。客户端只连接用户填写的 HTTPS 域名；服务端不代理或存储媒体。

**Tech Stack:** .NET SDK 8.0.422、ASP.NET Core 8、EF Core 8、Npgsql 8、PostgreSQL 16、Razor Pages、Argon2id、JWT、Docker Compose

## Global Constraints

- 产品名称固定为“MT播放器”，API 前缀固定为 `/api/v1`。
- 生产客户端只接受 HTTPS 域名；不得硬编码示例域名或群晖内部端口。
- 容器只包含 `mt-api` 与 `mt-db`；复用用户已有 cloudflared。
- 游客本地播放不依赖服务端；服务端不接收、代理、缓存或存储媒体流。
- 注册开放；同步前必须完成邮箱验证。
- SMTP 与公开地址通过 `/admin` 配置；仅 `DATA_ENCRYPTION_KEY`、`DATABASE_PASSWORD`、`ADMIN_SETUP_TOKEN` 使用环境变量。
- 敏感字段使用 AES-256-GCM；密码使用 Argon2id；刷新令牌只保存 SHA-256 哈希。
- 每个任务必须先运行目标失败测试，再写最小实现，再运行完整相关测试。
- 本计划完成后再执行 Windows、Android、macOS 客户端计划。
- 代码片段中的 `ServerFixture`、`TestDb`、`Fixtures` 等测试辅助类型，均作为当前任务所列测试文件底部的 `private`/`internal` 辅助类型一并创建，不依赖未列出的测试库。

---

### Task 1: 创建契约、服务端与测试工程

**Files:**
- Create: `src/MTPlayer.Contracts/MTPlayer.Contracts.csproj`
- Create: `src/MTPlayer.Contracts/AuthContracts.cs`
- Create: `src/MTPlayer.Contracts/SyncContracts.cs`
- Create: `src/MTPlayer.Server/MTPlayer.Server.csproj`
- Create: `src/MTPlayer.Server/Program.cs`
- Create: `tests/MTPlayer.Server.Tests/MTPlayer.Server.Tests.csproj`
- Modify: `WebHtv.Windows.sln`

**Interfaces:**
- Produces: `RegisterRequest`, `LoginRequest`, `TokenResponse`, `SyncMutation`, `SyncPushRequest`, `SyncPullResponse`.
- Produces: ASP.NET Core entry point `Program` visible to `WebApplicationFactory<Program>`.

- [ ] **Step 1: Write the failing contract serialization test**

```csharp
[Fact]
public void SyncMutation_round_trips_with_web_json_names()
{
    var payload = JsonSerializer.SerializeToElement(new { title = "仙逆" });
    var value = new SyncMutation(Guid.Parse("11111111-1111-1111-1111-111111111111"),
        SyncEntityKind.Favorite, 3, DateTimeOffset.Parse("2026-07-14T00:00:00Z"), false, payload);
    var json = JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    var restored = JsonSerializer.Deserialize<SyncMutation>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    Assert.Equal(value, restored);
}
```

- [ ] **Step 2: Run the test and verify the missing-project failure**

Run: `dotnet test .\tests\MTPlayer.Server.Tests\MTPlayer.Server.Tests.csproj --filter SyncMutation_round_trips_with_web_json_names`

Expected: FAIL because `MTPlayer.Server.Tests.csproj` and contract types do not exist.

- [ ] **Step 3: Create the projects and exact public contracts**

```csharp
namespace MTPlayer.Contracts;

public sealed record RegisterRequest(string Email, string Password);
public sealed record LoginRequest(string Email, string Password, string DeviceName, string Platform);
public sealed record RefreshRequest(string RefreshToken);
public sealed record TokenResponse(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAtUtc, bool EmailVerified);
public sealed record DeviceCodeResponse(string DeviceCode, string UserCode, Uri VerificationUri, DateTimeOffset ExpiresAtUtc, int PollIntervalSeconds);

public enum SyncEntityKind { ConfigurationGroup, Favorite, PlaybackHistory, SkipMarker, Preference }
public sealed record SyncMutation(Guid Id, SyncEntityKind Kind, long BaseVersion, DateTimeOffset ModifiedAtUtc, bool IsDeleted, JsonElement Payload);
public sealed record SyncPushRequest(Guid DeviceId, IReadOnlyList<SyncMutation> Mutations);
public sealed record SyncPushResult(Guid Id, long Version, DateTimeOffset ServerModifiedAtUtc, bool Accepted, string? ErrorCode);
public sealed record SyncPullResponse(long Cursor, IReadOnlyList<SyncMutation> Changes);
```

Create the server with `Microsoft.NET.Sdk.Web`, target `net8.0`, reference `MTPlayer.Contracts`, and expose `public partial class Program { }` after `app.Run();`.

- [ ] **Step 4: Add projects to the solution and run tests**

Run:

```powershell
dotnet sln .\WebHtv.Windows.sln add .\src\MTPlayer.Contracts\MTPlayer.Contracts.csproj
dotnet sln .\WebHtv.Windows.sln add .\src\MTPlayer.Server\MTPlayer.Server.csproj
dotnet sln .\WebHtv.Windows.sln add .\tests\MTPlayer.Server.Tests\MTPlayer.Server.Tests.csproj
dotnet test .\tests\MTPlayer.Server.Tests\MTPlayer.Server.Tests.csproj
```

Expected: PASS, 1 test passed.

- [ ] **Step 5: Commit**

```powershell
git add WebHtv.Windows.sln src/MTPlayer.Contracts src/MTPlayer.Server tests/MTPlayer.Server.Tests
git commit -m "feat(server): add API and sync contracts"
```

### Task 2: PostgreSQL 模型与迁移

**Files:**
- Create: `src/MTPlayer.Server/Data/ApiDbContext.cs`
- Create: `src/MTPlayer.Server/Data/Entities.cs`
- Create: `src/MTPlayer.Server/Data/EntityConfigurations.cs`
- Create: `src/MTPlayer.Server/Data/Migrations/*`
- Create: `tests/MTPlayer.Server.Tests/Data/ApiDbContextTests.cs`
- Modify: `src/MTPlayer.Server/MTPlayer.Server.csproj`
- Modify: `src/MTPlayer.Server/Program.cs`

**Interfaces:**
- Produces: `ApiDbContext` with `Users`, `DeviceSessions`, `SyncRecords`, `ChangeLog`, `EmailTokens`, `MailOutbox`, `SystemSettings`, `AuditLog`.
- Consumes: `SyncEntityKind` from Task 1.

- [ ] **Step 1: Write model constraint tests**

```csharp
[Fact]
public void User_email_has_unique_normalized_index()
{
    using var db = TestDb.CreateContext();
    var entity = db.Model.FindEntityType(typeof(UserEntity))!;
    Assert.Contains(entity.GetIndexes(), index => index.IsUnique &&
        index.Properties.Single().Name == nameof(UserEntity.NormalizedEmail));
}
```

- [ ] **Step 2: Run the model tests**

Run: `dotnet test .\tests\MTPlayer.Server.Tests\MTPlayer.Server.Tests.csproj --filter User_email_has_unique_normalized_index`

Expected: FAIL because `ApiDbContext` and entities do not exist.

- [ ] **Step 3: Add entities and mappings**

```csharp
public sealed class UserEntity
{
    public Guid Id { get; set; }
    public required string Email { get; set; }
    public required string NormalizedEmail { get; set; }
    public required string PasswordHash { get; set; }
    public bool EmailVerified { get; set; }
    public bool Disabled { get; set; }
    public string Role { get; set; } = "user";
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class SyncRecordEntity
{
    public Guid UserId { get; set; }
    public Guid Id { get; set; }
    public SyncEntityKind Kind { get; set; }
    public long Version { get; set; }
    public DateTimeOffset ModifiedAtUtc { get; set; }
    public bool IsDeleted { get; set; }
    public required string PayloadJson { get; set; }
}
```

Map `(UserId, Id, Kind)` as the primary key, use PostgreSQL `jsonb` for `PayloadJson`, and create indexes for normalized email, refresh-token hash, change cursor, outbox status, and token expiry.

- [ ] **Step 4: Create and validate the initial migration**

Run:

```powershell
dotnet ef migrations add InitialServerSchema --project .\src\MTPlayer.Server\MTPlayer.Server.csproj --output-dir Data\Migrations
dotnet test .\tests\MTPlayer.Server.Tests\MTPlayer.Server.Tests.csproj --filter ApiDbContextTests
```

Expected: PASS; migration contains all 9 tables and the unique normalized-email index.

- [ ] **Step 5: Commit**

```powershell
git add src/MTPlayer.Server/Data tests/MTPlayer.Server.Tests/Data src/MTPlayer.Server/Program.cs
git commit -m "feat(server): add PostgreSQL data model"
```

### Task 3: 加密、密码与令牌基础设施

**Files:**
- Create: `src/MTPlayer.Server/Security/ISecretProtector.cs`
- Create: `src/MTPlayer.Server/Security/AesGcmSecretProtector.cs`
- Create: `src/MTPlayer.Server/Security/PasswordHasher.cs`
- Create: `src/MTPlayer.Server/Security/TokenFactory.cs`
- Create: `tests/MTPlayer.Server.Tests/Security/SecurityTests.cs`
- Modify: `src/MTPlayer.Server/MTPlayer.Server.csproj`
- Modify: `src/MTPlayer.Server/Program.cs`

**Interfaces:**
- Produces: `string Protect(string plaintext)`, `string Unprotect(string encoded)`.
- Produces: `string HashPassword(string password)`, `bool VerifyPassword(string encoded, string password)`.
- Produces: random 32-byte refresh tokens and SHA-256 token hashes.

- [ ] **Step 1: Write deterministic behavior tests**

```csharp
[Fact]
public void Secret_round_trip_uses_random_nonce()
{
    var key = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
    var protector = new AesGcmSecretProtector(key);
    var first = protector.Protect("smtp-password");
    var second = protector.Protect("smtp-password");
    Assert.NotEqual(first, second);
    Assert.Equal("smtp-password", protector.Unprotect(first));
}
```

- [ ] **Step 2: Run security tests**

Run: `dotnet test .\tests\MTPlayer.Server.Tests\MTPlayer.Server.Tests.csproj --filter SecurityTests`

Expected: FAIL because security services do not exist.

- [ ] **Step 3: Implement AES-GCM envelope and Argon2id**

Use envelope bytes `[version=1][12-byte nonce][16-byte tag][ciphertext]`, Base64 encoded. Reject decoded values shorter than 30 bytes and keys not exactly 32 bytes. Configure Argon2id with 64 MiB memory, 3 iterations, parallelism 2, 16-byte random salt and 32-byte output. Use `CryptographicOperations.FixedTimeEquals` for all hash comparisons.

```csharp
public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string encoded);
}
```

- [ ] **Step 4: Run tests and validate missing-key startup failure**

Run:

```powershell
dotnet test .\tests\MTPlayer.Server.Tests\MTPlayer.Server.Tests.csproj --filter SecurityTests
$env:DATA_ENCRYPTION_KEY=''; dotnet run --project .\src\MTPlayer.Server\MTPlayer.Server.csproj
```

Expected: tests PASS; server exits with a clear `DATA_ENCRYPTION_KEY must be a Base64 encoded 32-byte key` message.

- [ ] **Step 5: Commit**

```powershell
git add src/MTPlayer.Server/Security tests/MTPlayer.Server.Tests/Security src/MTPlayer.Server/Program.cs src/MTPlayer.Server/MTPlayer.Server.csproj
git commit -m "feat(server): add encryption and credential security"
```

### Task 4: 注册、邮箱验证、登录、刷新与找回密码

**Files:**
- Create: `src/MTPlayer.Server/Auth/AuthService.cs`
- Create: `src/MTPlayer.Server/Auth/AuthEndpoints.cs`
- Create: `src/MTPlayer.Server/Auth/JwtOptions.cs`
- Create: `src/MTPlayer.Server/Auth/CurrentUser.cs`
- Create: `tests/MTPlayer.Server.Tests/Auth/AuthFlowTests.cs`
- Modify: `src/MTPlayer.Server/Program.cs`

**Interfaces:**
- Produces: endpoints `/api/v1/auth/register`, `verify-email`, `login`, `refresh`, `forgot-password`, `reset-password`.
- Consumes: `PasswordHasher`, `TokenFactory`, `ApiDbContext`, `MailOutbox`.

- [ ] **Step 1: Write the end-to-end authentication test**

```csharp
[Fact]
public async Task Verified_user_can_login_and_rotated_refresh_token_cannot_be_reused()
{
    await using var app = await ServerFixture.StartAsync();
    await app.Client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest("a@example.com", "Correct-Horse-2026"));
    var token = await app.ReadLatestEmailTokenAsync("a@example.com", "verify");
    await app.Client.PostAsJsonAsync("/api/v1/auth/verify-email", new { token });
    var login = await app.Client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("a@example.com", "Correct-Horse-2026", "测试电脑", "windows"));
    var pair = (await login.Content.ReadFromJsonAsync<TokenResponse>())!;
    var firstRefresh = await app.Client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(pair.RefreshToken));
    Assert.True(firstRefresh.IsSuccessStatusCode);
    var reuse = await app.Client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(pair.RefreshToken));
    Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
}
```

- [ ] **Step 2: Run the auth test**

Run: `dotnet test .\tests\MTPlayer.Server.Tests\MTPlayer.Server.Tests.csproj --filter Verified_user_can_login`

Expected: FAIL with HTTP 404 for the first auth endpoint.

- [ ] **Step 3: Implement routes and security rules**

Normalize emails with `Trim().ToUpperInvariant()`. Require 10–128 character passwords. Verification and reset tokens expire after values loaded from system settings; store only token hashes. Access tokens expire after 15 minutes; refresh tokens after 30 days and rotate on every use. Reject login and sync when `Disabled` is true; allow an unverified user to log in only long enough to resend verification, but issue no sync-capable token.

Map routes through:

```csharp
public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder routes)
{
    var group = routes.MapGroup("/api/v1/auth");
    group.MapPost("/register", RegisterAsync).RequireRateLimiting("registration");
    group.MapPost("/verify-email", VerifyEmailAsync).RequireRateLimiting("email-token");
    group.MapPost("/login", LoginAsync).RequireRateLimiting("login");
    group.MapPost("/refresh", RefreshAsync).RequireRateLimiting("refresh");
    group.MapPost("/forgot-password", ForgotPasswordAsync).RequireRateLimiting("email-token");
    group.MapPost("/reset-password", ResetPasswordAsync).RequireRateLimiting("email-token");
    return routes;
}
```

- [ ] **Step 4: Run auth tests**

Run: `dotnet test .\tests\MTPlayer.Server.Tests\MTPlayer.Server.Tests.csproj --filter AuthFlowTests`

Expected: PASS for register, duplicate email, verification, disabled user, reset, token rotation and expiry cases.

- [ ] **Step 5: Commit**

```powershell
git add src/MTPlayer.Server/Auth tests/MTPlayer.Server.Tests/Auth src/MTPlayer.Server/Program.cs
git commit -m "feat(server): add account authentication flows"
```

### Task 5: 网页初始化、系统设置与 SMTP 发件箱

**Files:**
- Create: `src/MTPlayer.Server/Settings/SystemSettingsService.cs`
- Create: `src/MTPlayer.Server/Mail/MailOutboxService.cs`
- Create: `src/MTPlayer.Server/Mail/MailOutboxWorker.cs`
- Create: `src/MTPlayer.Server/Pages/Admin/Setup.cshtml`
- Create: `src/MTPlayer.Server/Pages/Admin/Setup.cshtml.cs`
- Create: `src/MTPlayer.Server/Pages/Admin/Settings.cshtml`
- Create: `src/MTPlayer.Server/Pages/Admin/Settings.cshtml.cs`
- Create: `tests/MTPlayer.Server.Tests/Admin/AdminSettingsTests.cs`
- Modify: `src/MTPlayer.Server/Program.cs`

**Interfaces:**
- Produces: encrypted keys `SmtpPassword`, `PublicBaseUrl`, mail templates and feature switches.
- Produces: durable `EnqueueAsync`, `ClaimBatchAsync`, `MarkSentAsync`, `MarkFailedAsync`.

- [ ] **Step 1: Write first-run and SMTP tests**

```csharp
[Fact]
public async Task Setup_token_is_single_use_and_smtp_password_is_not_returned()
{
    await using var app = await ServerFixture.StartAsync(adminSetupToken: "one-use-token");
    var created = await app.Client.PostAsJsonAsync("/admin/setup", new { token = "one-use-token", email = "owner@example.com", password = "Owner-Password-2026" });
    Assert.True(created.IsSuccessStatusCode);
    var retry = await app.Client.PostAsJsonAsync("/admin/setup", new { token = "one-use-token", email = "other@example.com", password = "Owner-Password-2026" });
    Assert.Equal(HttpStatusCode.NotFound, retry.StatusCode);
    var settings = await app.AdminClient.GetStringAsync("/api/v1/admin/settings");
    Assert.DoesNotContain("smtp-password", settings, StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run admin settings tests**

Run: `dotnet test .\tests\MTPlayer.Server.Tests\MTPlayer.Server.Tests.csproj --filter AdminSettingsTests`

Expected: FAIL because `/admin/setup` and settings endpoints do not exist.

- [ ] **Step 3: Implement Razor Pages and encrypted settings**

The settings form must contain `PublicBaseUrl`, `SmtpHost`, `SmtpPort`, `SmtpUsername`, `NewSmtpPassword`, `SmtpFromName`, `SmtpFromAddress`, `SmtpUseTls`, `RegistrationEnabled`, `RequireVerifiedEmail`, token-expiry minutes and three email templates. Show SMTP password as `已配置`/`未配置`; never bind the stored plaintext back to HTML.

Validate `PublicBaseUrl` as an absolute HTTPS URI with no path, query or fragment. Keep it null until the administrator saves a real value. The test-email action is disabled until SMTP and public URL are complete.

- [ ] **Step 4: Implement durable outbox and run tests**

Claim pending rows in a transaction with `FOR UPDATE SKIP LOCKED`, retry after 1, 5, 15, 60 and 360 minutes, and stop after 8 attempts. Render templates using only `{verificationUrl}`, `{resetUrl}`, `{email}` and `{expiresMinutes}` tokens; HTML encode all substituted user values.

Run: `dotnet test .\tests\MTPlayer.Server.Tests\MTPlayer.Server.Tests.csproj --filter "AdminSettingsTests|MailOutbox"`

Expected: PASS; restart test proves an unsent row remains queued.

- [ ] **Step 5: Commit**

```powershell
git add src/MTPlayer.Server/Settings src/MTPlayer.Server/Mail src/MTPlayer.Server/Pages tests/MTPlayer.Server.Tests/Admin src/MTPlayer.Server/Program.cs
git commit -m "feat(server): add web settings and durable email"
```

### Task 6: 设备管理与电视设备码登录

**Files:**
- Create: `src/MTPlayer.Server/Devices/DeviceService.cs`
- Create: `src/MTPlayer.Server/Devices/DeviceEndpoints.cs`
- Create: `src/MTPlayer.Server/Pages/Admin/Users.cshtml`
- Create: `src/MTPlayer.Server/Pages/Admin/Users.cshtml.cs`
- Create: `tests/MTPlayer.Server.Tests/Devices/DeviceFlowTests.cs`
- Modify: `src/MTPlayer.Server/Program.cs`

**Interfaces:**
- Produces: `/api/v1/devices`, `/api/v1/auth/tv/device-code`, `/api/v1/auth/tv/approve`, `/api/v1/auth/tv/token`.
- Produces: administrator disable/enable and revoke-all operations.

- [ ] **Step 1: Write TV device-code flow test**

```csharp
[Fact]
public async Task Tv_receives_tokens_only_after_user_approval()
{
    await using var app = await ServerFixture.StartVerifiedUserAsync();
    var code = await app.Client.GetFromJsonAsync<DeviceCodeResponse>("/api/v1/auth/tv/device-code?serverName=客厅电视");
    var pending = await app.Client.PostAsJsonAsync("/api/v1/auth/tv/token", new { code!.DeviceCode });
    Assert.Equal(HttpStatusCode.PreconditionRequired, pending.StatusCode);
    await app.UserClient.PostAsJsonAsync("/api/v1/auth/tv/approve", new { code.UserCode });
    var approved = await app.Client.PostAsJsonAsync("/api/v1/auth/tv/token", new { code.DeviceCode });
    Assert.True(approved.IsSuccessStatusCode);
}
```

- [ ] **Step 2: Run device tests**

Run: `dotnet test .\tests\MTPlayer.Server.Tests\MTPlayer.Server.Tests.csproj --filter DeviceFlowTests`

Expected: FAIL with HTTP 404.

- [ ] **Step 3: Implement single-use codes and device revocation**

Generate an 8-character uppercase user code excluding `0/O/1/I`, store only hashes of both codes, expire after 10 minutes, poll no faster than every 5 seconds, and invalidate the code after token issuance. Device list responses expose name, platform, created time and last activity but never token hashes.

- [ ] **Step 4: Run device and admin tests**

Run: `dotnet test .\tests\MTPlayer.Server.Tests\MTPlayer.Server.Tests.csproj --filter "DeviceFlowTests|AdminUser"`

Expected: PASS for approval, expiry, double-use, polling limit, single-device revoke, revoke-all and disabled-account cases.

- [ ] **Step 5: Commit**

```powershell
git add src/MTPlayer.Server/Devices src/MTPlayer.Server/Pages/Admin/Users* tests/MTPlayer.Server.Tests/Devices src/MTPlayer.Server/Program.cs
git commit -m "feat(server): add devices and TV sign-in"
```

### Task 7: 增量同步、游客合并与冲突规则

**Files:**
- Create: `src/MTPlayer.Server/Sync/SyncService.cs`
- Create: `src/MTPlayer.Server/Sync/SyncEndpoints.cs`
- Create: `src/MTPlayer.Server/Sync/SyncPayloadValidator.cs`
- Create: `tests/MTPlayer.Server.Tests/Sync/SyncFlowTests.cs`
- Modify: `src/MTPlayer.Server/Program.cs`

**Interfaces:**
- Produces: `PushAsync(Guid userId, SyncPushRequest request)`, `PullAsync(Guid userId, long cursor, int limit)`.
- Produces: `/api/v1/sync/push` and `/api/v1/sync/pull?cursor={long}&limit={int}`.

- [ ] **Step 1: Write conflict and tombstone tests**

```csharp
[Fact]
public async Task Newer_server_change_wins_and_delete_is_returned_as_tombstone()
{
    await using var app = await ServerFixture.StartVerifiedUserAsync();
    var id = Guid.NewGuid();
    await app.PushAsync(new SyncMutation(id, SyncEntityKind.Preference, 0, DateTimeOffset.UtcNow, false,
        JsonSerializer.SerializeToElement(new { key = "defaultSpeed", value = 1.25 })));
    var stale = await app.PushAsync(new SyncMutation(id, SyncEntityKind.Preference, 0, DateTimeOffset.UtcNow.AddMinutes(-1), false,
        JsonSerializer.SerializeToElement(new { key = "defaultSpeed", value = 2.0 })));
    Assert.False(stale.Single().Accepted);
    await app.DeleteAsync(id, SyncEntityKind.Preference);
    var pulled = await app.PullAsync(0);
    Assert.Contains(pulled.Changes, change => change.Id == id && change.IsDeleted);
}
```

- [ ] **Step 2: Run sync tests**

Run: `dotnet test .\tests\MTPlayer.Server.Tests\MTPlayer.Server.Tests.csproj --filter SyncFlowTests`

Expected: FAIL because sync routes do not exist.

- [ ] **Step 3: Implement transaction and validation rules**

Accept no more than 500 mutations or 2 MiB per push. Validate payload schemas per `SyncEntityKind`. Increment a user-scoped monotonic change cursor in the same transaction as the record update. Preserve tombstones for 180 days after every active device has advanced beyond their cursor. Return `version_conflict` with the current server record when `BaseVersion` is stale.

Conflict resolver behavior:

```csharp
public static ConflictDecision Decide(SyncEntityKind kind, SyncMutation client, SyncRecordEntity? server) =>
    server is null ? ConflictDecision.Accept :
    client.BaseVersion == server.Version ? ConflictDecision.Accept :
    kind == SyncEntityKind.Favorite && !client.IsDeleted && !server.IsDeleted ? ConflictDecision.MergeFavorite :
    client.ModifiedAtUtc > server.ModifiedAtUtc ? ConflictDecision.Accept : ConflictDecision.RejectWithServer;
```

- [ ] **Step 4: Run sync and authorization tests**

Run: `dotnet test .\tests\MTPlayer.Server.Tests\MTPlayer.Server.Tests.csproj --filter "SyncFlowTests|Unverified_user_cannot_sync"`

Expected: PASS for incremental cursor, pagination, conflict, tombstone, payload limit, user isolation and unverified-user rejection.

- [ ] **Step 5: Commit**

```powershell
git add src/MTPlayer.Server/Sync tests/MTPlayer.Server.Tests/Sync src/MTPlayer.Server/Program.cs
git commit -m "feat(server): add incremental sync"
```

### Task 8: 群晖 Docker、Cloudflare 转发与运行诊断

**Files:**
- Create: `deploy/synology/docker-compose.yml`
- Create: `deploy/synology/.env.example`
- Create: `deploy/synology/README.zh-CN.md`
- Create: `src/MTPlayer.Server/Diagnostics/HealthChecks.cs`
- Create: `tests/MTPlayer.Server.Tests/Deployment/ForwardedHeadersTests.cs`
- Modify: `src/MTPlayer.Server/Program.cs`

**Interfaces:**
- Produces: `/health/live`, `/health/ready`.
- Consumes: existing Cloudflare Tunnel either through shared Docker network or a NAS-local mapped port.

- [ ] **Step 1: Write forwarded-host and health tests**

```csharp
[Fact]
public async Task Forwarded_https_host_is_used_for_generated_links()
{
    await using var app = await ServerFixture.StartAsync();
    using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/client-config");
    request.Headers.Add("X-Forwarded-Proto", "https");
    request.Headers.Add("X-Forwarded-Host", "media.example.com");
    var response = await app.AdminClient.SendAsync(request);
    var body = await response.Content.ReadAsStringAsync();
    Assert.Contains("https://media.example.com", body);
    Assert.DoesNotContain(":8080", body);
}
```

- [ ] **Step 2: Run deployment tests**

Run: `dotnet test .\tests\MTPlayer.Server.Tests\MTPlayer.Server.Tests.csproj --filter Deployment`

Expected: FAIL before forwarded headers and health checks are configured.

- [ ] **Step 3: Add production middleware and Compose**

Compose must expose `mt-api:8080` only inside the `mtplayer` network by default. Add an opt-in profile named `nas-port` that maps `${MT_API_BIND_ADDRESS:-127.0.0.1}:${MT_API_BIND_PORT:-8888}:8080` for an existing Tunnel that cannot join the Docker network. Never place the public hostname in Compose.

Configure `UseForwardedHeaders` before HTTPS redirection, restrict known proxies/networks from configuration, set `AllowedHosts`, add request IDs, redact sensitive log fields, and return readiness failure until PostgreSQL responds and migrations are current.

- [ ] **Step 4: Verify containers and the no-public-port rule**

Run:

```powershell
docker compose -f .\deploy\synology\docker-compose.yml config
docker compose -f .\deploy\synology\docker-compose.yml --profile nas-port up -d --build
Invoke-WebRequest http://127.0.0.1:8888/health/ready -UseBasicParsing
dotnet test .\tests\MTPlayer.Server.Tests\MTPlayer.Server.Tests.csproj
```

Expected: Compose config contains no public hostname; health returns `Healthy`; all server tests PASS.

- [ ] **Step 5: Commit**

```powershell
git add deploy/synology src/MTPlayer.Server/Diagnostics src/MTPlayer.Server/Program.cs tests/MTPlayer.Server.Tests/Deployment
git commit -m "feat(server): add Synology deployment"
```

### Task 9: OpenAPI、备份、密钥轮换与服务端验收

**Files:**
- Create: `src/MTPlayer.Server/Maintenance/BackupCommands.cs`
- Create: `src/MTPlayer.Server/Maintenance/RotateKeyCommand.cs`
- Create: `deploy/synology/scripts/backup.ps1`
- Create: `deploy/synology/scripts/restore.ps1`
- Create: `tests/MTPlayer.Server.Tests/Maintenance/MaintenanceTests.cs`
- Modify: `src/MTPlayer.Server/Program.cs`
- Modify: `deploy/synology/README.zh-CN.md`

**Interfaces:**
- Produces: OpenAPI document at build time, `rotate-key` maintenance command, timestamped PostgreSQL backups.
- Produces: stable contract consumed by all client plans.

- [ ] **Step 1: Write key-rotation rollback test**

```csharp
[Fact]
public async Task Failed_key_rotation_leaves_old_ciphertext_readable()
{
    await using var app = await ServerFixture.StartWithEncryptedSettingsAsync();
    await Assert.ThrowsAsync<CryptographicException>(() => app.RotateKeyAsync("invalid-key"));
    Assert.Equal("smtp.example.com", await app.Settings.GetAsync("SmtpHost"));
}
```

- [ ] **Step 2: Run maintenance tests**

Run: `dotnet test .\tests\MTPlayer.Server.Tests\MTPlayer.Server.Tests.csproj --filter MaintenanceTests`

Expected: FAIL because maintenance commands do not exist.

- [ ] **Step 3: Implement atomic rotation and backup scripts**

Rotation reads all encrypted values, decrypts them with the old key, encrypts them with the new key inside one transaction, verifies every row, commits, and prints the Base64 new key only to the invoking terminal. Backup writes `mtplayer-YYYYMMDD-HHmmss.dump`, checksum and a manifest explicitly stating that `DATA_ENCRYPTION_KEY` must be backed up separately.

- [ ] **Step 4: Export OpenAPI and run the server acceptance suite**

Run:

```powershell
dotnet test .\tests\MTPlayer.Server.Tests\MTPlayer.Server.Tests.csproj
dotnet publish .\src\MTPlayer.Server\MTPlayer.Server.csproj -c Release -o .\artifacts\server
dotnet .\artifacts\server\MTPlayer.Server.dll --export-openapi .\contracts\mtplayer-api-v1.json
```

Expected: all tests PASS; `contracts/mtplayer-api-v1.json` contains auth, device, sync and admin routes; published service starts with valid secrets.

- [ ] **Step 5: Commit**

```powershell
git add src/MTPlayer.Server/Maintenance deploy/synology/scripts deploy/synology/README.zh-CN.md contracts/mtplayer-api-v1.json tests/MTPlayer.Server.Tests/Maintenance src/MTPlayer.Server/Program.cs
git commit -m "feat(server): complete maintenance and API contract"
```
