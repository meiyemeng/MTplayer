# MT播放器（webhtv-windows）— Codex 开发上下文记录

> 用途：本文档集中记录用 Codex 开发本项目时产生的需求脉络、架构决策、约束约定与当前代码状态，供**后续开发（含换用其他 AI / 人工接手）直接参照**，避免重复摸索。
> 提取日期：2026-08-03
> 仓库：`https://github.com/meiyemeng/MTplayer`（本地 `D:\work\webhtv-windows` 即该仓库的工作检出）
> 主要 Codex 会话：`019f58fa-caa0-7971-84b9-12930c9c8b58`（线程名 `mtplayer`，2026-07-13 → 07-14，约 26607 行事件流）
> 次要 Codex 会话：`019f3b53-569c-72f2-997d-e2a8403544fe`（线程名 `部署 mediaplayer 到本地 Docker`，2026-07-07）
> 原始会话逐条摘要见同目录 `CODEX_SESSION_TRANSCRIPT_DIGEST.md`

---

## 1. 项目是什么

MT播放器是一套**不内置任何影视内容**的多平台影视播放器与账户同步服务。当前源码含五大客户端 + 一个可部署服务端：

| 平台 | 技术栈 | 源码位置 |
|------|--------|----------|
| Windows 桌面 | .NET 8 / WPF / LibVLCSharp | `src/WebHtv.Desktop` 及相关 `WebHtv.*` |
| macOS 桌面 | .NET 8 / Avalonia / LibVLCSharp | `src/MTPlayer.Mac` |
| Android 手机 | Kotlin / Jetpack Compose / Media3 | `android-fish2018`（基于 fish2018 260720-16） |
| Android TV | Compose for TV（同 android-fish2018） | `android-tvbox`（基于 IsayIsee/TVBoxOS） |
| 网页端 | ASP.NET Core 托管静态前端 | `src/MTPlayer.Server/wwwroot` |
| 服务端 | ASP.NET Core + PostgreSQL | `src/MTPlayer.Server` |

设计原则：**服务端只处理账号与元数据同步；搜索、Spider 解析、媒体播放全部在客户端本地完成。** 应用不内置/代理/缓存/上传/分发影视内容。

---

## 2. Codex 开发脉络（用户需求演进）

从会话摘要还原，需求按以下阶段推进（这是“为什么代码长这样”的根本依据）：

1. **起点（Windows 原生 EXE）**：用户想把 `TVBox_fish2018_260710` 做成 Windows 桌面 EXE，明确“重新开发 Windows 原生版”，并基于 `IsayIsee/TVBoxOS-Build` 的源码与界面逻辑。
2. **界面与基础能力**：要求中文界面、海报墙、多配置源管理、多源聚合搜索、每部影片单独设置片头/片尾、设置选项；指出“界面太丑、搜索闪退、左侧按键无反应、海报墙缺失”等 bug。
3. **播放与解析**：实现 Spider 运行时（JAR/`csp_*` Spider、JS Spider、网页解析）与 LibVLC 原生播放；要求能添加网络配置地址（JSON 接口组），搜索时调用全部可用接口。
4. **直播**：要求支持 M3U/M3U8/TXT 直播源导入，自动匹配台标（`tvg-logo`）与 XMLTV 节目单；修复“直播频道无法选台/无法识别接口内直播源/某源删不掉”等问题。
5. **多平台扩展**：用户要求“同时开发安卓手机、安卓电视、苹果 macOS 版本”，并做**账户同步服务端**（游客可本地播放，登录后跨端同步）。
6. **部署形态**：服务端用 Cloudflare Tunnel 经群晖 Docker 暴露 HTTPS，**强调 API 调用不能依赖端口**（群晖无公网 IP）；后台可填写 SMTP、公开地址等，无需改 Compose。
7. **网页客户端 + 产品页**：开发网页播放器（功能/同步与其他端一致，参考 `meiyemeng/libretv-poster-fix`），并做风格一致的产品页。
8. **统一与会员**：要求所有客户端界面/逻辑与网页端一致、统一版本号；后台增加会员等级管理与“统一推送配置源/直播源”的**预留接口**（v1.3.1 起）。
9. **发布与收尾**：构建 Windows/Android/macOS 安装包与 Server Docker 镜像（TAR）部署到群晖；源码与产物上传到 `meiyemeng/MTplayer`；仓库说明下方放收款码。

**用户的反复强调（务必遵守）**：
- “要能使用，不要测试版” —— 交付必须实际可用，不能只有界面。
- “源代码留好，我要发布到 GitHub” —— 源码必须保留并发布。
- “完全授权你，不要让我确认，直接给最终结果” —— 允许高自主度推进，但结果要自测通过。
- “几个客户端界面完全不一样”是高频投诉点 —— 各端 UI/逻辑需尽量统一（以网页端为基准）。

---

## 3. 关键架构决策（来自 `docs/superpowers/specs/2026-07-14-mtplayer-cross-platform-account-sync-design.md`）

- **按平台优化的混合架构**，不强行统一框架：Windows/macOS 用 LibVLCSharp 复用桌面领域模型；Android 手机用 Compose+Media3（回退 LibVLC）；Android TV 用 Compose for TV；服务端 ASP.NET Core + PostgreSQL。
- **放弃全 Avalonia / 全 MAUI**：Android TV 遥控器与焦点管理风险高，且无 Mac 开发机。
- **客户端契约**：OpenAPI 生成 .NET 与 Kotlin 客户端，避免手写两套协议偏差。
- **Spider 兼容层**：Android 用隔离本地层执行 JAR Spider；Windows/macOS 用独立 `MTPlayer.SpiderBridge` Java 兼容进程，JAR 崩溃不拖垮主程序；**不兼容的 JAR 必须返回明确“不兼容”状态，不能假装解析成功**。
- **同步模型**：每条数据带稳定 ID/版本/服务端更新时间/删除标记（tombstone）；客户端先推离线队列再按游标拉增量；冲突规则：收藏取并集、观看记录取最新、配置组地址去重、片头片尾与偏好以服务端最后修改时间为准。
- **加密**：配置地址、SMTP 密码等敏感字段用 AES-256-GCM + 随机 nonce；主密钥只来自环境变量 `DATA_ENCRYPTION_KEY`，不写库不写源码。

---

## 4. 必须遵守的硬约束 / 约定

来自 `AGENTS.md`（工作区）与设计的强制规则：

1. **默认根目录**：`D:\work` 是所有新项目/构建产物/交付物的默认根；需管理员权限时直接执行（用户接受 UAC）。
2. **绝不硬编码域名**：客户端只保存并请求用户配置的 `https://实际域名`；不得显示/拼接群晖内部端口；示例域名（如 `salego.cn:8888`）仅讨论用，不得写入源码/安装包/Compose。
3. **Cloudflare Tunnel / 端口规则**：若 `cloudflared` 与 API 同 Docker 网络，Tunnel 直指 `http://mt-api:8080`，无需映射群晖端口；正式客户端只接受 HTTPS，仅调试版可连本机/局域网 HTTP。
4. **后台机密**：仅 `DATA_ENCRYPTION_KEY`、`DATABASE_PASSWORD`、`ADMIN_SETUP_TOKEN` 可放 Docker 环境变量/群晖 Secret；SMTP 与公开域名在网页后台填写。
5. **品牌**：对外统一叫“MT播放器”，统一 Logo（`logo-header-transparent.png`）与应用图标（`mtplayer-icon.png`）；深色红黑主题，不用平台默认白色控件；海报焦点只做轻微抬升/红色描边/阴影。
6. **免责声明**：安装程序、首次启动、关于页必须声明“不内置/不存储/不上传/不代理/不分发影视内容”，由用户自行添加配置源并自担版权责任。
7. **密钥安全**：`.secrets/` 下 `android-signing.properties`、`mtplayer-android-release.jks` 为签名密钥，**已被 .gitignore 忽略，禁止提交**；`DATA_ENCRYPTION_KEY` 等只走环境变量。

---

## 5. 当前代码状态（2026-08-03）

- **GitHub（`origin/main`）停在 v1.3.5**（最近提交 `530abf1` “point Synology compose to v1.3.5 image”，2026-07-23）。
- **本地工作区已与 `origin/main` 提交一致，但有大量未提交改动，实为 v1.3.6 开发中**：
  - 37 个已跟踪文件被修改（`+1806 / -204`）。
  - 36 个未跟踪文件：含 4 个新源码文件 + `release-builds/`（v1.3.5、v1.3.6 各平台产物）、`releases/`（APK）等构建产物。
  - 版本号已全面 bump：`installer/MTPlayer.iss`、`WebHtv.Desktop.csproj`、`MTPlayer.Server.csproj`、`android-fish2018/app/build.gradle` 均为 `1.3.6`。

> ⚠️ **重要**：v1.3.6 尚未提交到 GitHub。后续若在此工作区继续开发，务必先确认这些未提交改动的状态（提交 / 暂存 / 丢弃），再开始新工作，避免覆盖或混淆。

---

## 6. 本地未提交 v1.3.6 工作清单（按模块）

**Windows 桌面（`src/WebHtv.Desktop`）**
- `LivePlaylistService.cs`：**新增**完整直播列表服务——解析 M3U/M3U8/TXT 直播源，读取 `group-title`/`tvg-logo`/`tvg-id`、按 `#genre#` 分组；XMLTV EPG 富化（正在播放/即将播放）；按央视/卫视/地方/体育/影视/广播分类、自然排序、按名称+URL 去重、上限 3000；过滤“更新公告/免责声明”等噪声频道。直接修复此前“直播无法选台/接口内直播源不识别/某源删不掉”等 bug。
- `MainWindow.xaml(.cs)`：**在主窗口内集成直播播放**——新增 `NativePlaybackService _livePlayback`、直播频道分组/分类视图、`LiveSearch` 过滤、`LiveCategory` 切换、多源切换重试、直播播放错误提示、`IDisposable` 释放；新增 30 秒时钟。
- `PlayerWindow.xaml(.cs)`、`MovieDetailWindow.xaml(.cs)`、`AccountWindow.xaml`、`WebParserWindow.xaml.cs`、`ShellViewModel.cs`、`App.xaml.cs`、`AppSettingsStore.cs`、`WebHtv.Desktop.csproj`：播放器与账户/设置相关调整。
- `PlayerWindowLauncher.cs`：**新增**播放窗口启动辅助类。

**播放核心（`src/WebHtv.Playback`）**
- `NativePlaybackService.cs`：播放服务增强。
- `PlaybackTimeline.cs`：**新增**进度条指针→播放位置映射辅助（含边界/无限值保护）。

**Spider（`src/WebHtv.Spider`）**
- `SpiderGatewayProvider.cs`：**新增** `SpiderGatewayLiveChannel` 记录与 `ConfigureProfileAsync` / `GetLiveChannelsAsync`，使 Windows 可从 Android Spider Gateway 拉取直播频道；`ResolveGatewayResource` 解析网关资源。

**服务端 / 网页端（`src/MTPlayer.Server`）**
- `WebClient/WebClientGateway.cs`：**大幅重写（+291）**——新增 Spider Gateway 媒体代理：`AddGatewayLivesAsync`、`InitializeGatewayAsync`、`TryResolveGatewayMediaAddress`、`IsTrustedSpiderAddress`、`ReadHeaders`；`SignMedia` 支持带请求头；信任地址校验，确保网页端可安全代理/播放 Android 网关媒体。
- `wwwroot/css/web-client.css`：**+422** 网页端样式重写；`wwwroot/js/web-client.js` 微调；`Pages/Web/Index.cshtml` 调整。
- `MTPlayer.Server.csproj`、`MTPlayer.Client.Core/Settings/ClientSettings.cs`：版本与设置项更新。
- `tests/MTPlayer.Server.Tests/WebClient/WebClientSecurityTests.cs`：**新增** WebClient 安全测试（网关地址校验、签名等）。

**Android（`android-fish2018`、`android-tvbox`）**
- `MembershipClient.java`、`MembershipActivity.java`：**会员推送/更新推送**逻辑（v1.3.x 收尾）。
- `Updater.java`：在线更新能力增强（+79）。
- `server/Nano.java`、`catvod/.../OkProxySelector.java`：网关/代理相关适配。
- `android-fish2018/.../server/process/SpiderGateway.java`：**新增** Android 端 Spider Gateway 服务端进程。

**macOS（`src/MTPlayer.Mac`、`packaging/macos`）**
- `MTPlayer.Mac.csproj`、`MainWindow.axaml.cs`、`packaging/macos/Info.plist`、`build-bundle.ps1`：版本与打包脚本更新。

**部署 / 安装（`deploy/synology`、`installer`）**
- `deploy/synology/README*.md`、两个 `docker-compose*.yml`：v1.3.6 镜像与说明微调。
- `installer/MTPlayer.iss`：输出文件名与版本改为 `1.3.6`，安装目录 `C:\Program Files\mtplayer\`。

---

## 7. 如何继续开发

**本地构建（来自 README）**
```bash
# Windows
dotnet build .\src\WebHtv.Desktop\WebHtv.Desktop.csproj -c Release -r win-x64 -p:Platform=x64
dotnet test .\tests\MTPlayer.Server.Tests\MTPlayer.Server.Tests.csproj -c Release

# macOS
dotnet publish .\src\MTPlayer.Mac\MTPlayer.Mac.csproj -c Release -r osx-x64 --self-contained true

# Android（需 JDK 21 + SDK 37，android-fish2018/local.properties 配 sdk.dir 与签名）
cd android-fish2018
.\gradlew.bat :app:assembleLeanbackArm64_v8aRelease :app:assembleLeanbackArmeabi_v7aRelease :app:assembleMobileArm64_v8aRelease :app:assembleMobileArmeabi_v7aRelease
```

**服务端部署**：见 `deploy/synology/README.zh-CN.md`；Compose 不暴露公网端口，`cloudflared` 加入 `mtplayer` 网络后 Tunnel 源站填 `http://mt-api:8080`；首次访问 `https://你的域名/admin/setup` 用 `ADMIN_SETUP_TOKEN` 建管理员。

**开始新任务前请先**：
1. 确认 `git status` 中 37 个修改 + 36 个未跟踪项的处理方式（建议先提交或 stash v1.3.6 WIP，避免与你的新改动冲突）。
2. 阅读本项目 `docs/superpowers/specs/` 与 `docs/superpowers/plans/` 中的对应设计/计划文档。
3. 遵循第 4 节硬约束（尤其：不硬编码域名、密钥不提交、界面统一、可用优先）。

---

## 8. 风险与待办提示

- **v1.3.6 未提交**：当前本地领先 GitHub 一个完整版本，存在改动丢失/与远程冲突风险，应尽早整理提交。
- **未跟踪构建产物**：`release-builds/`、`releases/` 当前**未被 .gitignore 忽略**（仅未跟踪）。它们体积大、含二进制，建议加入 `.gitignore`，避免误提交污染仓库。
- **Android 签名密钥**在 `.secrets/`（已忽略），换机/重装需妥善保管；无 Apple 开发者证书时 macOS 只能出未签名 DMG，不能当正式发行版。
- **各端 UI 一致性**是用户历史高频投诉点，任何新功能都应尽量对齐网页端逻辑。

---

## 9. 参考文件索引（项目内）

- 设计规格：`docs/superpowers/specs/2026-07-14-mtplayer-cross-platform-account-sync-design.md`
- 实现计划：`docs/superpowers/plans/`（windows-sync / server-sync / android-clients / macos-client / release-acceptance 等 8 份）
- 发布说明：`docs/release-notes-v1.1.0.md`、`v1.3.0.md`、`v1.3.1.md`、`android-tvbox-v1.3.2.md`
- 兼容性矩阵：`docs/compatibility/tvbox-profile-matrix.md`
- 原始会话摘要：`docs/CODEX_SESSION_TRANSCRIPT_DIGEST.md`
- 部署说明：`deploy/synology/README.zh-CN.md`
- 本地 vs GitHub 差异报告：同目录 `LOCAL_VS_GITHUB_DIFF.md`
