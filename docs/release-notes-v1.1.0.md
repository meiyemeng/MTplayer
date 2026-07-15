# MT播放器 1.1.0

发布日期：2026-07-15

## 安装文件

- Windows：x64/x86 自动识别安装包，以及 x64、x86 便携单文件
- Android 手机：正式 Release 签名 APK，Android 8.0+
- Android TV：正式 Release 签名 APK，Android TV 8.0+
- macOS：Intel x64 `.app.zip` 与可挂载 DMG，Apple Silicon 可通过 Rosetta 2 运行
- 群晖：Linux amd64 Docker 镜像 TAR 与一键 Compose

## 本版内容

- 多配置源命名、启用、停用和聚合搜索
- 中文海报墙、影片详情、接口/线路/剧集选择
- 原生播放、进度、静音、音量、倍速、全屏和上下集
- 每部影片独立设置跳过片头和片尾
- 收藏、观看记录、直播入口和本地持久化
- Windows、Android 手机、Android TV、macOS 均接入账户入口
- 群晖后台提供开放注册、邮箱验证/找回参数、用户管理和设备码登录

## 校验与限制

- Android 手机和 TV APK 使用同一 4096 位 Release 证书签名；证书 SHA-256：`112de34fde1d8d803623fae5afa70ae0999cb56dfa17864acf45d5e62c5c0cf4`
- macOS 包为 Intel x64 架构，已完成交叉编译和 Mach-O/包结构校验，但未使用 Apple Developer ID 签名或 Apple 公证，也未在实体 Mac 上执行播放测试
- Android 与 macOS 当前支持标准 CMS API；JAR/JS Spider 运行时仅在 Windows 提供
- Android 与 macOS 初始配置为空；Windows 显示委托方指定的可删除配置组入口，软件本身不内置影视文件
