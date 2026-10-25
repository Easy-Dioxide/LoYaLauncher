# LoyaLauncher 0.8.1 — Windows 下载回归热修

> 0.8.0 在 Windows 上存在严重回归：**每次文件下载都会失败**（重试耗尽后报错）。
> 根因是 0.8.0 引入的 `File.Move` 在文件流未释放时执行——POSIX 允许移动打开中的文件，
> Windows 抛共享冲突。本版修复该回归，并顺带修复 CI 在三平台上的测试侧缺陷。
> **已安装 0.8.0 Windows 包的用户请务必升级。**

## 修复

- **Windows 下载失败（P0 回归）**：`DownloadManager.DownloadToFileAsync` 原先用声明式
  `await using var fileStream`，文件流直到方法结束才释放，而块内紧接着
  `File.Move(partPath, destinationPath, overwrite: true)`。Linux/macOS 允许移动打开中的
  文件故一直通过；Windows 抛 `IOException: 正在被另一个进程使用`，被判定为可重试的
  传输故障 → 重试耗尽 → `DownloadFailed`。改为 `await using` 语句块 + 块内
  `FlushAsync`，确保先释放后移动。
- **测试侧缺陷（不影响运行时，但导致 CI 三平台全红）**：
  - 手搓 tar 的 mode 字段全零，`ExtractToFile` 产出无读权限文件，非 root 用户读取即
    `UnauthorizedAccessException`（本地 root 开发环境会掩盖）——补 0644 并加回归断言；
  - 资产选择测试断言写死 `linux-x64`，macOS 上无精确匹配走回退分支即失败——改为
    平台感知（注入当前平台资产 + 平台相关回退用例）；
  - JreManager 测试的假 java 是 shell 脚本：Windows 上 `GetManagedJavaPath` 查找
    `java.exe` 找不到，且 `Process.Start(UseShellExecute=false)` 无法执行脚本；
    `File.SetUnixFileMode` 在 Windows 抛 `PlatformNotSupportedException`——
    假 JRE 增加 Windows `java.exe` 条目；新增可注入版本探针（与 systemJavaProbe 同模式），
    使下载→解压→查找→校验完整链路在三平台真实可测；Unix 文件模式 API 按平台守卫。

## 测试

- 本地 296 全绿；Windows/macOS/ubuntu 三平台 CI 全绿后发布。
- 新增回归防护：提取文件必须可读（Unix 模式断言）；Windows 分支覆盖
  「找到 java.exe 但无法执行 → 校验失败不静默」。
