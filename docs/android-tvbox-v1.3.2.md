# Android 客户端：TVBoxOS 原生基础（v1.3.2）

从 v1.3.2 起，MT播放器 Android 客户端的正式基础为
`android-tvbox/`，而非旧的 Compose 原型目录 `android/`。

它直接基于 IsayIsee/TVBoxOS 的原生配置解析、JAR Spider、JS Spider、
直播、详情、选集与播放器链路修改，因此不再把 TVBox 的 JAR/CSP 站点错误
归类为“仅 HTTP 站点”。

MT 的改动：

- Android 应用名、包名和启动图标替换为 MT播放器；
- 设置页新增“账户与会员推送”，单击进入；
- 会员可从现有 MT 服务器接收配置源、直播源和 Android 更新推送；
- 剧集单击直接调用播放，未引入双击确认步骤；
- Release APK 同时含 `armeabi-v7a` 与 `arm64-v8a`。

构建需 JDK 17 与 Android SDK Platform 33。完整许可和对应源码位于
`android-tvbox/LICENSE` 和 `android-tvbox/` 中。
