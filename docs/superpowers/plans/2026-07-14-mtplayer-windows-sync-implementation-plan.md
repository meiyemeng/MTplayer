# MT播放器 Windows 客户端与同步 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在现有 Windows 10/11 WPF 客户端上接入账号与离线同步，并完成多配置源、接口检测、播放器、直播和现代中文界面的正式版加固。

**Architecture:** 将目前位于 `WebHtv.Desktop` 的本地数据模型下沉到可测试的 .NET 领域项目，WPF 只负责展示和平台能力。同步采用本地优先、持久化出站队列和增量游标；未登录或服务器不可达时不影响播放。

**Tech Stack:** .NET SDK 8.0.422、WPF、LibVLCSharp.WPF 3.10.0、WebView2、ASP.NET OpenAPI 客户端、DPAPI、xUnit

## Global Constraints

- 支持 Windows 10/11 x64 与 x86；产品名固定为“MT播放器”。
- 游客拥有完整本地播放能力；登录仅增加同步。
- 首次服务器绑定支持手动 HTTPS 地址、配置二维码/文件；不得硬编码域名或内部端口。
- 多个配置组可以同时启用并参与搜索，搜索后隐藏 Top 10。
- 详情页只显示含当前影片且可访问的接口，顺序为接口、线路、剧集。
- 片头片尾按影片、接口和线路保存；播放器控制 5 秒无操作自动隐藏。
- 保留现有 logo 和圆角快捷方式图标；不显示原生白色滚动条、滑块和组合框。
- 服务端计划及 `contracts/mtplayer-api-v1.json` 必须先完成。
- 代码片段中的 `Fixtures`、`TestFiles`、`FakeSyncApi` 等测试辅助类型，均作为当前任务所列测试文件底部的 `private`/`internal` 辅助类型一并创建。

---

### Task 1: 下沉本地媒体库与设置领域模型

**Files:**
- Create: `src/MTPlayer.Client.Core/MTPlayer.Client.Core.csproj`
- Create: `src/MTPlayer.Client.Core/Library/LibraryModels.cs`
- Create: `src/MTPlayer.Client.Core/Library/JsonLibraryStore.cs`
- Create: `src/MTPlayer.Client.Core/Settings/ClientSettings.cs`
- Create: `src/MTPlayer.Client.Core/Settings/JsonSettingsStore.cs`
- Create: `tests/MTPlayer.Client.Core.Tests/MTPlayer.Client.Core.Tests.csproj`
- Create: `tests/MTPlayer.Client.Core.Tests/Library/JsonLibraryStoreTests.cs`
- Modify: `WebHtv.Windows.sln`
- Modify: `src/WebHtv.Desktop/WebHtv.Desktop.csproj`
- Delete after migration: `src/WebHtv.Desktop/LibraryStore.cs`
- Delete after migration: `src/WebHtv.Desktop/AppSettingsStore.cs`

**Interfaces:**
- Produces: `ILibraryStore`, `IClientSettingsStore`, `FavoriteRecord`, `PlaybackRecord`, `SkipMarkerRecord`, `ConfigurationGroupRecord`.
- Consumes: no WPF types; records serialize identically on Windows and macOS.

- [ ] **Step 1: Write atomic persistence and migration tests**

```csharp
[Fact]
public async Task Legacy_library_migrates_outro_absolute_position_to_remaining_seconds()
{
    var path = TestFiles.Write("library.json", """{"skipMarkers":[{"sourceKey":"a","id":"1","introEndMs":60000,"outroStartMs":120000}]}""");
    var store = new JsonLibraryStore(path);
    var library = await store.LoadAsync();
    var marker = Assert.Single(library.SkipMarkers);
    Assert.Equal(60, marker.IntroEndSeconds);
    Assert.Equal(0, marker.OutroRemainingSeconds);
    Assert.True(library.RequiresDurationRepair);
}
```

- [ ] **Step 2: Run the core test**

Run: `dotnet test .\tests\MTPlayer.Client.Core.Tests\MTPlayer.Client.Core.Tests.csproj --filter Legacy_library_migrates`

Expected: FAIL because the project and store do not exist.

- [ ] **Step 3: Create stable records and atomic stores**

```csharp
public sealed record FavoriteRecord(Guid Id, string SourceKey, string ContentId, string Category,
    string Title, string Caption, string CoverUrl, DateTimeOffset ModifiedAtUtc, long Version = 0, bool IsDeleted = false);
public sealed record PlaybackRecord(Guid Id, string SourceKey, string ContentId, string InterfaceKey,
    string LineName, int EpisodeIndex, long PositionMs, long DurationMs, DateTimeOffset WatchedAtUtc,
    long Version = 0, bool IsDeleted = false);
public sealed record SkipMarkerRecord(Guid Id, string SourceKey, string ContentId, string InterfaceKey,
    string LineName, int IntroEndSeconds, int OutroRemainingSeconds, DateTimeOffset ModifiedAtUtc,
    long Version = 0, bool IsDeleted = false);
public sealed record ConfigurationGroupRecord(Guid Id, string Name, string Address, bool IsEnabled,
    DateTimeOffset ModifiedAtUtc, long Version = 0, bool IsDeleted = false);
```

Each store writes `path.{guid}.tmp`, flushes, replaces the destination, then removes the temporary file in `finally`. Preserve the old `%LocalAppData%\WebHomeTVDesktop` path and add a one-time product-folder migration to `%LocalAppData%\MTPlayer`.

- [ ] **Step 4: Migrate WPF references and run tests**

Run:

```powershell
dotnet sln .\WebHtv.Windows.sln add .\src\MTPlayer.Client.Core\MTPlayer.Client.Core.csproj .\tests\MTPlayer.Client.Core.Tests\MTPlayer.Client.Core.Tests.csproj
dotnet test .\tests\MTPlayer.Client.Core.Tests\MTPlayer.Client.Core.Tests.csproj
dotnet build .\src\WebHtv.Desktop\WebHtv.Desktop.csproj -c Debug -p:Platform=x64
```

Expected: core tests PASS and WPF builds without duplicate legacy store types.

- [ ] **Step 5: Commit**

```powershell
git add WebHtv.Windows.sln src/MTPlayer.Client.Core tests/MTPlayer.Client.Core.Tests src/WebHtv.Desktop
git commit -m "refactor(windows): extract local client data core"
```

### Task 2: 服务器绑定、令牌安全存储与账号客户端

**Files:**
- Create: `src/MTPlayer.Client.Core/Account/ServerBinding.cs`
- Create: `src/MTPlayer.Client.Core/Account/AccountApiClient.cs`
- Create: `src/WebHtv.Desktop/Security/DpapiTokenStore.cs`
- Create: `tests/MTPlayer.Client.Core.Tests/Account/AccountApiClientTests.cs`
- Modify: `src/WebHtv.Desktop/ApplicationPaths.cs`
- Modify: `src/WebHtv.Desktop/ShellViewModel.cs`

**Interfaces:**
- Produces: `ServerBinding.TryCreate(string, bool allowInsecureLoopback, out ServerBinding?)`.
- Produces: `IAccountApiClient` and `ITokenStore`.
- Consumes: auth contracts and generated API routes from the server plan.

- [ ] **Step 1: Write URL safety and refresh tests**

```csharp
[Theory]
[InlineData("https://sync.example.com", true)]
[InlineData("https://sync.example.com:443", true)]
[InlineData("http://sync.example.com", false)]
[InlineData("https://sync.example.com/path", false)]
public void Production_server_binding_requires_https_origin(string value, bool valid)
{
    Assert.Equal(valid, ServerBinding.TryCreate(value, false, out _));
}
```

- [ ] **Step 2: Run account tests**

Run: `dotnet test .\tests\MTPlayer.Client.Core.Tests\MTPlayer.Client.Core.Tests.csproj --filter Account`

Expected: FAIL because account types do not exist.

- [ ] **Step 3: Implement binding and token refresh serialization**

```csharp
public sealed record ServerBinding(Uri BaseUri)
{
    public static bool TryCreate(string value, bool allowInsecureLoopback, out ServerBinding? binding)
    {
        binding = null;
        if (!Uri.TryCreate(value.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri)) return false;
        var loopbackDebug = allowInsecureLoopback && uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp;
        if (uri.Scheme != Uri.UriSchemeHttps && !loopbackDebug) return false;
        if (uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return false;
        binding = new ServerBinding(uri);
        return true;
    }
}
```

Serialize concurrent refreshes with `SemaphoreSlim`; retry one request after a successful refresh; on `401 invalid_refresh_token` clear tokens but keep the server binding and all local media data.

- [ ] **Step 4: Add DPAPI storage and run tests**

Store refresh token bytes with `ProtectedData.Protect(..., DataProtectionScope.CurrentUser)`. Store access tokens in memory only. Run:

```powershell
dotnet test .\tests\MTPlayer.Client.Core.Tests\MTPlayer.Client.Core.Tests.csproj --filter Account
dotnet build .\src\WebHtv.Desktop\WebHtv.Desktop.csproj -c Debug -p:Platform=x86
```

Expected: account tests PASS and x86 build resolves DPAPI correctly.

- [ ] **Step 5: Commit**

```powershell
git add src/MTPlayer.Client.Core/Account tests/MTPlayer.Client.Core.Tests/Account src/WebHtv.Desktop/Security src/WebHtv.Desktop/ApplicationPaths.cs src/WebHtv.Desktop/ShellViewModel.cs
git commit -m "feat(windows): add secure account client"
```

### Task 3: 离线队列、增量同步和首次登录合并

**Files:**
- Create: `src/MTPlayer.Client.Core/Sync/SyncQueueStore.cs`
- Create: `src/MTPlayer.Client.Core/Sync/SyncEngine.cs`
- Create: `src/MTPlayer.Client.Core/Sync/SyncMapper.cs`
- Create: `tests/MTPlayer.Client.Core.Tests/Sync/SyncEngineTests.cs`
- Modify: `src/MTPlayer.Client.Core/Library/JsonLibraryStore.cs`
- Modify: `src/MTPlayer.Client.Core/Settings/JsonSettingsStore.cs`

**Interfaces:**
- Produces: `QueueAsync(SyncMutation)`, `SynchronizeAsync(Guid deviceId)`, `MergeGuestDataAsync()`.
- Consumes: `IAccountApiClient`, `ILibraryStore`, `IClientSettingsStore`.

- [ ] **Step 1: Write offline and merge tests**

```csharp
[Fact]
public async Task Offline_mutation_survives_restart_and_is_removed_only_after_server_accepts_it()
{
    var queue = new SyncQueueStore(TestFiles.Path("sync-queue.json"));
    await queue.EnqueueAsync(Fixtures.FavoriteMutation());
    var api = new FakeSyncApi { IsOffline = true };
    await new SyncEngine(api, queue, Fixtures.Library, Fixtures.Settings).SynchronizeAsync(Guid.NewGuid());
    Assert.Single((await new SyncQueueStore(queue.FilePath).LoadAsync()).Items);
    api.IsOffline = false;
    await new SyncEngine(api, queue, Fixtures.Library, Fixtures.Settings).SynchronizeAsync(Guid.NewGuid());
    Assert.Empty((await queue.LoadAsync()).Items);
}
```

- [ ] **Step 2: Run sync tests**

Run: `dotnet test .\tests\MTPlayer.Client.Core.Tests\MTPlayer.Client.Core.Tests.csproj --filter SyncEngineTests`

Expected: FAIL because sync queue and engine do not exist.

- [ ] **Step 3: Implement push-then-pull synchronization**

Persist queue item IDs, attempt count and next retry UTC. Push no more than 200 mutations per batch, apply accepted versions atomically, then pull pages until fewer than 500 changes are returned. Save the new cursor only after the page is applied. Use 5 seconds, 30 seconds, 2 minutes, 10 minutes and 1 hour retry delays.

Guest merge rules are exact: favorite union, newest playback record, normalized configuration address dedupe, newest skip marker and newest preference. Never delete local data simply because a user logs out.

- [ ] **Step 4: Run sync tests and corruption recovery test**

Run: `dotnet test .\tests\MTPlayer.Client.Core.Tests\MTPlayer.Client.Core.Tests.csproj --filter Sync`

Expected: PASS for restart, partial acceptance, pull pagination, cursor crash safety, tombstones, conflict and first-login merge.

- [ ] **Step 5: Commit**

```powershell
git add src/MTPlayer.Client.Core/Sync src/MTPlayer.Client.Core/Library src/MTPlayer.Client.Core/Settings tests/MTPlayer.Client.Core.Tests/Sync
git commit -m "feat(windows): add offline-first synchronization"
```

### Task 4: 多配置组同时启用与缓存

**Files:**
- Create: `src/MTPlayer.Client.Core/Configuration/ConfigurationGroupService.cs`
- Create: `tests/MTPlayer.Client.Core.Tests/Configuration/ConfigurationGroupServiceTests.cs`
- Modify: `src/WebHtv.Desktop/ShellViewModel.cs`
- Modify: `src/WebHtv.Desktop/MainWindow.xaml`
- Modify: `src/WebHtv.Desktop/MainWindow.xaml.cs`

**Interfaces:**
- Produces: `LoadEnabledProfilesAsync()`, `AddOrUpdateAsync(name, address)`, `SetEnabledAsync(id, enabled)`, `RemoveAsync(id)`.
- Produces: runtime site key format `{groupId:N}:{siteRuntimeKey}`.

- [ ] **Step 1: Write multi-group merge tests**

```csharp
[Fact]
public async Task Enabled_groups_merge_without_site_key_collision()
{
    var service = Fixtures.ConfigurationGroups(("甲", true, "same"), ("乙", true, "same"), ("丙", false, "same"));
    var profile = await service.LoadEnabledProfilesAsync();
    Assert.Equal(2, profile.Sites.Count);
    Assert.All(profile.Sites, site => Assert.Contains(':', site.RuntimeKey));
    Assert.Equal(2, profile.Sites.Select(site => site.RuntimeKey).Distinct().Count());
}
```

- [ ] **Step 2: Run configuration-group tests**

Run: `dotnet test .\tests\MTPlayer.Client.Core.Tests\MTPlayer.Client.Core.Tests.csproj --filter ConfigurationGroupServiceTests`

Expected: FAIL because only one active configuration currently exists.

- [ ] **Step 3: Implement per-group cache and merge**

Save each downloaded document as `%LocalAppData%\MTPlayer\configurations\{id:N}.json`. A failed refresh keeps the last valid cache and records the error. Normalize addresses by lowercasing scheme/host, removing default port and trailing slash while preserving path/query. Prevent duplicate normalized addresses.

- [ ] **Step 4: Replace active-source radio UI with enable switches**

Settings must show name, address, enabled state, last success, refresh and delete. Importing a group enables it by default. Search and Top 10 load all enabled group profiles. Remove `DefaultConfigurationAddress`; a clean install starts with no content source.

Run:

```powershell
dotnet test .\tests\MTPlayer.Client.Core.Tests\MTPlayer.Client.Core.Tests.csproj --filter Configuration
dotnet build .\src\WebHtv.Desktop\WebHtv.Desktop.csproj -c Debug -p:Platform=x64
```

Expected: tests PASS; clean startup shows “请在设置中添加配置源” and no bundled network address.

- [ ] **Step 5: Commit**

```powershell
git add src/MTPlayer.Client.Core/Configuration tests/MTPlayer.Client.Core.Tests/Configuration src/WebHtv.Desktop/ShellViewModel.cs src/WebHtv.Desktop/MainWindow.xaml src/WebHtv.Desktop/MainWindow.xaml.cs
git commit -m "feat(windows): support multiple enabled configuration groups"
```

### Task 5: 搜索聚合与有效接口检测缓存

**Files:**
- Create: `src/MTPlayer.Client.Core/Catalogue/AggregatedSearchService.cs`
- Create: `src/MTPlayer.Client.Core/Catalogue/PlaybackInterfaceProbe.cs`
- Create: `tests/MTPlayer.Client.Core.Tests/Catalogue/AggregatedSearchServiceTests.cs`
- Create: `tests/MTPlayer.Client.Core.Tests/Catalogue/PlaybackInterfaceProbeTests.cs`
- Modify: `src/WebHtv.Desktop/ShellViewModel.cs`
- Modify: `src/WebHtv.Desktop/MovieDetailWindow.xaml`
- Modify: `src/WebHtv.Desktop/MovieDetailWindow.xaml.cs`

**Interfaces:**
- Produces: `SearchAsync(keyword, enabledSites, cancellationToken)` and `ProbeAsync(title, candidates, cancellationToken)`.
- Produces: `PlaybackInterfaceResult(Name, RuntimeKey, Context, ProbeLatency)` only for valid interfaces.

- [ ] **Step 1: Write dedupe, concurrency and failure-filter tests**

```csharp
[Fact]
public async Task Probe_returns_only_matching_reachable_interfaces_with_bounded_concurrency()
{
    var fake = new ProbeFake(maximumObservedConcurrency: out var observed);
    var service = new PlaybackInterfaceProbe(fake, maxConcurrency: 6, timeout: TimeSpan.FromSeconds(6));
    var result = await service.ProbeAsync("云边有个小卖部", Fixtures.FortyEightSites(), CancellationToken.None);
    Assert.All(result, item => Assert.True(item.Context.Detail.Sources.SelectMany(x => x.Episodes).Any()));
    Assert.True(observed() <= 6);
    Assert.DoesNotContain(result, item => item.Name == "403接口");
}
```

- [ ] **Step 2: Run catalogue aggregation tests**

Run: `dotnet test .\tests\MTPlayer.Client.Core.Tests\MTPlayer.Client.Core.Tests.csproj --filter Catalogue`

Expected: FAIL because aggregation and probe services do not exist.

- [ ] **Step 3: Implement normalized-title matching and cache**

Normalize with Unicode Form KC, remove punctuation/symbol/whitespace, uppercase invariant, and compare exact title first. Use year when both sides provide one. Cache positive results 15 minutes and negative network failures 2 minutes; clear on configuration refresh. Dispose every response after headers and support cancellation when the detail window closes.

- [ ] **Step 4: Wire WPF search/detail behavior**

Set `ShowTopLists = false` before search starts. Present interface selector before line selector; selector text is `TvBoxSite.Name`. Hide failed options from the selector and show a collapsible diagnostic list instead of message boxes. Make the episode region vertically scrollable and at least 280 device-independent pixels tall.

Run:

```powershell
dotnet test .\tests\MTPlayer.Client.Core.Tests\MTPlayer.Client.Core.Tests.csproj --filter Catalogue
dotnet build .\src\WebHtv.Desktop\WebHtv.Desktop.csproj -c Debug -p:Platform=x64
```

Expected: all catalogue tests PASS; no `PlaybackInterfaceOption { ... }` text appears in UI snapshots.

- [ ] **Step 5: Commit**

```powershell
git add src/MTPlayer.Client.Core/Catalogue tests/MTPlayer.Client.Core.Tests/Catalogue src/WebHtv.Desktop/ShellViewModel.cs src/WebHtv.Desktop/MovieDetailWindow*
git commit -m "feat(windows): harden search and interface discovery"
```

### Task 6: 播放解析会话与 Spider 隔离

**Files:**
- Create: `src/MTPlayer.Client.Core/Playback/PlaybackSessionResolver.cs`
- Create: `src/MTPlayer.Client.Core/Playback/ParserModels.cs`
- Create: `src/WebHtv.SpiderBridge/MTPlayer.SpiderBridge.csproj`
- Create: `src/WebHtv.SpiderBridge/Program.cs`
- Create: `src/WebHtv.Spider/JarSpiderBridgeClient.cs`
- Create: `tests/MTPlayer.Client.Core.Tests/Playback/PlaybackSessionResolverTests.cs`
- Modify: `src/WebHtv.Spider/JintSpiderProvider.cs`
- Modify: `src/WebHtv.Desktop/ParserResolver.cs`

**Interfaces:**
- Produces: `ResolvedPlayback(Uri MediaUri, IReadOnlyDictionary<string,string> Headers, IReadOnlyList<SubtitleTrack> Subtitles)`.
- Produces: JSON-lines bridge methods `init`, `search`, `detail`, `player`, `dispose` with request IDs.

- [ ] **Step 1: Write resolver fallback and timeout tests**

```csharp
[Fact]
public async Task Resolver_uses_direct_then_json_then_web_and_cancels_later_fallbacks()
{
    var direct = new FakeResolver(null);
    var json = new FakeResolver(new ResolvedPlayback(new Uri("https://media.example/a.m3u8"), new Dictionary<string, string>(), []));
    var web = new FakeResolver(Fail.IfCalled<ResolvedPlayback>());
    var result = await new PlaybackSessionResolver([direct, json, web]).ResolveAsync(Fixtures.PlayRequest(), CancellationToken.None);
    Assert.Equal("media.example", result!.MediaUri.Host);
    Assert.Equal(0, web.CallCount);
}
```

- [ ] **Step 2: Run playback resolver tests**

Run: `dotnet test .\tests\MTPlayer.Client.Core.Tests\MTPlayer.Client.Core.Tests.csproj --filter PlaybackSessionResolverTests`

Expected: FAIL because the resolver does not exist.

- [ ] **Step 3: Implement ordered resolver chain and JS limits**

Resolver order is direct, JSON parser, JS player function, JAR bridge, background WebView. Give each fallback its own 15-second budget and a 45-second total budget. Jint must cap statements, recursion depth and timeout; expose only approved HTTP functions and JSON utilities. Return typed error codes `timeout`, `forbidden`, `invalid_response`, `plugin_crashed`, `unsupported_android_api`.

- [ ] **Step 4: Implement the process bridge and crash isolation test**

Use one JSON request and response per UTF-8 line, include `requestId`, reject messages over 2 MiB, and kill/restart the helper after timeout or malformed output. The single-file app extracts the compressed runtime under `%LocalAppData%\MTPlayer\runtime\{version}` only when a JAR source is used.

Run: `dotnet test .\tests\MTPlayer.Client.Core.Tests\MTPlayer.Client.Core.Tests.csproj --filter "Playback|SpiderBridge"`

Expected: PASS; a deliberately crashing plugin produces `plugin_crashed` without terminating the test host.

- [ ] **Step 5: Commit**

```powershell
git add src/MTPlayer.Client.Core/Playback src/WebHtv.SpiderBridge src/WebHtv.Spider tests/MTPlayer.Client.Core.Tests/Playback src/WebHtv.Desktop/ParserResolver.cs
git commit -m "feat(windows): add isolated playback resolution"
```

### Task 7: LibVLC 播放器状态、片头片尾和现代控制栏

**Files:**
- Create: `src/MTPlayer.Client.Core/Playback/PlayerStateMachine.cs`
- Create: `tests/MTPlayer.Client.Core.Tests/Playback/PlayerStateMachineTests.cs`
- Modify: `src/WebHtv.Playback/NativePlaybackService.cs`
- Modify: `src/WebHtv.Desktop/PlayerWindow.xaml`
- Modify: `src/WebHtv.Desktop/PlayerWindow.xaml.cs`
- Modify: `src/WebHtv.Desktop/Themes/DarkControls.xaml`

**Interfaces:**
- Produces: deterministic player commands `Play`, `Pause`, `Seek`, `Previous`, `Next`, `SetSpeed`, `ToggleMute`, `ToggleFullscreen`.
- Consumes: `SkipMarkerRecord` with intro seconds and outro remaining seconds.

- [ ] **Step 1: Write skip and control-visibility tests**

```csharp
[Fact]
public void Outro_triggers_once_when_remaining_time_crosses_marker()
{
    var state = new PlayerStateMachine(TimeSpan.FromSeconds(5));
    state.SetSkipMarker(introEndSeconds: 60, outroRemainingSeconds: 90);
    Assert.False(state.Tick(positionMs: 400_000, durationMs: 600_000).SkipToNext);
    Assert.True(state.Tick(positionMs: 511_000, durationMs: 600_000).SkipToNext);
    Assert.False(state.Tick(positionMs: 512_000, durationMs: 600_000).SkipToNext);
}
```

- [ ] **Step 2: Run player-state tests**

Run: `dotnet test .\tests\MTPlayer.Client.Core.Tests\MTPlayer.Client.Core.Tests.csproj --filter PlayerStateMachineTests`

Expected: FAIL because the state machine does not exist.

- [ ] **Step 3: Implement state machine and LibVLC track APIs**

Expose audio tracks, subtitle tracks, mute, volume, rate, seek and hardware-decode fallback from `NativePlaybackService`. On hardware open failure recreate LibVLC with `--avcodec-hw=none` once. Save progress every 15 seconds, pause, episode change and close.

- [ ] **Step 4: Replace native controls with themed templates**

Use named commands and labels: “播放速度”, “静音/取消静音”, “上一集”, “下一集”, “设置片头”, “设置片尾”, “清除跳过”, “全屏”. Slider thumbs must be 16×16 and not clipped. Controls are in a rounded translucent overlay and collapse after exactly 5 seconds without mouse/keyboard activity; any input restores them.

Run:

```powershell
dotnet test .\tests\MTPlayer.Client.Core.Tests\MTPlayer.Client.Core.Tests.csproj --filter Playback
dotnet build .\src\WebHtv.Desktop\WebHtv.Desktop.csproj -c Release -p:Platform=x64
```

Expected: tests PASS; build has no WPF binding warnings for player controls.

- [ ] **Step 5: Commit**

```powershell
git add src/MTPlayer.Client.Core/Playback src/WebHtv.Playback src/WebHtv.Desktop/PlayerWindow* src/WebHtv.Desktop/Themes tests/MTPlayer.Client.Core.Tests/Playback
git commit -m "feat(windows): complete native player controls"
```

### Task 8: 账号、同步状态和服务器配置界面

**Files:**
- Create: `src/WebHtv.Desktop/Account/AccountViewModel.cs`
- Create: `src/WebHtv.Desktop/Account/AccountView.xaml`
- Create: `src/WebHtv.Desktop/Account/AccountView.xaml.cs`
- Create: `src/WebHtv.Desktop/Account/ServerConfigImporter.cs`
- Modify: `src/WebHtv.Desktop/MainWindow.xaml`
- Modify: `src/WebHtv.Desktop/MainWindow.xaml.cs`
- Modify: `src/WebHtv.Desktop/ShellViewModel.cs`

**Interfaces:**
- Produces: account states `Guest`, `SignedIn`, `Unverified`, `Offline`, `Syncing`, `SyncError`.
- Consumes: account client, token store and sync engine from Tasks 2–3.

- [ ] **Step 1: Write server-config import tests**

```csharp
[Fact]
public void Import_accepts_signed_mtplayer_config_and_rejects_http_production_url()
{
    var valid = ServerConfigImporter.Parse("""{"version":1,"serverUrl":"https://sync.example.com"}""", false);
    Assert.Equal("https://sync.example.com/", valid.BaseUri.AbsoluteUri);
    Assert.Throws<InvalidDataException>(() => ServerConfigImporter.Parse("""{"version":1,"serverUrl":"http://sync.example.com"}""", false));
}
```

- [ ] **Step 2: Run account UI support tests**

Run: `dotnet test .\tests\MTPlayer.Client.Core.Tests\MTPlayer.Client.Core.Tests.csproj --filter ServerConfig`

Expected: FAIL because importer does not exist.

- [ ] **Step 3: Implement account view model and commands**

Provide `BindServerCommand`, `RegisterCommand`, `VerifyCommand`, `LoginCommand`, `LogoutCommand`, `ResendVerificationCommand`, `RefreshDevicesCommand`, `RevokeDeviceCommand`, `SyncNowCommand`. Logout clears tokens and stops scheduling sync but leaves local data untouched.

- [ ] **Step 4: Add WPF account page and sync indicator**

Add “账户” under “关于软件”. The page contains server address, import configuration, register/login forms, verification state, device list and last sync result. Display a small non-blocking status indicator in the shell; never use modal information boxes for normal offline state.

Run: `dotnet build .\src\WebHtv.Desktop\WebHtv.Desktop.csproj -c Debug -p:Platform=x64`

Expected: build succeeds and account navigation resolves all bindings.

- [ ] **Step 5: Commit**

```powershell
git add src/WebHtv.Desktop/Account src/WebHtv.Desktop/MainWindow* src/WebHtv.Desktop/ShellViewModel.cs tests/MTPlayer.Client.Core.Tests
git commit -m "feat(windows): add account and sync UI"
```

### Task 9: 直播、品牌、无障碍与 Windows 验收

**Files:**
- Create: `tests/MTPlayer.Windows.Acceptance/MTPlayer.Windows.Acceptance.csproj`
- Create: `tests/MTPlayer.Windows.Acceptance/WindowsAcceptanceTests.cs`
- Modify: `src/WebHtv.Desktop/LivePlaylistService.cs`
- Modify: `src/WebHtv.Desktop/MainWindow.xaml`
- Modify: `src/WebHtv.Desktop/Themes/DarkControls.xaml`
- Modify: `src/WebHtv.Desktop/WebHtv.Desktop.csproj`
- Modify: `installer/MTPlayer.iss`
- Modify: `build/build-release.ps1`

**Interfaces:**
- Produces: x64/x86 single-file and x64/x86 installers using the same MT icon.
- Consumes: all prior Windows tasks.

- [ ] **Step 1: Write live matching and navigation acceptance tests**

```csharp
[Fact]
public async Task Xmltv_matches_tvg_id_before_normalized_channel_name()
{
    var channels = new[] { new LiveChannel("央视", "CCTV-1 综合", "https://media.example/live.m3u8", new Dictionary<string, string>(), ChannelId: "cctv1") };
    var result = await Fixtures.LiveService.EnrichWithEpgAsync(channels, [Fixtures.XmlTv("cctv1", "新闻联播")]);
    Assert.Equal("新闻联播", result.Single().NowPlaying);
}
```

- [ ] **Step 2: Run acceptance tests**

Run: `dotnet test .\tests\MTPlayer.Windows.Acceptance\MTPlayer.Windows.Acceptance.csproj`

Expected: FAIL until live matching and application harness are wired.

- [ ] **Step 3: Complete live import and themed navigation**

Match XMLTV by exact `tvg-id`, then normalized name. Add channel group, favorites, recent channels and retry/alternate address. Ensure the horizontal and vertical scrollbars use the MT theme. Apply automation names and keyboard focus visuals to navigation, poster cards, selectors, episode buttons and player controls.

- [ ] **Step 4: Build and smoke-test four Windows artifacts**

Run:

```powershell
dotnet test .\tests\MTPlayer.Client.Core.Tests\MTPlayer.Client.Core.Tests.csproj
dotnet test .\tests\MTPlayer.Windows.Acceptance\MTPlayer.Windows.Acceptance.csproj
powershell -ExecutionPolicy Bypass -File .\build\build-release.ps1 -Version 2.0.0
```

Expected: PASS; outputs include `MT播放器-x64.exe`, `MT播放器-x86.exe`, `MT播放器-Setup-x64.exe`, `MT播放器-Setup-x86.exe`; version info and shortcuts use `mtplayer.ico`; clean-install smoke test can navigate every left item and open a poster without a modal placeholder.

- [ ] **Step 5: Commit**

```powershell
git add src/WebHtv.Desktop src/WebHtv.Playback installer build tests/MTPlayer.Windows.Acceptance
git commit -m "feat(windows): complete Windows 2.0 acceptance"
```
