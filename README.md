# MT播放器

MT播放器是一套不内置影视内容的多平台影视播放器与账户同步服务。当前源码包含 Windows、Android 手机、Android TV、macOS 客户端，以及可部署到群晖 Docker 的账号和同步后台。

## 已交付平台

| 平台 | 安装要求 | 主要能力 |
| --- | --- | --- |
| Windows | Windows 10 / 11，x64 或 x86 | WPF 原生桌面端、Spider/CMS 配置、多源搜索、海报墙、LibVLC 播放、直播、收藏/历史、登录同步 |
| Android 手机 | Android 8.0+ | 触屏界面、CMS 配置源、多源搜索、详情/选集、Media3 播放、收藏/历史、登录同步 |
| Android TV | Android TV 5.0+ | Leanback 启动、遥控器焦点导航、设备码登录、详情/选集、Media3 播放；APK 使用 v1+v2 双签名兼容旧电视 |
| macOS | macOS 10.15+，Intel x64；Apple Silicon 可使用 Rosetta 2 | Avalonia 桌面界面、CMS 配置、多源搜索、详情/选集、LibVLC 播放、收藏/历史、登录同步 |

Android 与 macOS 客户端当前读取标准 TVBox `type=1` / CMS API；JAR/JS Spider 运行时由 Windows 客户端提供。

## 播放与资料功能

- 可命名保存多个 TVBox 单仓/多仓配置源，并分别启用或停用
- 多接口聚合搜索、电影/电视剧/动漫电影/动漫番剧/综艺片单
- 影片介绍、有效播放接口、线路和剧集选择
- 进度、静音/音量、倍速、全屏、上下集与每部影片独立片头片尾设置
- 收藏、观看记录和本地续播数据
- Windows 支持 M3U/M3U8/TXT 直播源、`tvg-logo` 台标与可选 XMLTV 节目单
- 游客可完整使用本地播放；登录后才启用跨设备同步
- Android 手机配置源和直播流同时支持 `http://` 与 `https://`；账户服务器仍强制 HTTPS 以保护密码和登录令牌
- Windows 当前配置源会在添加后、启动时和每 20 分钟自动刷新，也可在设置中立即更新

## 源码结构

- `src/WebHtv.Desktop`：Windows WPF 客户端
- `src/MTPlayer.Mac`：macOS Avalonia 客户端
- `android/mobile`：Android 手机客户端
- `android/tv`：Android TV 客户端
- `android/core`：Android 共用配置、CMS、搜索与账户逻辑
- `src/MTPlayer.Server`：账户、设备码、同步与管理后台
- `deploy/synology`：群晖 Docker Compose、备份与恢复脚本
- `installer`：Windows Inno Setup 安装脚本和免责声明
- `packaging/macos`：macOS `.app` 打包脚本

## 本地构建

### Windows

```powershell
dotnet build .\src\WebHtv.Desktop\WebHtv.Desktop.csproj -c Release -r win-x64 -p:Platform=x64
dotnet test .\tests\MTPlayer.Server.Tests\MTPlayer.Server.Tests.csproj -c Release
```

### Android

安装 JDK 17+ 与 Android SDK 35，复制 `android/local.properties.example` 为 `android/local.properties`。正式 Release 还需复制 `android/keystore.properties.example`，填写自己的私有签名密钥；私钥和密码不会提交到仓库。

```powershell
cd android
.\gradlew.bat :core:test :mobile:assembleDebug :tv:assembleDebug
```

如果电视上已有签名不同但包名相同的旧版，可构建独立包名的共存版，避免卸载旧应用：

```powershell
.\gradlew.bat :tv:assembleRelease -PtvApplicationId=cn.mtplayer.tv.compat -PtvAppLabel="MT播放器 TV 共存版"
```

### macOS

```powershell
dotnet publish .\src\MTPlayer.Mac\MTPlayer.Mac.csproj -c Release -r osx-x64 --self-contained true
```

未使用 Apple Developer ID 签名和 Apple 公证的包，首次打开时需按 macOS 的安全提示手动允许。正式公开分发前，应在 Mac 上使用开发者证书完成签名、公证和最终真机播放验证。

## 群晖与 Cloudflare Tunnel

部署说明见 [`deploy/synology/README.zh-CN.md`](deploy/synology/README.zh-CN.md)。Compose 默认不暴露公网端口；让 `cloudflared` 加入 `mtplayer` 网络后，Tunnel 源站直接填写：

```text
http://mt-api:8080
```

首次部署访问 `https://你的域名/admin/setup`，使用 `.env` 中的 `ADMIN_SETUP_TOKEN` 创建管理员；随后在后台填写真实公开 HTTPS 地址及 SMTP 参数。

## 内容、版权与许可证

本项目不预置、不存储、不上传、不分发影视内容。节目资料、播放链接、直播地址、台标与节目单来自用户自行添加的第三方配置；版权归相应权利人所有。用户应在获得授权并符合所在地法律法规的前提下使用。

Windows 首次运行会显示项目委托方指定的配置组入口，用户可在设置中停用、删除或替换；Android 与 macOS 初始配置为空。配置入口不代表项目对第三方内容的授权、可用性或合法性作出保证。

仓库尚未选择统一的公开发布许可证，详见 [`LICENSE-STATUS.md`](LICENSE-STATUS.md)。在公开仓库或二次分发前，应完成第三方依赖与来源清单，并保留各组件要求的许可证和声明。
