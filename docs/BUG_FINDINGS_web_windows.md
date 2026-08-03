# 网页端 & Windows 端 BUG 审查发现（2026-08-03）

> 审查基线：`docs/CODEX_DEV_CONTEXT.md` 记录的需求/约束；重点核查用户历史高频抱怨（搜索闪退、左侧导航无响应、海报点击无反应、直播无法选台、各端 UI 不一致）与 v1.3.6 改动最重处（网页端重写、Windows 直播集成）。
> 范围：网页端（服务端 `WebClientGateway.cs` + 前端 `web-client.js`/`web-client.css`）、Windows 桌面端（`ShellViewModel.cs`/`MainWindow.xaml.cs`/`App.xaml.cs`/`LivePlaylistService.cs` 等）。

---

## 一、按严重程度排序的发现

### 🔴 高：Windows 无全局未处理异常兜底 + 大量 `async void` 事件处理器
- **现象**：`grep` 确认 `App.xaml.cs` 及整个 `WebHtv.Desktop` **未注册 `Dispatcher.UnhandledException`**；而 `MainWindow`/`AccountWindow`/`PlayerWindow`/`MovieDetailWindow`/`WebParserWindow` 等存在 **30+ 个 `async void` 事件处理器**（`Window_Loaded`、`Search_Click`、`Poster_Click`、`Navigation_Click`、`LiveChannelGroup_SelectionChanged`…）。
- **后果**：WPF 中 `async void` 处理器内任何未被局部 try/catch 覆盖的未处理异常，会因缺少全局兜底而**直接闪退进程**。这正是历史上“点击搜索就自动闪退 / 左侧按键没反应”类问题的根因模式。
- **当前状态**：搜索（`ShellViewModel.SearchAsync` 409-498，逐站点容错 + 外層 try/catch）与直播（`PlayLiveSourceAsync` 内 try/catch）链路已局部加固；但其余处理器（如 `ConfigurationRefreshTimer_Tick`、`WebParserWindow.WebResourceResponseReceived` 等）若抛出未覆盖异常仍会崩。
- **修复（2026-08-04）**：
  1. `App.xaml.cs` 的 `OnStartup` 已注册 `DispatcherUnhandledException`（捕获后 `e.Handled = true`，写入 `%LocalAppData%/MTPlayer/crash.log`）+ `TaskScheduler.UnobservedTaskException`（标记已观察，避免未观察 Task 崩溃）。
  2. 全局兜底已覆盖所有 `async void` 处理器，无需逐个包 try/catch；其余处理器维持原有局部容错即可。

### 🟠 中：服务端 `DetailAsync`/`PlayAsync` 用 `.Single()` 会抛 500
- **位置**：`src/MTPlayer.Server/WebClient/WebClientGateway.cs:246` 与 `:277`
  ```csharp
  var site = NormalizeSites([request.Site]).Single();
  ```
- **后果**：当请求站点被 `NormalizeSites` 过滤掉（如 CSP 站点但 `SPIDER_GATEWAY_URL` 未配置、或 `Api` 非法）时，`.Single()` 抛 `InvalidOperationException` → HTTP 500。网页端捕获后只显示“请求失败 (500)”，无业务语义。
- **修复（2026-08-04）**：两处已改为 `NormalizeSites([request.Site]).SingleOrDefault() ?? throw new ArgumentException($"站点“{request.Site}”不可用或未配置。")`。`WebClientEndpoints.ExecuteAsync` 已将 `ArgumentException` 映射为 HTTP 400，网页端会显示友好提示而非 500。

### 🟡 低：直播解析 `catch` 漏掉 `FormatException` 且 try 外调用 `new Uri`
- **位置**：`WebClientGateway.cs:814`（`LooksLikeDirectLiveMedia(liveAddress)` 在 try 之外调用 `new Uri(value)`）+ `:847-856`（catch 仅含 `HttpRequestException/InvalidDataException/TaskCanceledException/ArgumentException`，**不含 `FormatException`/`UriFormatException`**）。
- **后果**：若 `liveAddress` 为畸形非绝对串（理论上上游已校验，概率低），`new Uri` 抛 `UriFormatException` 未被捕获 → 整个 inspect 请求 500。
- **修复（2026-08-04）**：`LooksLikeDirectLiveMedia(liveAddress)`（内部 `new Uri(value)`）已移入 try 块；catch 列表已加入 `FormatException`（涵盖 `UriFormatException`）。畸形直播地址现在记为 warning 而非 500。

### 🟡 低：Windows 直播错误回调用 `async void` lambda
- **位置**：`MainWindow.xaml.cs:245` `Dispatcher.BeginInvoke(async () => { … await PlayLiveSourceAsync(next); })`
- **后果**：异步 void lambda 内若抛未覆盖异常会闪退。当前因 `PlayLiveSourceAsync` 自带 try/catch 而安全，但写法脆弱。
- **修复（2026-08-04）**：`LivePlayer_EncounteredError` 改为 `Dispatcher.BeginInvoke(() => { … _ = PlayLiveSourceAsync(next); })`：UI 工作仍在调度器线程执行，播放以 fire-and-forget 启动（`PlayLiveSourceAsync` 自带 try/catch），消除 async void lambda 隐患。

### ✅ 已确认无问题 / 历史抱怨已修复（好消息）
- **硬约束达标**：全仓库 `src` 下**未硬编码业务域名 / `salego` / 端口**（仅 `bin/Debug/.../libvlc/...` 第三方 CSS 许可证注释命中，非业务代码）。
- **网页海报点击**：`posterCard` 用 `itemRegistry.set(key,item)` 注册（web-client.js:739），`handleClick` 经 `[data-item]` 取回并 `openDetail`（:160-161）——绑定完整，点击有效。
- **网页搜索/详情/播放**：全链路均有 `try/catch + toast`（search:334、openDetail:348、playEpisode:383 等），未发现崩溃点。
- **Windows 搜索闪退**：`SearchAsync` 已用 try/catch 包裹并逐站点容错（409-498），历史崩溃在当前工作区应已修复。
- **Windows 直播选台**：`LivePlaylistService` 解析 + `MainWindow` 内联选台、`_failedLiveUrls` 多源重试、`_changingLiveSource` 防重入，逻辑正确。
- **`_livePlayback!.Player.Stop()`（:225）**：在 `EnsureLivePlayback()` 返回 true 后才执行，非空安全，无 NRE。

---

## 二、修复记录（2026-08-04，全部完成）
1. ✅ **全局异常兜底**（`App.xaml.cs`）：注册 `DispatcherUnhandledException` + `TaskScheduler.UnobservedTaskException`，异常写入 `%LocalAppData%/MTPlayer/crash.log`，`e.Handled = true` 阻止闪退。
2. ✅ **服务端 `.Single()` → `SingleOrDefault()` + `ArgumentException`**（两处）：CSP 站点详情/播放由 500 变为 400 友好提示。
3. ✅ **直播解析 `FormatException`**（`LooksLikeDirectLiveMedia` 移入 try + catch 补 `FormatException`）：畸形直播地址降级为 warning。
4. ✅ **`LivePlayer_EncounteredError` async void lambda**：改为同步 `BeginInvoke` + fire-and-forget。

**验证**：`dotnet build` 对 `MTPlayer.Server` 与 `WebHtv.Desktop` 均 0 错误、0 警告（Debug）。改动均遵循 `CODEX_DEV_CONTEXT.md` 硬约束（未硬编码域名、未提交密钥）。

> 修复为增量改动，叠加在 v1.3.6 未提交工作区之上；尚未 `git commit`。如需提交，请确认（建议把 `release-builds/`、`releases/` 加入 `.gitignore` 后再提交）。
