# 本地（webhtv-windows） vs GitHub（meiyemeng/MTplayer）差异报告

> 生成日期：2026-08-03
> 比较对象：本地工作区 `D:\work\webhtv-windows` ↔ 远程 `origin/main`（`https://github.com/meiyemeng/MTplayer`）
> 配套文档：同目录 `CODEX_DEV_CONTEXT.md`（Codex 开发上下文记录）

---

## 1. 结论摘要

| 维度 | 结论 |
|------|------|
| 已提交状态 | 本地 `HEAD` 与 `origin/main` **完全一致**，均停在 **v1.3.5**（提交 `530abf1`，2026-07-23）。 |
| 核心差异 | 本地有 **37 个已跟踪文件被修改**（`+1806 / -204` 行），且**未提交**；另有 **36 个未跟踪文件**。 |
| 本质 | 本地领先 GitHub 一个完整开发版本 —— 实为 **v1.3.6 开发中**（版本号已全面 bump 至 1.3.6）。 |
| 文件结构 | 目录结构层面本地与 GitHub 基本一致；差异集中在**源码内容**与**新增 4 个源文件 + 构建产物**。 |
| 配置 | App/Installer/Compose/Info.plist 等版本字符串已升至 1.3.6；新增 Spider Gateway 媒体代理配置、直播列表服务配置。 |

> **一句话**：GitHub 上是 v1.3.5 正式版；本地已做出 v1.3.6 的大量改动（网页端重写、Windows 直播播放、Spider Gateway 拉直播、Android 会员/网关、macOS 打包、服务端安全测试），但尚未推送。

---

## 2. 提交状态对比

```text
本地分支 main:  up to date with 'origin/main'   (committed tree 相同)
origin/main:    530abf1 chore(deploy): point Synology compose to v1.3.5 image  (Jul 23)
本地未提交:     37 modified (+1806/-204)  +  36 untracked
```

因此“本地 vs GitHub 的源码差异”= 上述未提交改动全集（见第 3、4 节）。

---

## 3. 未提交改动清单（37 个已跟踪文件，按模块）

### Windows 桌面 `src/WebHtv.Desktop`（改动最重）
| 文件 | 行变化 | 内容 |
|------|--------|------|
| `LivePlaylistService.cs` | +120 | **新增**完整直播列表服务（M3U/TXT 解析、XMLTV EPG、分类去重） |
| `MainWindow.xaml.cs` | +178 | 主窗口内集成直播播放（频道分组/分类/多源重试/错误提示/释放） |
| `MainWindow.xaml` | +112 | 直播相关界面布局 |
| `PlayerWindow.xaml.cs` | +77 | 播放器逻辑调整 |
| `App.xaml.cs` | +50 | 启动/初始化逻辑 |
| `ShellViewModel.cs` | +74 | 主视图模型 |
| `PlayerWindow.xaml` | +23 | 播放器界面 |
| `MovieDetailWindow.xaml.cs` | +20 | 详情逻辑 |
| `AccountWindow.xaml` | +16 | 账户界面 |
| `MovieDetailWindow.xaml` | +22 | 详情界面 |
| `WebParserWindow.xaml.cs` | +8 | 解析窗口 |
| `AppSettingsStore.cs` | +2 | 设置存储 |
| `WebHtv.Desktop.csproj` | +10 | 版本 1.3.6 / 依赖 |

### 播放核心 `src/WebHtv.Playback`
| `NativePlaybackService.cs` | +56 | 播放服务增强 |

### Spider `src/WebHtv.Spider`
| `SpiderGatewayProvider.cs` | +124 | 从 Android Spider Gateway 拉取直播频道 |

### 服务端 / 网页端 `src/MTPlayer.Server` + `MTPlayer.Client.Core`
| `WebClient/WebClientGateway.cs` | +291 | **大幅重写**：Spider Gateway 媒体代理、签名带请求头、信任地址校验 |
| `wwwroot/css/web-client.css` | +422 | 网页端样式重写 |
| `wwwroot/js/web-client.js` | +7 | 前端脚本微调 |
| `Pages/Web/Index.cshtml` | +23 | 网页入口页 |
| `MTPlayer.Server.csproj` | +8 | 版本 1.3.6 |
| `MTPlayer.Client.Core/Settings/ClientSettings.cs` | +2 | 设置项 |

### Android `android-fish2018` / `android-tvbox`
| `android-fish2018/.../membership/MembershipClient.java` | +124 | 会员推送/更新推送 |
| `android-fish2018/.../Updater.java` | +79 | 在线更新增强 |
| `android-tvbox/.../MembershipActivity.java` | +15 | 会员界面 |
| `android-fish2018/.../server/Nano.java` | +2 | 网关适配 |
| `android-fish2018/catvod/.../OkProxySelector.java` | +3 | 代理选择 |
| `android-fish2018/app/build.gradle` | +4 | versionName 1.3.6 |

### macOS `src/MTPlayer.Mac` / `packaging/macos`
| `MTPlayer.Mac.csproj` | +8 | 版本 1.3.6 |
| `MainWindow.axaml.cs` | +2 | 微调 |
| `packaging/macos/Info.plist` | +4 | 版本 |
| `packaging/macos/build-bundle.ps1` | +4 | 打包脚本 |

### 部署 / 安装 `deploy/synology` / `installer`
| `installer/MTPlayer.iss` | +4 | 输出名/版本 1.3.6、安装目录 `C:\Program Files\mtplayer\` |
| `deploy/synology/README.zh-CN.md` | +2 | 说明 |
| `deploy/synology/README-existing-postgres.zh-CN.md` | +4 | 说明 |
| `deploy/synology/docker-compose.release.yml` | +2 | 镜像版本 |
| `deploy/synology/docker-compose.existing-postgres.yml` | +2 | 镜像版本 |

### 测试 `tests`
| `tests/MTPlayer.Server.Tests/WebClient/WebClientSecurityTests.cs` | +106 | **新增** WebClient 安全测试（网关地址校验、签名） |

---

## 4. 未跟踪文件（36 个）

**新增源码（4 个，应随 v1.3.6 提交）**
- `src/WebHtv.Desktop/PlayerWindowLauncher.cs`（播放窗口启动辅助）
- `src/WebHtv.Playback/PlaybackTimeline.cs`（进度条指针→位置映射）
- `android-fish2018/app/src/main/java/com/fongmi/android/tv/server/process/SpiderGateway.java`（Android 端 Spider Gateway 进程）
- `docs/CODEX_DEV_CONTEXT.md` 与 `docs/LOCAL_VS_GITHUB_DIFF.md`（本批次新增的开发参考文档）

**构建产物（建议加入 .gitignore，勿提交）**
- `release-builds/v1.3.5/`：4 个 Android APK + SHA256SUMS + 2 个 compose yml（8 项）
- `release-builds/v1.3.6/`：各平台安装包/镜像（Windows x64/x86 exe、macOS arm64/x64 tar.gz、Android 4 APK、Server Web tar、docker-compose、README、SHA256SUMS、leanback/mobile.json 等，15 项）
- `releases/`：Android TV/手机 APK + idsig + SHA256SUMS（7 项）

---

## 5. 功能实现差异（GitHub v1.3.5 → 本地 v1.3.6）

1. **网页端重写**：`web-client.css` +422、`WebClientGateway.cs` +291，网页播放器与代理通道大幅重构，并新增安全测试。
2. **Windows 直播播放**：`LivePlaylistService.cs` 全新直播列表引擎 + `MainWindow` 内联直播播放，修复“无法选台/接口内直播源不识别/某源删不掉”等历史 bug。
3. **Spider Gateway 直播贯通**：Windows `SpiderGatewayProvider` 与 Android `SpiderGateway.java` 打通，Windows 可直接拉取并播放 Android 网关提供的直播频道；服务端 `WebClientGateway` 增加带信任校验的网关媒体代理。
4. **Android 会员/更新推送**：`MembershipClient`/`MembershipActivity`/`Updater` 完善会员等级、配置/直播源推送与在线更新。
5. **macOS 打包**：`Info.plist` 与 `build-bundle.ps1` 版本与打包更新。
6. **播放体验微调**：`PlayerWindow`、`MovieDetailWindow`、`NativePlaybackService`、`PlaybackTimeline` 等播放/进度相关优化（与“片头片尾/续播”诉求一致）。

---

## 6. 配置差异

| 配置项 | GitHub (v1.3.5) | 本地 (v1.3.6) |
|--------|----------------|---------------|
| Windows `WebHtv.Desktop.csproj` Version | 1.3.5 | **1.3.6** |
| Server `MTPlayer.Server.csproj` Version | 1.3.5 | **1.3.6** |
| Android `build.gradle` versionName | 1.3.5 | **1.3.6** |
| Installer 输出名 | MT播放器-Setup-1.3.5 | **MT播放器-Setup-1.3.6** |
| macOS `Info.plist` | 1.3.5 | **1.3.6** |
| Synology Compose 镜像 | 1.3.5-amd64 | **1.3.6-amd64** |
| 新增：直播列表服务 | 无 | `LivePlaylistService` 接入 MainWindow |
| 新增：Spider Gateway 媒体代理 | 无 | `WebClientGateway` 网关代理 + 安全测试 |
| 新增：Android SpiderGateway 进程 | 无 | `server/process/SpiderGateway.java` |

---

## 7. 风险与建议

1. **⚠️ v1.3.6 未提交**：本地领先远程一个完整版本。建议先整理提交（或暂存）再开始新开发，避免覆盖/冲突。若打算继续本地迭代，可直接在此基础上工作；若准备发布，应先 `git add` 相关源码（**排除 `release-builds/`、`releases/` 二进制产物**）并提交、推送。
2. **构建产物未忽略**：`release-builds/`、`releases/` 当前未被 `.gitignore` 覆盖（仅未跟踪）。建议将这两个目录加入 `.gitignore`，防止误提交污染仓库。
3. **密钥安全**：`.secrets/`（Android 签名 jks / properties）已被忽略，禁止提交；服务端机密仅走环境变量。
4. **完整性自测**：用户反复强调“不要测试版”。提交/发布前应按 `docs/superpowers/specs/...` 第 17 节验收清单（搜索不闪退、直播可选台、各端界面一致、断开 API 仍可本地播放等）做一遍验证。

---

## 8. 后续开发基线

后续任何开发都应基于 `docs/CODEX_DEV_CONTEXT.md` 中记录的：需求演进、架构决策、硬约束（不硬编码域名、密钥不提交、UI 统一、可用优先）与当前 v1.3.6 工作清单。开始新任务前先 `git status` 确认未提交改动状态。
