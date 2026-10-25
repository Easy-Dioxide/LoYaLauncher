# LoyaLauncher 0.8.0 — 网络可靠性与诊断版

> 本版为网络专项修复 + P1 界面增强。相对 0.7.1：全部 P0（网络统一/重试/响应验证/镜像切换/
> 更新安全/文件安全/登录加固）落地，新增网络诊断页，下载中心展示源名称、剩余时间与重试次数。

## 新增

- **网络诊断页**（侧边栏 + 设置页入口）：一键测试 Mojang / BMCLAPI / Modrinth / GitHub /
  Adoptium / 自定义源连通性，展示 HTTPS 状态码、耗时与失败原因；近期网络请求时间线；
  「复制诊断报告」生成脱敏文本（仅主机名/状态码/耗时，不含令牌、账号与完整查询串）。
- **下载中心增强**：任务行展示下载源名称（BMCLAPI / Mojang 官方 / Modrinth / Adoptium /
  GitHub）、重试次数角标与剩余时间估算；校验阶段独立状态。
- **减少动效开关**（设置 → 启动器）：关闭页面过渡、交错入场、涟漪/扫光与背景漂移，
  适合低性能设备或晕动敏感用户。
- **自定义源诊断**：配置自定义镜像后自动加入诊断目标列表。

## 修复（P0）

- 统一 HttpClient 与网络日志（NetLog）：所有请求记录模块、脱敏主机、阶段、耗时、
  状态码、异常类型、重试次数与所用镜像；环形缓冲 + 异步落盘 logs/network-YYYYMMDD.log，
  日志失败绝不影响下载。
- 文本获取不再静默吞错（StringFetchResult 携带状态码与错误）；可重试状态码
  （408/429/500/502/503/504）指数退避 + 抖动，尊重 Retry-After（封顶 60s）。
- 响应体验证：拒绝 HTML 错误页、空响应与非预期 JSON Content-Type；损坏缓存下载前删除。
- 版本清单缓存策略修正：无可信 SHA-1 的文件不因"已存在"跳过；损坏 manifest/JSON 重试前清理。
- 镜像按文件切换（MirrorSource/MirrorUrlSet）：客户端 jar 走 BMCLAPI /version/{id}/client，
  资源对象走 /objects/{hash}；官方 manifest 提供的版本 JSON 地址作为镜像失败兜底。
- Modrinth v2：facets 改为字符串数组；版本过滤用 game_versions/loaders JSON 数组参数；
  429 与 5xx 分类错误；文件下载保持版本数据原始 CDN URL；文件名严格拒绝目录穿越与保留设备名。
- Java 安装：解压后执行 java -version 验证主版本，不符则拒绝；清华目录解析失败回退官方 API；
  快照版本号（如 25-windows）正确推断所需 Java 主版本。
- 微软登录链路阶段化日志（device-code/token-poll/xbox/xsts/mc-login/profile/refresh），
  日志脱敏（不含令牌与设备码）；网络故障/拒绝/过期/无资格/儿童账户分类呈现；
  XSTS 2148916233/2148916238 语义保留；令牌绝不发往镜像（仅官方 HTTPS 端点）。
- 自动更新安全三件套：SHA-256 校验（同名 .sha256 / SHA256SUMS / Release Notes 三来源）、
  应用前备份与失败回滚、首次启动确认（AwaitingFirstStart 宽限避免误回滚）；
  更新检查分状态呈现（最新/可更新/无限流/资源缺失/校验失败/回滚完成）。
- 文件安全：解压路径统一经 SafePath/SafeArchive 校验，拒绝目录穿越；符号链接条目拒绝。

## 测试

- 296 个单元测试全部通过（新增 27 个：网络诊断 10、下载事件元数据 17）。
- 真实联调验证：Modrinth search/versions 200、清华 Adoptium 目录 200、Adoptium API 307
  重定向正常、微软登录 5 个官方端点全部可达。

## 发布

- Release 工作流为四个平台包生成 SHA-256 校验（.sha256 + SHA256SUMS + Release Notes 校验表），
  自动更新按序查找期望值；源码包不参与更新校验。
