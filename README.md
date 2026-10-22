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
- **启动前环境自检**（v0.2.0）：磁盘空间 / 内存配置 / Java 版本 / 版本安装状态四项检查，问题分级提示或阻断
- **启动崩溃保护**（v0.2.0）：启动器自身崩溃时写日志 + 弹原生错误框，不再静默退出
- **崩溃日志诊断**（v0.3.0）：游戏崩溃后自动分析日志，定位内存不足 / 显卡驱动 / Mod 冲突 / Java 版本 / 网络等根因并给出中文修复建议，也可在主页日志面板手动点击"诊断"
- **Java 运行时管理页**（v0.3.0）：系统 Java 检测、托管 JRE 列表与删除（游戏运行中自动保护）、手动下载任意主版本（含 Java 8/17/21 快捷按钮），安装版本时自动预下载所需 Java
- **启动可观测性**（v0.4.0）：从进程入口到主窗口显示的全程阶段日志（`~/.loya/logs/startup-*.log`）+ 全局未处理异常捕获；macOS / Linux 崩溃时同样弹原生错误框，不再静默退出
- **全平台稳定渲染**（v0.4.0）：启动画面去窗口级透明合成（无 compositor 的平台整个窗口不显示的回归已修复）；循环动画改 DispatcherTimer 驱动，规避 Avalonia 12 对循环动画的限制
- **Java 启动参数修正**（v0.4.0）：参数统一以裸值经 ArgumentList 传递（.NET 自动按需加引号），修复路径含空格时双重引号导致游戏目录/资源目录找不到的启动失败
- **进程生命周期**（v0.4.0）：游戏退出事件终态保证（取消 / 异常 / 正常退出都必定发布，UI 状态机可靠复位）；退出码与运行时长如实记录；后台监控任务持有引用并及时释放进程句柄

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
