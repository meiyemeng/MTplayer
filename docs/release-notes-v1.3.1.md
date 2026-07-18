# MT播放器 v1.3.1

## 主要更新

- Android 手机与 Android TV 新增本地 DEX/JAR `csp_*` Spider 运行时，可读取此前显示为“0 个站点可用”的 Android TVBox 配置。
- 修复正式版 R8 裁剪 Spider、Gson、OkHttp 运行依赖的问题；`https://6800.kstore.vip/fish.json` 已完成清空数据后的首次搜索、详情和播放验证。
- Spider JAR 使用独立的首次下载超时，86 站点并发搜索改为统一截止时间，避免首次导入时长时间无结果。
- Android 设置页新增带随机令牌认证的局域网 Spider Gateway；Windows 与群晖网页端可连接网关，调用同一批 Android 专用站点。
- 修复 Windows/网页端向 Android Gateway 传递 `searchable` 类型不兼容、异常时连接被提前关闭的问题。
- Spider Gateway 已完成真实配置搜索、详情、选集和播放地址解析验证；无效请求会立即返回 400，不再长时间卡住。
- 新增解析器网页播放通道：Spider 返回 `parse=1` 时，Android 使用内置全屏 WebView，Windows 使用 WebView2，网页端打开独立解析页。
- 修复网页服务器依赖注入构造函数冲突造成的 500 错误。
- 修复配置源切换或刷新为空时仍残留上一配置海报、接口和直播频道的问题。
- 修复 Android/网页端搜索时未尊重 `searchable=0` 的问题。
- 保持 TV 包名、升级签名与 v1.3.0 一致，可覆盖安装并保留本地收藏、历史和设置。
- Windows x64/x86、Android 手机、Android TV、macOS、服务器与网页端统一版本号为 1.3.1。

## Spider Gateway 使用方法

1. 在 Android 手机或 Android TV 的设置页开启 Spider Gateway。
2. 记下设备显示的端口（默认 9978）和随机令牌。
3. Windows 设置填写 `http://安卓设备局域网IP:9978` 和令牌。
4. 群晖 `.env` 填写 `SPIDER_GATEWAY_URL`、`SPIDER_GATEWAY_TOKEN`，然后重新创建服务器容器。

网关默认关闭、仅用于可信局域网，所有请求都必须携带令牌。请勿将 9978 端口直接暴露到公网。

## 兼容性说明

第三方 Spider 由配置提供方维护。仅包含 ARM 原生库的插件无法在 x86/x64 Android 模拟器上运行，但可在匹配架构的真实手机或电视上运行；纯 DEX/JAR Spider 不受该限制。Python Spider 与依赖缺失私有组件的插件仍不属于本版本支持范围。

## 隐私与内容说明

软件不预置、不存储、不上传、不分发任何影视内容。Spider Gateway 只在用户自己的 Android 设备上执行用户自行添加的配置，服务器仅转发必要的搜索、详情和播放解析请求。
