# MT播放器 Android 手机与电视 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 交付可实际搜索、解析、播放和同步的 Android 手机 APK 与 Android TV APK，其中电视端拥有完整遥控器焦点体验。

**Architecture:** 单一 Gradle 工程包含共享 `core`、手机 `mobile` 和电视 `tv` 三个模块。共享层负责配置、目录、Spider、播放会话、本地数据库、账号和同步；两个应用模块仅负责平台导航和视图。

**Tech Stack:** Android Gradle Plugin 8.6.1、Kotlin 2.0.21、minSdk 26、compileSdk/targetSdk 35、Jetpack Compose、Compose for TV、Media3 1.5.1、LibVLC 3.6.5、Room 2.6.1、DataStore 1.1.1、OkHttp 4.12.0、kotlinx.serialization 1.7.3

## Global Constraints

- Android 8.0/API 26 及以上；分别输出 `MT播放器-Mobile.apk` 与 `MT播放器-TV.apk`。
- 手机使用触控与底部导航；电视所有功能可只用方向键、确认键、返回键完成。
- 游客可完整本地播放；服务器离线不阻塞搜索和播放。
- 生产版服务器绑定只接受 HTTPS；不硬编码任何域名或群晖端口。
- 应用不内置内容源；首次启动为空库并引导用户添加配置。
- 接口、线路、剧集顺序固定；只显示含该影片且可访问的接口。
- 片头片尾按影片、接口和线路保存并同步。
- 使用现有 `mtplayer-icon.png` 生成 Android adaptive icon；关于页使用横版 Logo。
- 服务端计划必须完成并冻结 OpenAPI v1 后再实现账号同步任务。
- 代码片段中的 `fixtureRepository`、`Fixtures`、`fakeEngine` 等测试辅助类型，均作为当前任务所列测试文件中的私有辅助类型一并创建。

---

### Task 1: 创建三模块 Android 工程与品牌资源

**Files:**
- Create: `android/settings.gradle.kts`
- Create: `android/build.gradle.kts`
- Create: `android/gradle/libs.versions.toml`
- Create: `android/gradlew`
- Create: `android/gradlew.bat`
- Create: `android/gradle/wrapper/gradle-wrapper.jar`
- Create: `android/gradle/wrapper/gradle-wrapper.properties`
- Create: `android/core/build.gradle.kts`
- Create: `android/mobile/build.gradle.kts`
- Create: `android/tv/build.gradle.kts`
- Create: `android/mobile/src/main/AndroidManifest.xml`
- Create: `android/tv/src/main/AndroidManifest.xml`
- Create: `android/mobile/src/main/res/mipmap-anydpi-v26/ic_launcher.xml`
- Create: `android/tv/src/main/res/mipmap-anydpi-v26/ic_launcher.xml`
- Create: `android/mobile/src/main/java/cn/mtplayer/mobile/MobileApplication.kt`
- Create: `android/tv/src/main/java/cn/mtplayer/tv/TvApplication.kt`
- Create: `android/core/src/test/java/cn/mtplayer/core/BuildContractTest.kt`

**Interfaces:**
- Produces: application IDs `cn.mtplayer.mobile` and `cn.mtplayer.tv`.
- Produces: shared package prefix `cn.mtplayer.core`.

- [ ] **Step 1: Write build contract test**

```kotlin
class BuildContractTest {
    @Test fun api_floor_is_android_8() {
        assertEquals(26, BuildContract.minSdk)
        assertEquals(35, BuildContract.targetSdk)
    }
}
```

- [ ] **Step 2: Run the test and verify missing-project failure**

Run: `Set-Location .\android; .\gradlew.bat :core:testDebugUnitTest`

Expected: FAIL because the Gradle wrapper and modules do not exist.

- [ ] **Step 3: Create Gradle modules and exact build constants**

```kotlin
object BuildContract {
    const val minSdk = 26
    const val targetSdk = 35
}
```

Configure Java 17, Kotlin JVM target 17, Compose compiler plugin matching Kotlin 2.0.21, ABI splits for `armeabi-v7a`, `arm64-v8a` and `x86_64`, and a universal release APK for each app. Mark TV manifest with `android.software.leanback`, touchscreen not required and `LEANBACK_LAUNCHER`.

- [ ] **Step 4: Generate wrapper, icons and run both debug builds**

Copy the approved source icon into each module, generate foreground/background adaptive icon XML, and use the horizontal Logo only on about/login surfaces.

Run:

```powershell
Set-Location .\android
gradle wrapper --gradle-version 8.10.2
.\gradlew.bat :core:testDebugUnitTest :mobile:assembleDebug :tv:assembleDebug
```

Expected: PASS; two debug APKs exist and TV APK declares the Leanback launcher.

- [ ] **Step 5: Commit**

```powershell
git add android
git commit -m "feat(android): add mobile and TV project skeletons"
```

### Task 2: 配置、目录与多源搜索共享层

**Files:**
- Create: `android/core/src/main/java/cn/mtplayer/core/config/TvBoxModels.kt`
- Create: `android/core/src/main/java/cn/mtplayer/core/config/ConfigurationRepository.kt`
- Create: `android/core/src/main/java/cn/mtplayer/core/catalogue/CatalogueModels.kt`
- Create: `android/core/src/main/java/cn/mtplayer/core/catalogue/CmsCatalogueProvider.kt`
- Create: `android/core/src/main/java/cn/mtplayer/core/catalogue/AggregatedSearchService.kt`
- Create: `android/core/src/test/java/cn/mtplayer/core/config/ConfigurationRepositoryTest.kt`
- Create: `android/core/src/test/java/cn/mtplayer/core/catalogue/AggregatedSearchServiceTest.kt`

**Interfaces:**
- Produces: `ConfigurationRepository.observeGroups()`, `refresh(id)`, `enabledProfiles()`.
- Produces: `CatalogueProvider.search`, `detail`, `createPlayRequest`.

- [ ] **Step 1: Write wrapped-config, depot and duplicate-key tests**

```kotlin
@Test fun enabled_groups_get_collision_free_runtime_keys() = runTest {
    val repository = fixtureRepository(group("甲", true, siteKey = "same"), group("乙", true, siteKey = "same"))
    val profile = repository.enabledProfiles()
    assertEquals(2, profile.sites.size)
    assertEquals(2, profile.sites.map { it.runtimeKey }.toSet().size)
}
```

- [ ] **Step 2: Run core configuration tests**

Run: `Set-Location .\android; .\gradlew.bat :core:testDebugUnitTest --tests "*ConfigurationRepositoryTest*"`

Expected: FAIL because repository and models do not exist.

- [ ] **Step 3: Implement JSON compatibility and per-group cache**

Use `kotlinx.serialization` with `ignoreUnknownKeys = true`, preserve unknown top-level fields as `JsonObject`, support plain JSON and wrapped Base64 payloads, limit remote configuration to 10 MiB, assign runtime keys as `{groupUuid}:{siteKey}` and retain the last valid cache when refresh fails.

```kotlin
interface CatalogueProvider {
    fun canHandle(site: TvBoxSite): Boolean
    suspend fun search(site: TvBoxSite, keyword: String, page: Int): CataloguePage
    suspend fun detail(site: TvBoxSite, id: String): CatalogueDetail
    fun createPlayRequest(site: TvBoxSite, line: EpisodeSource, episode: Episode): PlayRequest
}
```

- [ ] **Step 4: Implement bounded aggregated search and run tests**

Search enabled sites with a semaphore of 8, 12-second per-site timeout and cancellation. Normalize titles with Unicode NFKC, punctuation removal and uppercase. Deduplicate exact title/year matches while preserving available interface keys.

Run: `Set-Location .\android; .\gradlew.bat :core:testDebugUnitTest`

Expected: PASS for import, depot expansion, source caching, CMS parsing, cancellation and dedupe.

- [ ] **Step 5: Commit**

```powershell
git add android/core/src/main/java/cn/mtplayer/core/config android/core/src/main/java/cn/mtplayer/core/catalogue android/core/src/test
git commit -m "feat(android): add configuration and catalogue core"
```

### Task 3: JS/JAR Spider 与有效接口检测

**Files:**
- Create: `android/core/src/main/java/cn/mtplayer/core/spider/SpiderProvider.kt`
- Create: `android/core/src/main/java/cn/mtplayer/core/spider/RhinoSpiderRuntime.kt`
- Create: `android/core/src/main/java/cn/mtplayer/core/spider/JarSpiderRuntime.kt`
- Create: `android/core/src/main/java/cn/mtplayer/core/catalogue/PlaybackInterfaceProbe.kt`
- Create: `android/core/src/test/java/cn/mtplayer/core/spider/SpiderRuntimeTest.kt`
- Create: `android/core/src/test/java/cn/mtplayer/core/catalogue/PlaybackInterfaceProbeTest.kt`

**Interfaces:**
- Produces: `SpiderRuntime.init/search/detail/player/destroy`.
- Produces: `PlaybackInterfaceProbe.probe(title, candidates)` returning only matching reachable details.

- [ ] **Step 1: Write timeout, destroy and interface-filter tests**

```kotlin
@Test fun hanging_spider_is_cancelled_without_blocking_other_sources() = runTest {
    val probe = PlaybackInterfaceProbe(listOf(hangingSite(), reachableSite()), maxConcurrency = 6, timeout = 6.seconds)
    val result = probe.probe("仙逆")
    assertEquals(listOf("可用接口"), result.map { it.name })
}
```

- [ ] **Step 2: Run Spider tests**

Run: `Set-Location .\android; .\gradlew.bat :core:testDebugUnitTest --tests "*Spider*" --tests "*PlaybackInterfaceProbe*"`

Expected: FAIL because runtimes and probe do not exist.

- [ ] **Step 3: Implement Rhino sandbox and JAR class loader**

Run Rhino with instruction counting, 15-second timeout, no Java package visibility, no filesystem or process bindings, and an approved OkHttp bridge. Download JAR/Dex to app-private storage, verify configured checksum when present, cap at 30 MiB, load with `DexClassLoader`, instantiate `com.github.catvod.spider.Spider`, invoke known methods through a narrow adapter and always call `destroy`.

Return typed states `Unsupported`, `Timeout`, `PluginCrash`, `InvalidResponse`, `NetworkFailure`; never convert them to a successful empty response.

- [ ] **Step 4: Implement interface probing and instrumentation crash test**

Probe at most 6 interfaces concurrently, search exact normalized title, load details, require at least one episode, then validate up to 4 first-episode requests. Cache success 15 minutes and network failure 2 minutes.

Run:

```powershell
Set-Location .\android
.\gradlew.bat :core:testDebugUnitTest :core:connectedDebugAndroidTest
```

Expected: PASS; a crashing sample Spider does not terminate the instrumentation process.

- [ ] **Step 5: Commit**

```powershell
git add android/core/src/main/java/cn/mtplayer/core/spider android/core/src/main/java/cn/mtplayer/core/catalogue/PlaybackInterfaceProbe.kt android/core/src/test android/core/src/androidTest
git commit -m "feat(android): add Spider runtimes and interface probe"
```

### Task 4: Room 本地库、设置和离线同步

**Files:**
- Create: `android/core/src/main/java/cn/mtplayer/core/data/MTPlayerDatabase.kt`
- Create: `android/core/src/main/java/cn/mtplayer/core/data/Entities.kt`
- Create: `android/core/src/main/java/cn/mtplayer/core/data/Daos.kt`
- Create: `android/core/src/main/java/cn/mtplayer/core/settings/SettingsRepository.kt`
- Create: `android/core/src/main/java/cn/mtplayer/core/sync/SyncEngine.kt`
- Create: `android/core/src/main/java/cn/mtplayer/core/sync/SyncWorker.kt`
- Create: `android/core/src/androidTest/java/cn/mtplayer/core/data/DatabaseMigrationTest.kt`
- Create: `android/core/src/test/java/cn/mtplayer/core/sync/SyncEngineTest.kt`

**Interfaces:**
- Produces: Room flows for favorites, history, skip markers, configuration groups, sync queue and cursor.
- Produces: WorkManager unique work `mtplayer-sync`.

- [ ] **Step 1: Write database and offline queue tests**

```kotlin
@Test fun logout_keeps_local_library_and_stops_sync() = runTest {
    val fixture = syncFixture(loggedIn = true)
    fixture.favorites.add(favorite("仙逆"))
    fixture.engine.logout()
    assertEquals(1, fixture.favorites.count())
    assertFalse(fixture.workManager.hasUniqueWork("mtplayer-sync"))
}
```

- [ ] **Step 2: Run data and sync tests**

Run: `Set-Location .\android; .\gradlew.bat :core:testDebugUnitTest :core:connectedDebugAndroidTest`

Expected: FAIL because Room schema and sync engine do not exist.

- [ ] **Step 3: Create schema and guest merge transaction**

Use stable UUID primary keys and include `version`, `modifiedAtUtc`, `isDeleted`. Store intro seconds and outro remaining seconds. On first login, merge favorites by stable content ID, history by latest watch time, configurations by normalized URL, and markers/preferences by latest modification in one Room transaction.

- [ ] **Step 4: Implement WorkManager synchronization**

Push queue then pull cursor pages, require unmetered network only for manual configuration refresh—not for metadata sync. Use WorkManager backoff, keep the queue after failure, and schedule 15-minute periodic sync plus immediate work after favorite/history/marker mutation.

Run: `Set-Location .\android; .\gradlew.bat :core:testDebugUnitTest :core:connectedDebugAndroidTest`

Expected: PASS for migration, offline restart, conflict, tombstone, logout and guest merge.

- [ ] **Step 5: Commit**

```powershell
git add android/core/src/main/java/cn/mtplayer/core/data android/core/src/main/java/cn/mtplayer/core/settings android/core/src/main/java/cn/mtplayer/core/sync android/core/src/test android/core/src/androidTest
git commit -m "feat(android): add local-first data and sync"
```

### Task 5: 账号、服务器绑定与电视设备码

**Files:**
- Create: `android/core/src/main/java/cn/mtplayer/core/account/ServerBinding.kt`
- Create: `android/core/src/main/java/cn/mtplayer/core/account/AccountRepository.kt`
- Create: `android/core/src/main/java/cn/mtplayer/core/account/SecureTokenStore.kt`
- Create: `android/core/src/test/java/cn/mtplayer/core/account/AccountRepositoryTest.kt`
- Create: `android/tv/src/main/java/cn/mtplayer/tv/account/TvDeviceCodeScreen.kt`
- Create: `android/mobile/src/main/java/cn/mtplayer/mobile/account/AccountScreen.kt`

**Interfaces:**
- Produces: account states `Guest`, `Unverified`, `SignedIn`, `Offline`, `Syncing`, `Error`.
- Produces: TV polling flow stopped on expiry, cancellation or approval.

- [ ] **Step 1: Write HTTPS binding and token-rotation tests**

```kotlin
@Test fun production_binding_rejects_http_and_paths() {
    assertTrue(ServerBinding.parse("https://sync.example.com", debug = false).isSuccess)
    assertTrue(ServerBinding.parse("http://sync.example.com", debug = false).isFailure)
    assertTrue(ServerBinding.parse("https://sync.example.com/api", debug = false).isFailure)
}
```

- [ ] **Step 2: Run account tests**

Run: `Set-Location .\android; .\gradlew.bat :core:testDebugUnitTest --tests "*Account*"`

Expected: FAIL because account repository does not exist.

- [ ] **Step 3: Implement account repository and encrypted storage**

Use Android Keystore AES/GCM for refresh token storage; keep access token in memory. Serialize token refresh with `Mutex`. Parse QR payload schema `{"version":1,"serverUrl":"https://..."}` and store only after HTTPS validation. Logout clears tokens and cancels sync without deleting Room data.

- [ ] **Step 4: Implement phone forms and TV device-code screen**

Phone page provides server binding, register, verify, login, resend verification, devices, revoke and sync status. TV shows QR plus 8-character code, polls no faster than server interval, supports manual server entry and clearly returns to guest mode on cancel.

Run: `Set-Location .\android; .\gradlew.bat :core:testDebugUnitTest :mobile:assembleDebug :tv:assembleDebug`

Expected: PASS; both apps compile against the same account repository.

- [ ] **Step 5: Commit**

```powershell
git add android/core/src/main/java/cn/mtplayer/core/account android/core/src/test/java/cn/mtplayer/core/account android/mobile/src/main/java/cn/mtplayer/mobile/account android/tv/src/main/java/cn/mtplayer/tv/account
git commit -m "feat(android): add account and TV device sign-in"
```

### Task 6: Media3/LibVLC 播放器与片头片尾

**Files:**
- Create: `android/core/src/main/java/cn/mtplayer/core/playback/PlaybackResolver.kt`
- Create: `android/core/src/main/java/cn/mtplayer/core/playback/PlayerEngine.kt`
- Create: `android/core/src/main/java/cn/mtplayer/core/playback/Media3Engine.kt`
- Create: `android/core/src/main/java/cn/mtplayer/core/playback/VlcEngine.kt`
- Create: `android/core/src/main/java/cn/mtplayer/core/playback/PlayerCoordinator.kt`
- Create: `android/core/src/test/java/cn/mtplayer/core/playback/PlayerCoordinatorTest.kt`
- Create: `android/mobile/src/main/java/cn/mtplayer/mobile/player/MobilePlayerScreen.kt`
- Create: `android/tv/src/main/java/cn/mtplayer/tv/player/TvPlayerScreen.kt`

**Interfaces:**
- Produces: engine-neutral `PlayerState` and `PlayerCommand` flow.
- Consumes: final URL, headers, subtitle/audio tracks and `SkipMarker`.

- [ ] **Step 1: Write fallback, progress and skip tests**

```kotlin
@Test fun media3_open_failure_falls_back_to_vlc_once() = runTest {
    val media3 = fakeEngine(openResult = Result.failure(IllegalStateException("decoder initialization failed")))
    val vlc = fakeEngine(openResult = Result.success(Unit))
    val coordinator = PlayerCoordinator(media3, vlc, fixtureProgressStore())
    coordinator.open(playback("https://media.example/a.m3u8"))
    assertEquals(1, media3.openCalls)
    assertEquals(1, vlc.openCalls)
}
```

- [ ] **Step 2: Run playback tests**

Run: `Set-Location .\android; .\gradlew.bat :core:testDebugUnitTest --tests "*PlayerCoordinator*"`

Expected: FAIL because player abstractions do not exist.

- [ ] **Step 3: Implement engine coordinator**

Media3 handles HLS/DASH/progressive first and applies HTTP headers. On unsupported codec, decoder initialization or open timeout, release it and try LibVLC once at the same resume position. Save progress every 15 seconds, pause, episode switch and dispose. Apply intro jump once and trigger next episode once when remaining duration reaches the marker.

- [ ] **Step 4: Implement touch and TV controls**

Both players expose play/pause, ±10 seconds, previous/next, scrub, volume/mute, speed labels, subtitles, audio, full screen and per-title skip settings. Mobile adds horizontal seek and vertical volume/brightness gestures. TV controls are DPAD focusable and hide after 5 seconds without input.

Run: `Set-Location .\android; .\gradlew.bat :core:testDebugUnitTest :mobile:connectedDebugAndroidTest :tv:connectedDebugAndroidTest`

Expected: PASS; synthetic HLS test opens on Media3 and forced codec failure opens on VLC.

- [ ] **Step 5: Commit**

```powershell
git add android/core/src/main/java/cn/mtplayer/core/playback android/core/src/test/java/cn/mtplayer/core/playback android/mobile/src/main/java/cn/mtplayer/mobile/player android/tv/src/main/java/cn/mtplayer/tv/player
git commit -m "feat(android): add native playback engines"
```

### Task 7: 手机完整导航与内容页面

**Files:**
- Create: `android/mobile/src/main/java/cn/mtplayer/mobile/MainActivity.kt`
- Create: `android/mobile/src/main/java/cn/mtplayer/mobile/navigation/MobileNavigation.kt`
- Create: `android/mobile/src/main/java/cn/mtplayer/mobile/home/HomeScreen.kt`
- Create: `android/mobile/src/main/java/cn/mtplayer/mobile/search/SearchScreen.kt`
- Create: `android/mobile/src/main/java/cn/mtplayer/mobile/detail/DetailScreen.kt`
- Create: `android/mobile/src/main/java/cn/mtplayer/mobile/library/LibraryScreen.kt`
- Create: `android/mobile/src/main/java/cn/mtplayer/mobile/settings/SettingsScreen.kt`
- Create: `android/mobile/src/androidTest/java/cn/mtplayer/mobile/MobileNavigationTest.kt`

**Interfaces:**
- Produces: bottom tabs `home`, `search`, `live`, `library`, `account`.
- Consumes: shared repositories and player from Tasks 2–6.

- [ ] **Step 1: Write Compose navigation test**

```kotlin
@Test fun search_hides_top_ten_and_detail_orders_interface_line_episode() {
    compose.setContent { MobileApp(fakes(keyword = "仙逆")) }
    compose.onNodeWithText("搜索").performClick()
    compose.onNodeWithText("仙逆").performTextInput("仙逆")
    compose.onNodeWithText("电影 Top 10").assertDoesNotExist()
    compose.onNodeWithText("仙逆").performClick()
    compose.onNodeWithText("播放接口").assertIsDisplayed()
    compose.onNodeWithText("播放线路").assertIsDisplayed()
    compose.onNodeWithText("选择剧集").assertIsDisplayed()
}
```

- [ ] **Step 2: Run mobile UI tests**

Run: `Set-Location .\android; .\gradlew.bat :mobile:connectedDebugAndroidTest`

Expected: FAIL because screens and navigation do not exist.

- [ ] **Step 3: Implement theme, shell and content states**

Use MT black/red theme, edge-to-edge insets, loading skeletons, inline retry and poster hover-free touch feedback. Home shows five Top 10 rows plus continue watching. Detail uses a large scrollable episode grid and sticky “播放所选集” button. Empty configuration, offline and no-results states must be actionable and non-modal.

- [ ] **Step 4: Run mobile accessibility and rotation tests**

Run: `Set-Location .\android; .\gradlew.bat :mobile:connectedDebugAndroidTest`

Expected: PASS for navigation, search, detail, favorites/history, settings, account, portrait recreation and landscape player entry.

- [ ] **Step 5: Commit**

```powershell
git add android/mobile/src/main android/mobile/src/androidTest
git commit -m "feat(android): complete mobile UI"
```

### Task 8: Android TV 十英尺界面与遥控器焦点

**Files:**
- Create: `android/tv/src/main/java/cn/mtplayer/tv/MainActivity.kt`
- Create: `android/tv/src/main/java/cn/mtplayer/tv/navigation/TvNavigation.kt`
- Create: `android/tv/src/main/java/cn/mtplayer/tv/home/TvHomeScreen.kt`
- Create: `android/tv/src/main/java/cn/mtplayer/tv/search/TvSearchScreen.kt`
- Create: `android/tv/src/main/java/cn/mtplayer/tv/detail/TvDetailScreen.kt`
- Create: `android/tv/src/main/java/cn/mtplayer/tv/library/TvLibraryScreen.kt`
- Create: `android/tv/src/main/java/cn/mtplayer/tv/settings/TvSettingsScreen.kt`
- Create: `android/tv/src/androidTest/java/cn/mtplayer/tv/TvDpadNavigationTest.kt`

**Interfaces:**
- Produces: left rail pages `home`, `search`, `favorites`, `history`, `live`, `settings`, `account`.
- Consumes: shared repositories and TV player.

- [ ] **Step 1: Write DPAD-only navigation test**

```kotlin
@Test fun remote_can_open_poster_select_episode_play_and_return_home() {
    compose.setContent { TvApp(fakes()) }
    pressKey(KeyEvent.KEYCODE_DPAD_RIGHT)
    pressKey(KeyEvent.KEYCODE_DPAD_CENTER)
    compose.onNodeWithText("选择剧集").assertIsDisplayed()
    pressKey(KeyEvent.KEYCODE_DPAD_DOWN)
    pressKey(KeyEvent.KEYCODE_DPAD_CENTER)
    compose.onNodeWithText("播放/暂停").assertExists()
    pressKey(KeyEvent.KEYCODE_BACK)
    pressKey(KeyEvent.KEYCODE_BACK)
    compose.onNodeWithText("热门推荐").assertIsDisplayed()
}
```

- [ ] **Step 2: Run TV UI tests**

Run: `Set-Location .\android; .\gradlew.bat :tv:connectedDebugAndroidTest`

Expected: FAIL because TV screens do not exist.

- [ ] **Step 3: Implement focus restoration and visual states**

Remember last focused item per row and page. Focused poster scales to 1.06, moves up 6 dp, uses a 2 dp red border and shadow; never adds a white overlay. Back closes overlays, then detail, then returns to the previous rail item; it exits only from the root after confirmation.

- [ ] **Step 4: Run remote, large-text and process-recreation tests**

Run: `Set-Location .\android; .\gradlew.bat :tv:connectedDebugAndroidTest`

Expected: PASS with keyboard-emulated DPAD; no focus trap across rail, rows, selectors, episode grid, dialogs or player controls.

- [ ] **Step 5: Commit**

```powershell
git add android/tv/src/main android/tv/src/androidTest
git commit -m "feat(android): complete Android TV UI"
```

### Task 9: 直播、签名 APK 与 Android 验收

**Files:**
- Create: `android/core/src/main/java/cn/mtplayer/core/live/LivePlaylistRepository.kt`
- Create: `android/core/src/main/java/cn/mtplayer/core/live/XmlTvRepository.kt`
- Create: `android/core/src/test/java/cn/mtplayer/core/live/LiveRepositoryTest.kt`
- Create: `android/mobile/src/main/java/cn/mtplayer/mobile/live/LiveScreen.kt`
- Create: `android/tv/src/main/java/cn/mtplayer/tv/live/TvLiveScreen.kt`
- Create: `android/keystore.properties.example`
- Create: `build/build-android.ps1`
- Modify: `android/mobile/build.gradle.kts`
- Modify: `android/tv/build.gradle.kts`

**Interfaces:**
- Produces: live groups, logo, current/next program, favorites and alternate streams.
- Produces: signed universal mobile and TV release APKs.

- [ ] **Step 1: Write M3U/XMLTV priority tests**

```kotlin
@Test fun epg_matches_tvg_id_before_name_and_source_logo_before_fallback() = runTest {
    val channel = parseM3u(fixtureM3u()).single()
    val enriched = xmlTv.enrich(listOf(channel), fixtureXmlTv())
    assertEquals("cctv1", enriched.single().channelId)
    assertEquals("新闻联播", enriched.single().nowPlaying)
    assertEquals("https://logo.example/cctv1.png", enriched.single().logoUrl)
}
```

- [ ] **Step 2: Run live tests**

Run: `Set-Location .\android; .\gradlew.bat :core:testDebugUnitTest --tests "*LiveRepositoryTest*"`

Expected: FAIL because live repositories do not exist.

- [ ] **Step 3: Implement live repositories and both UIs**

Support local picker and HTTP(S) M3U/M3U8/TXT, optional XMLTV URL, scheduled refresh, exact `tvg-id` then normalized-name matching, source logo priority, groups, favorites, recent channels and same-name alternate URLs. TV uses channel list + EPG panel; phone uses group chips + channel list.

- [ ] **Step 4: Build signed release artifacts and run full Android checks**

Read signing values only from untracked `android/keystore.properties`; fail release build with a clear message when missing. Run:

```powershell
Set-Location .\android
.\gradlew.bat clean testDebugUnitTest lintDebug :mobile:connectedDebugAndroidTest :tv:connectedDebugAndroidTest
Set-Location ..
powershell -ExecutionPolicy Bypass -File .\build\build-android.ps1 -Version 2.0.0
```

Expected: all checks PASS; `D:\work\MTPlayer\release\android\MT播放器-Mobile.apk` and `MT播放器-TV.apk` are signed, installable and use the approved icon.

- [ ] **Step 5: Commit**

```powershell
git add android/core/src/main/java/cn/mtplayer/core/live android/core/src/test/java/cn/mtplayer/core/live android/mobile/src/main/java/cn/mtplayer/mobile/live android/tv/src/main/java/cn/mtplayer/tv/live android/keystore.properties.example android/mobile/build.gradle.kts android/tv/build.gradle.kts build/build-android.ps1
git commit -m "feat(android): complete Android release"
```
