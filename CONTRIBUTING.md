# 贡献指南

感谢你愿意为 LoyaLauncher 出力！本项目是长期项目，欢迎各类贡献。

## 开发环境

- [.NET 10 SDK](https://dotnet.microsoft.com/download)（版本由 `global.json` 锁定）
- 推荐 IDE：Visual Studio 2022+ / JetBrains Rider / VS Code + C# Dev Kit

## 提交前检查

```bash
dotnet build          # 必须零警告通过
dotnet test           # 全部测试必须通过
```

## 代码约定

- **分层依赖**：`Core ← Downloader ← {Java, Mods, Bedrock} ← Pack ← App`，禁止反向依赖
- **新增包**：一律进 `Directory.Packages.props` 统一版本管理，不要在 csproj 里写版本号
- **UI**：ViewModel 用 `CommunityToolkit.Mvvm` 源生成（`[ObservableProperty]` / `[RelayCommand]`），禁止在 ViewModel 里直接操作控件
- **新增用户可见文本**：必须进 `Assets/Lang/zh-CN.json`，XAML 用 `DynamicResource` 引用，不要硬编码中文字符串
- **样式**：沿用 `Styles/Glass.axaml` 玻璃拟态设计系统，不要另起炉灶
- **纯函数逻辑**：尽量写成可测的静态方法，并补充 xUnit 测试（参考 `tests/LoyaLauncher.Tests` 现有风格）

## 提交规范

- 一个 PR 只做一件事
- Commit message 用英文祈使句，如 `Add Quilt loader installation`
- 涉及用户可见行为的改动，请同步更新 README

## 提交 PR

1. Fork 并创建特性分支：`git checkout -b feat/your-feature`
2. 提交改动并推送到你的 Fork
3. 发起 Pull Request，说明动机与测试情况
