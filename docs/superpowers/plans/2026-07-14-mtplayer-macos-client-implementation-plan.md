# MT播放器 macOS 客户端 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 交付支持 Intel 与 Apple Silicon 的 MT播放器 macOS 桌面客户端及网站 DMG，功能覆盖配置、搜索、详情、播放、收藏、历史、直播、账号和同步。

**Architecture:** Avalonia 桌面 UI 复用 .NET 配置、目录、Spider、播放会话和同步核心；平台层提供 Keychain、WKWebView 解析桥、菜单、文件选择和 macOS 全屏。两个 RID 分别发布后在 macOS CI 合并为 Universal App，再签名、公证和制作 DMG。

**Tech Stack:** .NET SDK 8.0.422、Avalonia 11.3.10、LibVLCSharp.Avalonia 3.10.0、VideoLAN.LibVLC.Mac、AppKit/WebKit Swift bridge、xUnit、macOS 12+

## Global Constraints

- 支持 macOS 12 及以上、Intel x64 与 Apple Silicon arm64。
- 官网正式 DMG 必须使用 Apple Developer ID 签名并完成公证；没有凭据的产物只标记为未签名测试版。
- 产品界面为原生桌面 UI，WKWebView 只用于后台解析。
- 游客可完整本地播放；账号和服务器离线不阻塞本地功能。
- 生产服务器绑定只接受 HTTPS，不硬编码域名或内部端口。
- 横版 Logo 用于侧栏/登录/关于页；圆角 MT 图标用于 App、Dock 和 DMG。
- 服务端计划与 Windows 计划中的 `MTPlayer.Client.Core` 必须先完成。
- 代码片段中的 `MacFixtures`、`BundleFixture`、`FakeMacPlatformServices` 等测试辅助类型，均作为当前任务所列测试文件中的内部辅助类型一并创建。

---

### Task 1: 创建 Avalonia macOS 应用与平台抽象

**Files:**
- Create: `src/MTPlayer.Mac/MTPlayer.Mac.csproj`
- Create: `src/MTPlayer.Mac/Program.cs`
- Create: `src/MTPlayer.Mac/App.axaml`
- Create: `src/MTPlayer.Mac/App.axaml.cs`
- Create: `src/MTPlayer.Mac/Platform/IMacPlatformServices.cs`
- Create: `src/MTPlayer.Mac/Platform/MacPlatformServices.cs`
- Create: `tests/MTPlayer.Mac.Tests/MTPlayer.Mac.Tests.csproj`
- Create: `tests/MTPlayer.Mac.Tests/AppStartupTests.cs`
- Modify: `WebHtv.Windows.sln`

**Interfaces:**
- Produces: `IMacPlatformServices` for app-data path, secure token store, URL open, file picker and full-screen command.
- Consumes: `MTPlayer.Client.Core`, `WebHtv.Core`, `WebHtv.Configuration`, `WebHtv.Catalogue`, `WebHtv.Spider`.

- [ ] **Step 1: Write headless startup test**

```csharp
[Fact]
public void App_constructs_with_test_platform_services()
{
    using var app = new App(new FakeMacPlatformServices());
    Assert.NotNull(app);
}
```

- [ ] **Step 2: Run the startup test**

Run: `dotnet test .\tests\MTPlayer.Mac.Tests\MTPlayer.Mac.Tests.csproj --filter App_constructs`

Expected: FAIL because macOS projects do not exist.

- [ ] **Step 3: Create projects and platform interface**

```csharp
public interface IMacPlatformServices
{
    string ApplicationDataDirectory { get; }
    ITokenStore TokenStore { get; }
    Task<string?> PickFileAsync(IReadOnlyList<string> extensions, CancellationToken cancellationToken);
    void OpenExternal(Uri uri);
    void SetFullscreen(Window window, bool fullscreen);
}
```

Target `net8.0`, set runtime identifiers `osx-x64;osx-arm64`, include Avalonia desktop/theme packages and reference all reusable .NET projects. Keep App constructor injectable for headless tests.

- [ ] **Step 4: Add projects and build from Windows**

Run:

```powershell
dotnet sln .\WebHtv.Windows.sln add .\src\MTPlayer.Mac\MTPlayer.Mac.csproj .\tests\MTPlayer.Mac.Tests\MTPlayer.Mac.Tests.csproj
dotnet test .\tests\MTPlayer.Mac.Tests\MTPlayer.Mac.Tests.csproj
dotnet publish .\src\MTPlayer.Mac\MTPlayer.Mac.csproj -c Release -r osx-arm64 --self-contained true -o .\artifacts\macos-arm64
```

Expected: tests PASS and cross-publish completes; running and packaging remain macOS CI steps.

- [ ] **Step 5: Commit**

```powershell
git add WebHtv.Windows.sln src/MTPlayer.Mac tests/MTPlayer.Mac.Tests
git commit -m "feat(macos): add Avalonia application shell"
```

### Task 2: Keychain、服务器绑定与本地数据路径

**Files:**
- Create: `src/MTPlayer.Mac/Platform/MacKeychainTokenStore.cs`
- Create: `src/MTPlayer.Mac/Account/MacAccountService.cs`
- Create: `tests/MTPlayer.Mac.Tests/Platform/MacKeychainTokenStoreTests.cs`
- Create: `tests/MTPlayer.Mac.Tests/Account/MacAccountServiceTests.cs`
- Modify: `src/MTPlayer.Mac/Platform/MacPlatformServices.cs`

**Interfaces:**
- Produces: Keychain service `cn.mtplayer.desktop`, account `refresh-token`.
- Consumes: shared `ServerBinding`, `IAccountApiClient`, `SyncEngine`.

- [ ] **Step 1: Write binding and logout tests**

```csharp
[Fact]
public async Task Logout_removes_keychain_token_but_keeps_local_library()
{
    var fixture = MacAccountFixture.Create();
    await fixture.Service.LoginAsync("user@example.com", "Password-2026");
    await fixture.Service.LogoutAsync();
    Assert.Null(await fixture.TokenStore.LoadRefreshTokenAsync());
    Assert.Single((await fixture.Library.LoadAsync()).Favorites);
}
```

- [ ] **Step 2: Run platform account tests**

Run: `dotnet test .\tests\MTPlayer.Mac.Tests\MTPlayer.Mac.Tests.csproj --filter "Keychain|MacAccount"`

Expected: FAIL because Keychain and account services do not exist.

- [ ] **Step 3: Implement Security.framework wrapper**

Use `SecItemAdd`, `SecItemCopyMatching`, `SecItemUpdate`, `SecItemDelete` through a small native interop file. Store only refresh token bytes, request `kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly`, map `errSecItemNotFound` to null and never log token contents.

Use `~/Library/Application Support/MTPlayer` for settings, library, configurations, cache, logs and sync queue. Do not write user data into the App bundle.

- [ ] **Step 4: Run tests on Windows fakes and macOS CI**

Run locally: `dotnet test .\tests\MTPlayer.Mac.Tests\MTPlayer.Mac.Tests.csproj --filter MacAccount`

Run on macOS CI: `dotnet test ./tests/MTPlayer.Mac.Tests/MTPlayer.Mac.Tests.csproj --filter MacKeychain`

Expected: account fake tests PASS locally; real Keychain create/read/delete tests PASS on macOS.

- [ ] **Step 5: Commit**

```powershell
git add src/MTPlayer.Mac/Platform src/MTPlayer.Mac/Account tests/MTPlayer.Mac.Tests/Platform tests/MTPlayer.Mac.Tests/Account
git commit -m "feat(macos): add Keychain account storage"
```

### Task 3: 主窗口、导航、首页与搜索

**Files:**
- Create: `src/MTPlayer.Mac/Shell/MainWindow.axaml`
- Create: `src/MTPlayer.Mac/Shell/MainWindow.axaml.cs`
- Create: `src/MTPlayer.Mac/Shell/ShellViewModel.cs`
- Create: `src/MTPlayer.Mac/Home/HomeView.axaml`
- Create: `src/MTPlayer.Mac/Home/HomeViewModel.cs`
- Create: `src/MTPlayer.Mac/Search/SearchView.axaml`
- Create: `src/MTPlayer.Mac/Search/SearchViewModel.cs`
- Create: `src/MTPlayer.Mac/Styles/MTTheme.axaml`
- Create: `tests/MTPlayer.Mac.Tests/Shell/ShellViewModelTests.cs`

**Interfaces:**
- Produces: routes `home`, `search`, `library`, `live`, `settings`, `about`, `account`.
- Consumes: shared configuration, catalogue, local library and account services.

- [ ] **Step 1: Write home/search state test**

```csharp
[Fact]
public async Task Search_hides_top_lists_and_cancel_restores_home()
{
    var vm = MacFixtures.Shell();
    await vm.Search.ExecuteAsync("仙逆");
    Assert.False(vm.Home.ShowTopLists);
    Assert.NotEmpty(vm.SearchResults);
    vm.CancelSearch.Execute(null);
    Assert.True(vm.Home.ShowTopLists);
}
```

- [ ] **Step 2: Run shell tests**

Run: `dotnet test .\tests\MTPlayer.Mac.Tests\MTPlayer.Mac.Tests.csproj --filter ShellViewModelTests`

Expected: FAIL because shell and view models do not exist.

- [ ] **Step 3: Implement desktop navigation and state**

Sidebar order is 首页、搜索、我的收藏、观看记录、直播频道、设置、关于软件、账户. Home shows continue watching and five Top 10 rows. Use `Cmd+K` for search, `Cmd+,` for settings and `Ctrl+Cmd+F` for full screen. Cancel search restores Top 10; errors appear inline.

- [ ] **Step 4: Implement MT theme and run headless view tests**

Create dark red/black resources for buttons, combo boxes, scrollbars, sliders, poster hover/focus and validation. Horizontal Logo occupies the sidebar header without a mismatched background. Poster hover translates Y by -4 and scales to 1.02; no white overlay.

Run: `dotnet test .\tests\MTPlayer.Mac.Tests\MTPlayer.Mac.Tests.csproj --filter "Shell|Home|Search"`

Expected: PASS; headless Avalonia loader resolves all styles and bindings.

- [ ] **Step 5: Commit**

```powershell
git add src/MTPlayer.Mac/Shell src/MTPlayer.Mac/Home src/MTPlayer.Mac/Search src/MTPlayer.Mac/Styles tests/MTPlayer.Mac.Tests/Shell
git commit -m "feat(macos): add desktop shell and catalogue UI"
```

### Task 4: 详情、有效接口、剧集与媒体资料库

**Files:**
- Create: `src/MTPlayer.Mac/Detail/DetailView.axaml`
- Create: `src/MTPlayer.Mac/Detail/DetailViewModel.cs`
- Create: `src/MTPlayer.Mac/Library/LibraryView.axaml`
- Create: `src/MTPlayer.Mac/Library/LibraryViewModel.cs`
- Create: `tests/MTPlayer.Mac.Tests/Detail/DetailViewModelTests.cs`
- Create: `tests/MTPlayer.Mac.Tests/Library/LibraryViewModelTests.cs`

**Interfaces:**
- Produces: `AvailableInterfaces`, `SelectedInterface`, `Lines`, `SelectedLine`, `Episodes`, `SelectedEpisode`.
- Consumes: shared `PlaybackInterfaceProbe`, `ILibraryStore`.

- [ ] **Step 1: Write interface ordering and favorite tests**

```csharp
[Fact]
public async Task Detail_exposes_only_probe_successes_in_interface_line_episode_order()
{
    var vm = MacFixtures.Detail(probeResults: [Fixtures.Reachable("三六资源"), Fixtures.Failed("403资源")]);
    await vm.LoadAsync();
    Assert.Equal(["三六资源"], vm.AvailableInterfaces.Select(x => x.Name));
    Assert.NotEmpty(vm.Lines);
    Assert.NotEmpty(vm.Episodes);
}
```

- [ ] **Step 2: Run detail/library tests**

Run: `dotnet test .\tests\MTPlayer.Mac.Tests\MTPlayer.Mac.Tests.csproj --filter "Detail|Library"`

Expected: FAIL because views and view models do not exist.

- [ ] **Step 3: Implement detail state and cancellation**

Cancel the previous probe when interface selection or window navigation changes. Show poster, metadata, description, interface first, line second and a vertically scrollable episode grid at least 320 px tall. Double-click episode or use the main play button. Failed interfaces appear only in a collapsed diagnostic panel.

- [ ] **Step 4: Implement favorites/history/continue-watching**

Library uses shared local records, supports clear history with confirmation, favorite removal, resume and sync-state badges. No modal placeholders are allowed.

Run: `dotnet test .\tests\MTPlayer.Mac.Tests\MTPlayer.Mac.Tests.csproj --filter "Detail|Library"`

Expected: PASS for cancellation, interface filtering, favorite toggle, history clear and resume selection.

- [ ] **Step 5: Commit**

```powershell
git add src/MTPlayer.Mac/Detail src/MTPlayer.Mac/Library tests/MTPlayer.Mac.Tests/Detail tests/MTPlayer.Mac.Tests/Library
git commit -m "feat(macos): add detail and library views"
```

### Task 5: LibVLC 播放器与 macOS 控制体验

**Files:**
- Create: `src/MTPlayer.Mac/Playback/MacPlaybackService.cs`
- Create: `src/MTPlayer.Mac/Playback/PlayerWindow.axaml`
- Create: `src/MTPlayer.Mac/Playback/PlayerViewModel.cs`
- Create: `tests/MTPlayer.Mac.Tests/Playback/PlayerViewModelTests.cs`
- Modify: `src/MTPlayer.Mac/MTPlayer.Mac.csproj`

**Interfaces:**
- Produces: LibVLC-backed player implementing the shared player service contract.
- Consumes: resolved media, progress store, skip markers and macOS full-screen service.

- [ ] **Step 1: Write player state tests**

```csharp
[Fact]
public async Task Player_saves_progress_on_pause_episode_change_and_close()
{
    var fixture = MacFixtures.Player();
    await fixture.ViewModel.OpenAsync(Fixtures.Episode(1));
    fixture.Engine.PositionMs = 42_000;
    await fixture.ViewModel.PauseAsync();
    await fixture.ViewModel.SelectEpisodeAsync(2);
    await fixture.ViewModel.CloseAsync();
    Assert.Equal(3, fixture.ProgressStore.SaveCalls);
}
```

- [ ] **Step 2: Run playback tests**

Run: `dotnet test .\tests\MTPlayer.Mac.Tests\MTPlayer.Mac.Tests.csproj --filter PlayerViewModelTests`

Expected: FAIL because player types do not exist.

- [ ] **Step 3: Implement LibVLC service and controls**

Apply User-Agent, Referer, Cookie and custom headers. Expose play/pause, ±10 seconds, seek, previous/next, volume/mute, speed, subtitles, audio tracks and full screen. Retry once with hardware decoding disabled after decoder/open failure. Save progress every 15 seconds and lifecycle events.

- [ ] **Step 4: Implement skip settings and 5-second overlay**

Store intro seconds and outro remaining seconds per content/interface/line. The rounded translucent control overlay collapses after exactly 5 seconds without mouse/keyboard input and returns on movement/click/key. Show complete slider thumbs and labels “音量” and “播放速度”.

Run: `dotnet test .\tests\MTPlayer.Mac.Tests\MTPlayer.Mac.Tests.csproj --filter Playback`

Expected: PASS for progress, next episode, mute restore, speed, one-time skip and overlay timer.

- [ ] **Step 5: Commit**

```powershell
git add src/MTPlayer.Mac/Playback src/MTPlayer.Mac/MTPlayer.Mac.csproj tests/MTPlayer.Mac.Tests/Playback
git commit -m "feat(macos): add native LibVLC player"
```

### Task 6: WKWebView 解析桥与 JAR 兼容进程

**Files:**
- Create: `native/macos/MTPlayer.WebParserBridge/Package.swift`
- Create: `native/macos/MTPlayer.WebParserBridge/Sources/main.swift`
- Create: `src/MTPlayer.Mac/Parsing/MacWebParserClient.cs`
- Create: `src/MTPlayer.Mac/Parsing/MacJarSpiderBridgeClient.cs`
- Create: `tests/MTPlayer.Mac.Tests/Parsing/ParserBridgeTests.cs`

**Interfaces:**
- Produces: JSON-lines commands `open`, `waitForMedia`, `cancel`, `shutdown` for WebKit.
- Consumes: shared resolver chain and desktop `MTPlayer.SpiderBridge` protocol.

- [ ] **Step 1: Write malformed-output and timeout tests**

```csharp
[Fact]
public async Task Parser_bridge_timeout_kills_helper_and_returns_typed_error()
{
    var process = new HangingBridgeProcess();
    var client = new MacWebParserClient(process, TimeSpan.FromMilliseconds(50));
    var result = await client.ResolveAsync(new Uri("https://parser.example/?url=x"), CancellationToken.None);
    Assert.Equal(ParserErrorCode.Timeout, result.ErrorCode);
    Assert.True(process.Killed);
}
```

- [ ] **Step 2: Run parser bridge tests**

Run: `dotnet test .\tests\MTPlayer.Mac.Tests\MTPlayer.Mac.Tests.csproj --filter ParserBridgeTests`

Expected: FAIL because clients do not exist.

- [ ] **Step 3: Implement Swift WKWebView helper**

Create a headless `WKWebView`, apply headers, observe navigation/resource URLs, return the first HTTP(S) URL matching HLS/DASH/media extensions or JavaScript player result, enforce 30-second timeout, and never expose a visible browser window. Limit input/output lines to 2 MiB and include request IDs.

- [ ] **Step 4: Package helpers and run bridge integration on macOS CI**

Bundle Swift and JAR helpers under `MT播放器.app/Contents/Helpers`, sign them with the same Developer ID and use `Process` with redirected UTF-8 streams. Run on macOS:

```bash
swift test --package-path native/macos/MTPlayer.WebParserBridge
dotnet test tests/MTPlayer.Mac.Tests/MTPlayer.Mac.Tests.csproj --filter ParserBridge
```

Expected: PASS for direct media capture, timeout, cancellation, malformed output and helper crash.

- [ ] **Step 5: Commit**

```powershell
git add native/macos/MTPlayer.WebParserBridge src/MTPlayer.Mac/Parsing tests/MTPlayer.Mac.Tests/Parsing
git commit -m "feat(macos): add isolated parser bridges"
```

### Task 7: 直播、设置、关于与账户同步页面

**Files:**
- Create: `src/MTPlayer.Mac/Live/LiveView.axaml`
- Create: `src/MTPlayer.Mac/Live/LiveViewModel.cs`
- Create: `src/MTPlayer.Mac/Settings/SettingsView.axaml`
- Create: `src/MTPlayer.Mac/Settings/SettingsViewModel.cs`
- Create: `src/MTPlayer.Mac/Account/AccountView.axaml`
- Create: `src/MTPlayer.Mac/Account/AccountViewModel.cs`
- Create: `src/MTPlayer.Mac/About/AboutView.axaml`
- Create: `tests/MTPlayer.Mac.Tests/FeaturePageTests.cs`

**Interfaces:**
- Produces: all remaining sidebar destinations without placeholders.
- Consumes: shared live repository, settings, account and sync engine.

- [ ] **Step 1: Write feature-page command tests**

```csharp
[Fact]
public async Task Every_sidebar_page_loads_real_state_and_no_placeholder_message()
{
    var shell = MacFixtures.Shell();
    foreach (var route in new[] { "live", "settings", "about", "account" })
    {
        await shell.Navigate.ExecuteAsync(route);
        Assert.DoesNotContain("正在接入", shell.CurrentPage.AccessibleText, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run feature page tests**

Run: `dotnet test .\tests\MTPlayer.Mac.Tests\MTPlayer.Mac.Tests.csproj --filter FeaturePageTests`

Expected: FAIL because the pages do not exist.

- [ ] **Step 3: Implement live/settings/about pages**

Live supports M3U/M3U8/TXT, XMLTV, groups, logo, current/next program, favorites and retry. Settings contains multi-source management, parser switches, player defaults, cover settings and server binding. About shows horizontal Logo, version, open-source notices and approved disclaimer.

- [ ] **Step 4: Implement account/devices/sync page and run tests**

Provide register, verify, login, resend, logout, device revoke, sync-now and status. Logout leaves local data. Run:

```powershell
dotnet test .\tests\MTPlayer.Mac.Tests\MTPlayer.Mac.Tests.csproj
dotnet publish .\src\MTPlayer.Mac\MTPlayer.Mac.csproj -c Release -r osx-x64 --self-contained true -o .\artifacts\macos-x64
```

Expected: all macOS headless tests PASS and x64 cross-publish succeeds.

- [ ] **Step 5: Commit**

```powershell
git add src/MTPlayer.Mac/Live src/MTPlayer.Mac/Settings src/MTPlayer.Mac/Account src/MTPlayer.Mac/About tests/MTPlayer.Mac.Tests/FeaturePageTests.cs
git commit -m "feat(macos): complete feature pages"
```

### Task 8: Universal App、签名、公证与 DMG

**Files:**
- Create: `build/macos/build-universal.sh`
- Create: `build/macos/entitlements.plist`
- Create: `build/macos/create-dmg.applescript`
- Create: `.github/workflows/build-macos.yml`
- Create: `docs/release/macos-signing.zh-CN.md`
- Create: `tests/MTPlayer.Mac.Tests/Packaging/BundleContractTests.cs`

**Interfaces:**
- Produces: `D:\work\MTPlayer\release\macos\MT播放器-universal.dmg` when copied from CI.
- Consumes: Apple secrets `APPLE_CERTIFICATE_P12`, `APPLE_CERTIFICATE_PASSWORD`, `APPLE_SIGNING_IDENTITY`, `APPLE_ID`, `APPLE_TEAM_ID`, `APPLE_APP_PASSWORD`.

- [ ] **Step 1: Write bundle contract test**

```csharp
[Fact]
public void Bundle_has_identifier_icon_helpers_and_privacy_strings()
{
    var plist = BundleFixture.ReadInfoPlist();
    Assert.Equal("cn.mtplayer.desktop", plist.BundleIdentifier);
    Assert.Equal("MTPlayer", plist.IconFile);
    Assert.True(BundleFixture.Exists("Contents/Helpers/MTPlayer.WebParserBridge"));
}
```

- [ ] **Step 2: Run packaging test before bundle exists**

Run on macOS: `dotnet test tests/MTPlayer.Mac.Tests/MTPlayer.Mac.Tests.csproj --filter BundleContract`

Expected: FAIL because the App bundle has not been assembled.

- [ ] **Step 3: Implement universal merge and architecture validation**

Publish `osx-x64` and `osx-arm64`. Create a bundle template, use `lipo -create` for every matching Mach-O executable/dylib, copy architecture-neutral resources once, and fail when a native file exists in only one RID unless explicitly listed as architecture-neutral. Verify main executable and LibVLC dylibs report both `x86_64 arm64`.

- [ ] **Step 4: Sign, notarize, staple, build DMG and test**

Sign nested helpers/libraries first, then App with hardened runtime. Use `notarytool submit --wait`, `stapler staple`, build branded DMG, sign DMG, verify with `codesign --verify --deep --strict`, `spctl --assess --type execute` and `stapler validate`.

When Apple secrets are absent, workflow emits `MT播放器-universal-UNSIGNED-TEST.dmg` and fails the official-release job; it must never rename an unsigned artifact to the official filename.

- [ ] **Step 5: Run macOS acceptance and commit**

Run on macOS CI:

```bash
dotnet test tests/MTPlayer.Mac.Tests/MTPlayer.Mac.Tests.csproj
bash build/macos/build-universal.sh 2.0.0
file artifacts/macos/MT播放器.app/Contents/MacOS/MTPlayer
```

Expected: tests PASS; `file` reports universal `x86_64` and `arm64`; signed job produces a notarized DMG.

```powershell
git add build/macos .github/workflows/build-macos.yml docs/release/macos-signing.zh-CN.md tests/MTPlayer.Mac.Tests/Packaging
git commit -m "feat(macos): add universal notarized DMG pipeline"
```
