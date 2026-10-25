# LoyaLauncher 0.7.1 — 网络修复记录

## 本次已修改

1. **Modrinth API 路径**：BaseAddress 使用带尾斜杠的 `/v2/`，请求使用相对路径，避免以 `/search` 开头时丢失 `/v2` 前缀。
2. **Modrinth 搜索参数**：`facets` 改为 API 要求的字符串数组分组格式，不再发送 JSON 对象。
3. **Modrinth 版本筛选**：使用 `game_versions` 与 `loaders` JSON 数组参数，不再错误地把它们作为 `facets` 传递。
4. **Modrinth 诊断**：请求超时限制为 25 秒；HTTP 非成功状态会显示状态码及有限长度的响应正文。
5. **Minecraft 官方版本 JSON**：优先按所配置镜像尝试，之后使用版本清单对应版本的真实 `url`，避免把版本 ID 拼进官方 SHA-1 路径。
6. **损坏缓存**：无效的版本清单和版本 JSON 缓存会被删除并重新获取。
7. **下载完整性**：下载目标只有在提供 SHA-1 且校验通过时才会被当作可跳过缓存；没有校验值时不再仅凭文件存在就判定成功，避免复用上次中断留下的损坏 Java 压缩包。
8. **回归测试**：增加 Modrinth 搜索与版本查询 URL/参数测试。

## 还需 Windows 实测的部分

- 当前编辑环境未安装仓库要求的 .NET SDK `10.0.402`，因此未能运行 `dotnet test`、`dotnet build` 或 Windows GUI smoke test。
- Microsoft OAuth / Xbox / Minecraft Services、Java 下载镜像、GitHub Releases 更新、Bedrock 版本数据库及实际网络代理环境仍需要逐项联机验证。
- Microsoft/Xbox 登录属于认证服务，不应通过未经信任的通用下载代理转发。GitHub 更新包应继续校验来源与完整性。
- 视觉动效尚未做大规模重构；当前包优先修复已经能从源码明确定位的联网缺陷，避免把未经编译验证的视觉改动混入网络修复。
