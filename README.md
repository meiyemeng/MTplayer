# MT播放器 for Windows

MT播放器是面向 Windows 10/11 的原生 WPF 影视播放器，支持鼠标与键盘操作，并提供 x64、x86 单文件版本和安装程序。

## 功能

- TVBox 单仓/多仓配置；可命名保存多个配置源并切换启用
- 多片源搜索、电影/电视剧/动漫电影/动漫番剧/综艺 Top 10
- 影片详情、可用片源接口预检（自动隐藏无片或失效接口）、播放线路与剧集选择
- LibVLC 原生播放、硬件解码、进度、静音/音量、倍速、全屏、上下集
- 每部影片独立设置跳过片头与片尾
- 收藏、观看记录、续播
- M3U/M3U8/TXT 直播源、`tvg-logo` 台标、可选 XMLTV 节目预告
- 深色中文界面和本机数据持久化

## 构建

```powershell
dotnet build .\src\WebHtv.Desktop\WebHtv.Desktop.csproj -c Release -r win-x64 -p:Platform=x64
dotnet run --project .\tests\WebHtv.Configuration.IntegrationChecks\WebHtv.Configuration.IntegrationChecks.csproj -c Release
```

安装脚本位于 `installer/MTPlayer.iss`，发布时会显示 `installer/DISCLAIMER.txt`，并创建卸载程序与可选桌面快捷方式。

## 内容与版权

本项目不预置、不存储、不上传、不分发影视内容。节目资料、播放链接、直播地址、台标与节目单来自用户自行添加的第三方配置；版权归相应权利人所有。用户应在获得授权并符合所在地法律法规的前提下使用。
