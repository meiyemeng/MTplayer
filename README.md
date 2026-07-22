# MT播放器

MT播放器是一套不内置影视内容的多平台影视播放器与账户同步服务。当前源码包含 Windows、Android 手机、Android TV、macOS 和网页客户端，以及可部署到群晖 Docker 的账号、同步与网页播放服务。

## 已交付平台

| 平台 | 安装要求 | 主要能力 |
| --- | --- | --- |
| Windows | Windows 10 / 11，x64 或 x86 | WPF 原生桌面端、Spider/CMS 配置、多源搜索、海报墙、LibVLC 播放、直播、收藏/历史、登录同步 |
| Android 手机 | Android 7.0+ | 基于 fish2018 260720-16 手机版的原界面与操作逻辑、完整 Spider/直播/播放能力、MT 账户与云端配置 |
| Android TV | Android TV 7.0+ | 基于 fish2018 260720-16 电视版的原界面与遥控器逻辑、完整 Spider/直播/播放能力、MT 账户与云端配置 |
| macOS | macOS 10.15+，Intel x64；Apple Silicon 可使用 Rosetta 2 | Avalonia 桌面界面、CMS 配置、多源搜索、详情/选集、LibVLC 播放、收藏/历史、登录同步 |
| 网页 | Chrome、Edge、Safari、Firefox 的现代版本 | 多配置源、Top 片单、跨源搜索、详情/选集、HLS 播放、直播、收藏/历史、注册登录与双向同步 |

Android 手机与 Android TV 既可读取标准 TVBox HTTP/CMS 接口，也可在设备本地运行 Android DEX/JAR `csp_*` Spider。Windows 和网页客户端除自身支持的 HTTP/CMS 接口外，还可连接同一局域网内 Android 客户端提供的 Spider Gateway，调用这些 Android 专用站点。
macOS 客户端当前读取标准 TVBox `type=1` / CMS API。Python Spider、仅原生 ARM 库可运行的 Spider，以及不兼容当前设备 CPU 的第三方插件仍会被明确标记为不可用，不会伪装成可播放接口。

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
- 网页客户端与账号服务同域部署，访问 `https://你的域名/player`；配置读取和媒体请求由服务端安全代理，解决浏览器跨域限制
- Android 设置页可开启“Spider Gateway”，Windows 设置页或服务器 Compose 填入设备局域网地址与随机令牌后，可搜索、读取详情并播放 Android DEX/JAR `csp_*` 站点

## 源码结构

- `src/WebHtv.Desktop`：Windows WPF 客户端
- `src/MTPlayer.Mac`：macOS Avalonia 客户端
- `android-fish2018`：当前 Android 手机与电视客户端，基于 fish2018 260720-16 完整源码，仅增加 MT 品牌、账户、云端配置、会员资源与 GitHub 在线更新
- `android`：早期自研 Android 客户端，保留作历史兼容，不再作为当前发布源
- `src/MTPlayer.Server`：账户、设备码、同步、网页客户端与管理后台
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

安装 JDK 21 与 Android SDK 37，在 `android-fish2018/local.properties` 中配置 `sdk.dir`。正式 Release 同时配置 `storeFile`、`keyAlias`、`storePassword` 和 `keyPassword`；私钥与密码不会提交到仓库。

```powershell
cd android-fish2018
.\gradlew.bat :app:assembleLeanbackArm64_v8aRelease :app:assembleLeanbackArmeabi_v7aRelease :app:assembleMobileArm64_v8aRelease :app:assembleMobileArmeabi_v7aRelease
```

`.github/workflows/release-android-fish2018.yml` 会用同一发布证书构建四个 APK、发布到 GitHub Release，并更新 `updates/android` 清单。首次从上游 fish2018 APK 切换到 MT播放器时，因为上游私钥不可取得，需要先卸载上游包；此后的 MT播放器版本可直接在线覆盖更新。

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

后台用户管理会显示最近登录 IP、所在城市，以及用户保存的配置源、影视接口和直播接口数量与地址。Cloudflare Tunnel 建议开启“添加访问者位置标头”托管转换，以便服务直接读取 `CF-IPCity`；未提供该标头时，服务会对公网 IP 使用可配置的地理位置服务兜底查询。

v1.3.1 保留会员统一推送接口：管理员可通过 `/api/v1/admin/members/{userId}` 设置 `free`、`member`、`vip` 等级，通过 `/api/v1/admin/member-pushes` 管理配置源与直播源推送；登录客户端通过 `/api/v1/member/pushes` 获取当前等级可见内容。

## 内容、版权与许可证

本项目不预置、不存储、不上传、不分发影视内容。节目资料、播放链接、直播地址、台标与节目单来自用户自行添加的第三方配置；版权归相应权利人所有。用户应在获得授权并符合所在地法律法规的前提下使用。

Windows 首次运行会显示项目委托方指定的配置组入口，用户可在设置中停用、删除或替换；Android 与 macOS 初始配置为空。配置入口不代表项目对第三方内容的授权、可用性或合法性作出保证。

仓库尚未选择统一的公开发布许可证，详见 [`LICENSE-STATUS.md`](LICENSE-STATUS.md)。在公开仓库或二次分发前，应完成第三方依赖与来源清单，并保留各组件要求的许可证和声明。

## 支持项目

如果 MT播放器对你有帮助，可以自愿使用支付宝捐助，支持后续维护。捐助完全自愿，不影响软件任何基础功能。

<p align="center">
  <img src="docs/assets/alipay-donate.png" alt="支付宝捐助二维码" width="360" />
</p>
