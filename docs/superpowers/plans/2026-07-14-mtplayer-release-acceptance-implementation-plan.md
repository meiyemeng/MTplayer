# MT播放器发布与跨端验收 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把服务端、Windows、Android 手机、Android TV 和 macOS 构建成果整合为可验证、可安装、可卸载、带免责声明与源码的正式发布包。

**Architecture:** 使用无版权风险的本地模拟 CMS、解析器、HLS、M3U 和 XMLTV 组成端到端测试环境，分别验证客户端本地播放与跨设备同步。统一构建脚本只收集已经通过平台验收的签名产物，并生成校验和、版本清单和完整源码压缩包。

**Tech Stack:** PowerShell 7、Docker Compose、ASP.NET 测试媒体服务、xUnit、Android Emulator、Windows UI Automation、GitHub Actions macOS、SHA-256

## Global Constraints

- 输出根目录固定为 `D:\work\MTPlayer\release`。
- 不包含任何第三方影视内容、配置源、播放接口或真实用户凭据。
- 所有测试媒体由仓库脚本生成短时测试图案与静音音轨。
- Windows 交付 x64/x86 单文件和 x64/x86 安装包；Android 交付手机/TV 两个签名 APK；macOS 交付签名公证 Universal DMG。
- macOS 缺少 Apple 凭据时不允许生成正式文件名。
- 客户端网络测试必须证明未访问群晖内部端口，服务地址必须由用户绑定。
- `.superpowers/`、本地数据库、日志、令牌、签名密码、证书和 Docker volumes 不进入源码包。
- 前四份实施计划必须分别通过后再执行本计划。
- 代码片段中的 `E2eEnvironment`、`DockerFixture`、`LegalFiles` 等测试辅助类型，均在当前任务所列测试目录中作为内部辅助类型一并创建。

---

### Task 1: 创建合法的端到端测试内容服务

**Files:**
- Create: `tools/MTPlayer.TestContentServer/MTPlayer.TestContentServer.csproj`
- Create: `tools/MTPlayer.TestContentServer/Program.cs`
- Create: `tools/MTPlayer.TestContentServer/Fixtures/TestCatalogue.cs`
- Create: `tools/MTPlayer.TestContentServer/Fixtures/TestLive.cs`
- Create: `tools/MTPlayer.TestContentServer/generate-media.ps1`
- Create: `tests/MTPlayer.EndToEnd/MTPlayer.EndToEnd.csproj`
- Create: `tests/MTPlayer.EndToEnd/TestContentServerTests.cs`
- Modify: `WebHtv.Windows.sln`

**Interfaces:**
- Produces: `/config.json`, `/api.php`, `/parse`, `/media/test.m3u8`, `/live/test.m3u8`, `/live/channels.m3u`, `/epg.xml`.
- Produces: deterministic titles `MT测试电影`, `MT测试剧集` and episodes 1–3.

- [ ] **Step 1: Write test service contract test**

```csharp
[Fact]
public async Task Test_service_exposes_config_catalogue_hls_live_and_epg()
{
    await using var server = await TestContentServerFixture.StartAsync();
    Assert.Contains("MT测试剧集", await server.Client.GetStringAsync("/api.php?ac=videolist&wd=MT测试剧集"));
    Assert.Contains("#EXTM3U", await server.Client.GetStringAsync("/media/test.m3u8"));
    Assert.Contains("tvg-id=\"mt-test\"", await server.Client.GetStringAsync("/live/channels.m3u"));
    Assert.Contains("channel=\"mt-test\"", await server.Client.GetStringAsync("/epg.xml"));
}
```

- [ ] **Step 2: Run the contract test**

Run: `dotnet test .\tests\MTPlayer.EndToEnd\MTPlayer.EndToEnd.csproj --filter Test_service_exposes`

Expected: FAIL because the test server does not exist.

- [ ] **Step 3: Implement deterministic fixtures**

Generate a 30-second 1280×720 color-bar H.264/AAC file with FFmpeg, split into 3 HLS segments, and reuse it for direct, parsed and live routes. CMS detail returns three episodes with different query IDs. Parser returns JSON `{ "url": "absolute-test-media-url", "header": { "Referer": "test" } }` only when the request contains the expected ID.

- [ ] **Step 4: Run contract and copyright scans**

Run:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\MTPlayer.TestContentServer\generate-media.ps1
dotnet test .\tests\MTPlayer.EndToEnd\MTPlayer.EndToEnd.csproj --filter TestContentServer
rg -n -i "(xn--|apitv\.php\?id=|真实片源|第三方仓库)" tools/MTPlayer.TestContentServer
```

Expected: tests PASS and the scan returns no matches.

- [ ] **Step 5: Commit**

```powershell
git add WebHtv.Windows.sln tools/MTPlayer.TestContentServer tests/MTPlayer.EndToEnd
git commit -m "test: add synthetic content service"
```

### Task 2: 跨设备账号与同步端到端测试

**Files:**
- Create: `tests/MTPlayer.EndToEnd/Sync/CrossDeviceSyncTests.cs`
- Create: `tests/MTPlayer.EndToEnd/Sync/OfflineRecoveryTests.cs`
- Create: `tests/MTPlayer.EndToEnd/Sync/EmailFlowTests.cs`
- Create: `tests/MTPlayer.EndToEnd/docker-compose.e2e.yml`

**Interfaces:**
- Consumes: real `mt-api` and PostgreSQL containers.
- Produces: two logical clients using the same public API contract and independent local stores.

- [ ] **Step 1: Write cross-device sync test**

```csharp
[Fact]
public async Task Favorite_progress_skip_and_configuration_sync_across_two_devices()
{
    await using var environment = await E2eEnvironment.StartAsync();
    var windows = await environment.CreateClientAsync("windows");
    var android = await environment.CreateClientAsync("android-tv");
    await windows.AddConfigurationAsync("测试源", environment.TestConfigUrl);
    await windows.FavoriteAsync("MT测试剧集");
    await windows.SaveProgressAsync("MT测试剧集", episode: 2, positionMs: 15_000);
    await windows.SaveSkipAsync("MT测试剧集", "测试接口", "线路一", introSeconds: 3, outroRemainingSeconds: 4);
    await windows.SyncAsync();
    await android.SyncAsync();
    Assert.Equal("测试源", android.Configurations.Single().Name);
    Assert.True(android.Favorites.Single().Title == "MT测试剧集");
    Assert.Equal(15_000, android.History.Single().PositionMs);
    Assert.Equal(4, android.SkipMarkers.Single().OutroRemainingSeconds);
}
```

- [ ] **Step 2: Run sync E2E tests**

Run: `dotnet test .\tests\MTPlayer.EndToEnd\MTPlayer.EndToEnd.csproj --filter CrossDeviceSync`

Expected: FAIL until the E2E environment starts real service containers.

- [ ] **Step 3: Implement Docker E2E fixture and mail capture**

Compose starts PostgreSQL, `mt-api`, a local SMTP capture service and test content server on an isolated network. Tests create admin through setup token, configure SMTP/public URL through admin API, register/verify the user from the captured email link, then create independent client directories.

- [ ] **Step 4: Run sync, offline and email E2E suites**

Run:

```powershell
docker compose -f .\tests\MTPlayer.EndToEnd\docker-compose.e2e.yml up -d --build
dotnet test .\tests\MTPlayer.EndToEnd\MTPlayer.EndToEnd.csproj --filter "Sync|Offline|EmailFlow"
docker compose -f .\tests\MTPlayer.EndToEnd\docker-compose.e2e.yml down -v
```

Expected: PASS for verification, reset, device revoke, first-login merge, offline queue, cursor recovery and cross-device data.

- [ ] **Step 5: Commit**

```powershell
git add tests/MTPlayer.EndToEnd/Sync tests/MTPlayer.EndToEnd/docker-compose.e2e.yml
git commit -m "test: add cross-device sync acceptance"
```

### Task 3: 首次启动免责声明与关于软件一致性

**Files:**
- Modify: `installer/DISCLAIMER.txt`
- Modify: `installer/MTPlayer.iss`
- Create: `src/WebHtv.Desktop/Legal/FirstRunConsentWindow.xaml`
- Create: `src/WebHtv.Desktop/Legal/FirstRunConsentWindow.xaml.cs`
- Create: `android/mobile/src/main/java/cn/mtplayer/mobile/legal/ConsentScreen.kt`
- Create: `android/tv/src/main/java/cn/mtplayer/tv/legal/TvConsentScreen.kt`
- Create: `src/MTPlayer.Mac/Legal/ConsentView.axaml`
- Create: `src/MTPlayer.Mac/Legal/ConsentViewModel.cs`
- Create: `docs/legal/NOTICE.zh-CN.md`
- Create: `tests/MTPlayer.EndToEnd/Legal/LegalCopyTests.cs`

**Interfaces:**
- Produces: versioned consent key `legal-consent-v1` stored locally per client.
- Produces: identical core legal statements on installer, first launch and about page.

- [ ] **Step 1: Write legal copy consistency test**

```csharp
[Fact]
public void Every_platform_states_no_bundled_stored_uploaded_proxied_or_distributed_content()
{
    foreach (var file in LegalFiles.All)
    {
        var text = File.ReadAllText(file);
        Assert.Contains("不内置", text);
        Assert.Contains("不存储", text);
        Assert.Contains("不上传", text);
        Assert.Contains("不代理", text);
        Assert.Contains("不分发", text);
    }
}
```

- [ ] **Step 2: Run legal tests**

Run: `dotnet test .\tests\MTPlayer.EndToEnd\MTPlayer.EndToEnd.csproj --filter LegalCopyTests`

Expected: FAIL until all platform consent screens exist.

- [ ] **Step 3: Implement consent gate without blocking local configuration ownership**

Require explicit accept before first use. Decline closes the app or installer. Store only consent version/time locally, never sync it. The copy states that users supply and are responsible for sources, links, live addresses, logos, EPG and Spider plugins, and must have authorization under applicable law.

- [ ] **Step 4: Run legal and UI build checks**

Run:

```powershell
dotnet test .\tests\MTPlayer.EndToEnd\MTPlayer.EndToEnd.csproj --filter Legal
dotnet build .\src\WebHtv.Desktop\WebHtv.Desktop.csproj -c Release -p:Platform=x64
Set-Location .\android; .\gradlew.bat :mobile:assembleDebug :tv:assembleDebug; Set-Location ..
dotnet build .\src\MTPlayer.Mac\MTPlayer.Mac.csproj -c Release
```

Expected: tests PASS and all three client families build with consent views.

- [ ] **Step 5: Commit**

```powershell
git add installer src/WebHtv.Desktop/Legal android/mobile/src/main/java/cn/mtplayer/mobile/legal android/tv/src/main/java/cn/mtplayer/tv/legal src/MTPlayer.Mac/Legal docs/legal tests/MTPlayer.EndToEnd/Legal
git commit -m "feat: add cross-platform legal consent"
```

### Task 4: 多架构服务端镜像与群晖部署包

**Files:**
- Create: `src/MTPlayer.Server/Dockerfile`
- Create: `build/build-server.ps1`
- Create: `.github/workflows/build-server.yml`
- Create: `docs/deployment/synology-docker.zh-CN.md`
- Create: `tests/MTPlayer.EndToEnd/Deployment/ContainerArchitectureTests.cs`

**Interfaces:**
- Produces: OCI image `mtplayer/server:{version}` for `linux/amd64` and `linux/arm64`.
- Produces: compose, `.env.example`, backup/restore scripts and Cloudflare routing examples without a real hostname.

- [ ] **Step 1: Write container contract test**

```csharp
[Theory]
[InlineData("linux/amd64")]
[InlineData("linux/arm64")]
public async Task Server_image_contains_healthcheck_and_runs_non_root(string platform)
{
    var inspect = await DockerFixture.InspectAsync("mtplayer/server:test", platform);
    Assert.NotEqual("0", inspect.User);
    Assert.Contains("/health/ready", inspect.Healthcheck);
}
```

- [ ] **Step 2: Run container tests**

Run: `dotnet test .\tests\MTPlayer.EndToEnd\MTPlayer.EndToEnd.csproj --filter ContainerArchitecture`

Expected: FAIL because the image does not exist.

- [ ] **Step 3: Build locked-down multi-stage image**

Use .NET 8 SDK/runtime images by digest, publish trimmed disabled, create `/app` and `/data`, run as numeric non-root user, expose internal 8080 only, add healthcheck and write no secrets into layers. Buildx creates a multi-platform OCI archive for offline Synology import and an optional registry tag.

- [ ] **Step 4: Build, scan and validate both architectures**

Run:

```powershell
powershell -ExecutionPolicy Bypass -File .\build\build-server.ps1 -Version 2.0.0
docker scout cves mtplayer/server:2.0.0 --only-severity critical,high
dotnet test .\tests\MTPlayer.EndToEnd\MTPlayer.EndToEnd.csproj --filter ContainerArchitecture
```

Expected: no known critical vulnerability; both architecture tests PASS; deployment docs contain same-network and NAS-local-port Tunnel modes.

- [ ] **Step 5: Commit**

```powershell
git add src/MTPlayer.Server/Dockerfile build/build-server.ps1 .github/workflows/build-server.yml docs/deployment tests/MTPlayer.EndToEnd/Deployment
git commit -m "build(server): add multi-architecture Synology release"
```

### Task 5: 统一发布清单、源码包与机密扫描

**Files:**
- Create: `build/build-all.ps1`
- Create: `build/collect-release.ps1`
- Create: `build/verify-release.ps1`
- Create: `build/release-excludes.txt`
- Create: `docs/release/acceptance-checklist.zh-CN.md`
- Create: `tests/MTPlayer.EndToEnd/Release/ReleaseManifestTests.cs`
- Create: `tests/MTPlayer.EndToEnd/Release/ReleaseManifest.cs`
- Create: `tests/MTPlayer.EndToEnd/Release/SecretPatterns.cs`

**Interfaces:**
- Produces: `release-manifest.json`, `SHA256SUMS.txt`, `MTPlayer-完整源码.zip`.
- Consumes: signed outputs from every platform plan.

- [ ] **Step 1: Write release manifest test**

```csharp
[Fact]
public void Manifest_contains_required_artifacts_and_no_secret_files()
{
    var manifest = ReleaseManifest.Load(Fixture.ReleaseRoot);
    AssertRequired(manifest, "windows/MT播放器-x64.exe", "windows/MT播放器-x86.exe",
        "windows/MT播放器-Setup-x64.exe", "windows/MT播放器-Setup-x86.exe",
        "android/MT播放器-Mobile.apk", "android/MT播放器-TV.apk",
        "macos/MT播放器-universal.dmg", "server/docker-compose.yml",
        "source/MTPlayer-完整源码.zip");
    Assert.DoesNotContain(manifest.Files, file => SecretPatterns.IsSecretPath(file.Path));
}
```

- [ ] **Step 2: Run release tests against an empty staging root**

Run: `dotnet test .\tests\MTPlayer.EndToEnd\MTPlayer.EndToEnd.csproj --filter ReleaseManifest`

Expected: FAIL listing every missing required artifact.

- [ ] **Step 3: Implement deterministic collection and exclusions**

Collect only explicit artifact paths. Source zip includes tracked source/docs/build files and excludes `.git`, `.superpowers`, `bin`, `obj`, `artifacts`, release output, local databases, logs, volumes, `keystore.properties`, `*.p12`, `*.jks`, `.env`, tokens and generated media. Normalize zip timestamps to the release commit time.

Manifest fields are product, version, commit, build UTC, platform, architecture, signed/notarized flags, size and SHA-256. Verify PE architecture, APK signatures, DMG signature/notarization report and OCI platforms before copying.

- [ ] **Step 4: Run secret scan and manifest verification**

Run:

```powershell
powershell -ExecutionPolicy Bypass -File .\build\collect-release.ps1 -Version 2.0.0
powershell -ExecutionPolicy Bypass -File .\build\verify-release.ps1 -ReleaseRoot D:\work\MTPlayer\release
dotnet test .\tests\MTPlayer.EndToEnd\MTPlayer.EndToEnd.csproj --filter ReleaseManifest
```

Expected: PASS; checksums match; source archive contains no excluded or credential-like paths.

- [ ] **Step 5: Commit**

```powershell
git add build/build-all.ps1 build/collect-release.ps1 build/verify-release.ps1 build/release-excludes.txt docs/release/acceptance-checklist.zh-CN.md tests/MTPlayer.EndToEnd/Release
git commit -m "build: add verified release collection"
```

### Task 6: 最终平台矩阵与发布门槛

**Files:**
- Create: `tests/MTPlayer.EndToEnd/Release/FinalAcceptanceTests.cs`
- Create: `tests/MTPlayer.EndToEnd/Release/AcceptanceCase.cs`
- Create: `tests/MTPlayer.EndToEnd/Release/FinalMatrix.cs`
- Create: `docs/release/final-test-report-template.zh-CN.md`
- Create: `docs/release/final-test-report-2.0.0.zh-CN.md`
- Modify: `docs/release/acceptance-checklist.zh-CN.md`
- Modify: `build/verify-release.ps1`

**Interfaces:**
- Produces: machine-readable and Chinese human-readable final test report.
- Consumes: all clients, server, synthetic content and release artifacts.

- [ ] **Step 1: Encode blocking acceptance cases**

```csharp
[Theory]
[MemberData(nameof(FinalMatrix.Cases), MemberType = typeof(FinalMatrix))]
public async Task Every_required_acceptance_case_passes(AcceptanceCase testCase)
{
    var result = await testCase.ExecuteAsync();
    Assert.True(result.Passed, $"{testCase.Platform}/{testCase.Name}: {result.Details}");
}
```

Matrix cases include clean install, consent, empty source, multi-source search, Top 10 hiding, poster click, detail metadata, valid interfaces only, line/episode selection, 10-minute playback, seek, mute, speed, full screen, next episode, per-title skip, resume, favorites/history, live/EPG, account verification/reset, device revoke, cross-device sync, server outage, recovery, upgrade and uninstall.

- [ ] **Step 2: Run the matrix before release wiring**

Run: `dotnet test .\tests\MTPlayer.EndToEnd\MTPlayer.EndToEnd.csproj --filter FinalAcceptance`

Expected: FAIL with each platform case reporting `platform runner is missing`.

- [ ] **Step 3: Wire platform runners and evidence collection**

Define `AcceptanceCase(string Platform, string Name, Func<Task<AcceptanceResult>> ExecuteAsync)` and `AcceptanceResult(bool Passed, string Details)`. `FinalMatrix.Cases` returns every blocking case listed in Step 1. Windows runner launches installed x64/x86 builds in isolated user-data directories. Android runner installs signed APKs to phone and TV emulator profiles and drives touch/DPAD tests. macOS runner mounts DMG, copies App, verifies Gatekeeper and runs UI/headless cases. Capture logs and screenshots only on failure, redact URLs/tokens, and attach request IDs for API errors.

- [ ] **Step 4: Execute final release command**

Run:

```powershell
powershell -ExecutionPolicy Bypass -File .\build\build-all.ps1 -Version 2.0.0
powershell -ExecutionPolicy Bypass -File .\build\verify-release.ps1 -ReleaseRoot D:\work\MTPlayer\release -RunAcceptance
```

Expected: zero blocking failures; final report lists the exact tested OS/device matrix and every artifact hash. If Apple credentials are absent, official final acceptance must fail rather than accept an unsigned DMG.

- [ ] **Step 5: Commit the release report and tag only after approval**

```powershell
git add docs/release/final-test-report-2.0.0.zh-CN.md
git commit -m "docs: record MTPlayer 2.0.0 acceptance"
git tag -a v2.0.0 -m "MT播放器 2.0.0"
```

Do not push the commit or tag until the user explicitly requests GitHub publication.
