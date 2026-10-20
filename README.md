# LoyaLauncher

> 让每一位 Minecraft 玩家都能玩上任意版本 —— 傻瓜式操作的第三方启动器。

LoyaLauncher 是一个使用 **Avalonia + .NET 10** 构建的跨平台（Windows / macOS / Linux）Minecraft 长期项目启动器。玻璃拟态界面、流畅页面过渡、离线即玩，致力于还原并超越 PCL2 的"傻瓜式"体验。

![.NET](https://img.shields.io/badge/.NET-10.0-512BD4) ![Avalonia](https://img.shields.io/badge/Avalonia-12.1-8B5CF6) ![License](https://img.shields.io/badge/License-MIT-green) ![Platform](https://img.shields.io/badge/Platform-Win%20%7C%20macOS%20%7C%20Linux-blue)

## ✨ 功能特性

### 已实现
- **离线模式即玩**：自定义用户名 + 皮肤，UUID 严格遵循 Mojang 离线规范（`MD5("OfflinePlayer:" + name)`），无需任何账号
- **Java 版全版本支持**：从远古 `rd-132211` 到最新正式版/快照，自动安装（客户端 jar → libraries → natives → assets 五步流程，幂等可续）
- **双源下载引擎**：BMCLAPI 镜像 + Mojang 官方源自动互备，`.part` 断点续传、SHA1 校验、并发下载、实时进度
- **版本继承链合并**：快照继承正式版的 JSON 自动合并，library 去重
- **自动 Java 管理**：按版本自动推断所需 JDK（1.16→8 / 1.17→16 / 1.18-1.20.4→17 / 1.20.5+→21），优先使用系统 Java，缺失则自动下载 Temurin
- **实例系统**：一套版本可创建多个隔离实例，各自独立的 mods / config / saves
- **四大加载器**：Fabric / Forge / NeoForge / Quilt 一键安装
- **Mod 生态**：Modrinth 搜索、安装、启用 / 禁用、本地 jar 导入
- **整合包**：一键导出实例为 `.zip` 整合包分享；导入即重建实例（含防目录穿越校验）
- **基岩版（Windows 完整支持）**：官方版本数据库浏览、UWP 包安装与启动
- **自动更新**：基于 GitHub Releases，检查 → 下载 → 重启自动应用，三平台一致的"下次启动安装"策略
- **设计系统**：深空渐变底 + 半透明磨砂卡片 + 渐变强调色，页面切换带 PageSlide + CrossFade 过渡动画
- **本地化框架**：语言资源外置 JSON，运行时切换

### 路线图
- 微软 OAuth 正版登录（接口已预留）
- authlib-injector 外置登录（模型已预留）
- 基岩版 macOS / Linux 实验性支持

## 🚀 快速开始

### 环境要求

- [.NET 10 SDK](https://dotnet.microsoft.com/download)（`global.json` 锁定 10.0.402）

### 构建运行

```bash
git clone https://github.com/Easy-Dioxide/LoyaLauncher.git
cd LoyaLauncher
dotnet run --project src/LoyaLauncher.App
```

### 运行测试

```bash
dotnet test
```

## 🏗️ 架构

```
src/
├── LoyaLauncher.Core        # 领域层：路径管理、配置、事件总线、实例模型
├── LoyaLauncher.Downloader  # 双源下载引擎（断点续传/SHA1/并发）
├── LoyaLauncher.Java        # Java 版安装与启动（版本 JSON/继承合并/libraries/JRE/进程）
├── LoyaLauncher.Auth        # 账户系统（离线 UUID 规范 / 外置登录占位）
├── LoyaLauncher.Mods        # Mod 生态（Modrinth / 四加载器安装）
├── LoyaLauncher.Bedrock     # 基岩版模块（Windows UWP）
├── LoyaLauncher.Pack        # 整合包导入导出 + GitHub Releases 自动更新
└── LoyaLauncher.App         # Avalonia UI（MVVM + DI + 玻璃拟态设计系统）
tests/
└── LoyaLauncher.Tests       # xUnit 单元测试（UUID 向量 / Rules / Java 映射 / 启动参数 / 整合包 round-trip）
```

**技术要点**：MVVM（CommunityToolkit.Mvvm 源生成）、依赖注入（Microsoft.Extensions.DependencyInjection）、中央包管理（Directory.Packages.props）、事件总线解耦 UI 与下载/游戏进程。

## 📦 发布

推送 `v*` 标签自动触发三平台构建，产出与更新服务约定的安装包：

```bash
git tag v0.1.0
git push origin v0.1.0
```

发布产物命名：`LoyaLauncher-{rid}.zip`（`win-x64` / `linux-x64` / `osx-x64` / `osx-arm64`）。

## ⚠️ 免责声明

本项目与 Mojang / Microsoft 无任何关联。Minecraft 是 Mojang Synergies AB 的商标。请支持正版游戏。使用本启动器下载的游戏资源版权归 Mojang 所有。

## 📄 License

[MIT](LICENSE) © LoyaLauncher (Dioxide Industries, LLC.) Contributors
