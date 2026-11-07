# Changelog

版本号与 `Directory.Build.props` 的 `<Version>` 保持一致；发布时从本文档抽取对应段落作为
Release 说明与 `manifest.json` 的 `changelog` 字段。

## [1.1.0]

首次加载体验与接口安全优化。

### 🚀 首屏加载

- 缓存改为内存 + 磁盘双层（`{DataPath}/ratings/ratings-cache.json`），重启后不再从零开始。
- 过期旧值秒开（stale-while-revalidate）：缓存过期后先返回旧数据、后台刷新，详情页不再因缓存到期而卡顿。
- 进入详情页立刻占位，取到外部评分再渲染，不再出现「先显示 Jellyfin 原始评分、几秒后被替换」的跳变；同一会话内回看直接预渲染。
- 关键路径并行：IMDb ID 解析与 MDBList 主数据同时发起；增强源增加宽限期（`EnrichmentGraceMs`，默认 1200ms），超时的来源转后台继续并回填缓存。

### 🔒 安全

- 查询接口（item / lookup / health）默认要求 Jellyfin 登录态，并按客户端限流（默认 120 次/分钟，可配置），避免被当作免费的外部 API 代理刷光配额。
- Provider ID 增加白名单校验，阻断拼进外部 URL / SPARQL 时的路径注入。
- 修复 SSRF：烂番茄链接来自公开可编辑的 Wikidata，现在只接受 `https` + `*.rottentomatoes.com`。
- logo 接口改为嵌入资源白名单，杜绝匿名请求把缓存字典撑爆。
- 外部可控文本（片名、用户名）写入日志前去除控制字符并截断；错误摘要默认关闭。
- 注入标签里的 `PathBase` 做 HTML 编码，避免反代路径前缀注入脚本。

### 🐛 修复

- `Dispose` 时缓存未落盘（`Flush` 被自身的 disposed 判断挡住）。
- 磁盘缓存加载未完成时刷盘会用空内存覆盖磁盘文件。
- 宽限期返回的部分结果不写缓存，导致紧接着的访问重复请求全部外部接口。
- 注入状态关闭后不复位，health 一直显示注入仍生效。

### ⚠️ 升级注意

- `RequireAuthentication` 默认改为 `true`。浏览器端由注入脚本自动带 token，无感知；用 `curl` / 第三方脚本直接调用 `/Ratings/v1/lookup` 时需带 `api_key`。
- `ExposeErrors` 默认改为 `false`，需要排障时到设置里手动打开。
- 缓存从纯内存改为落盘，升级后首次启动仍是空缓存，需要重新抓一轮。

## [1.0.0]

首个插件版本。

### ✨ 新增

- 多来源评分聚合：MDBList（IMDb / 烂番茄 / Popcornmeter / Metacritic / Trakt / Letterboxd / MyAnimeList）、TMDb、AniList、Kinopoisk、豆瓣、AlloCiné。
- 单集 / 单季评分：从 `Episode` / `Season` 回溯 `Series` 取 TMDb ID，按季集号定位后只显示该集评分。
- 认证角标：烂番茄「新鲜认证 / 观众认证」、Metacritic「必看」替换为认证版图标。
- 服务端缓存（默认 720 分钟）+ 并发去重 + 每 6 小时清理过期项。
- 失败隔离与熔断：单源异常不影响其他来源，连续失败进入冷却期。
- 零文件写入的脚本注入，容器 / 只读 rootfs / 升级不丢配置。
- `GET /Ratings/v1/health` 一次性给出注入状态、缓存条数与各数据源健康度。

### 🔧 构建

- 仅目标 Jellyfin 12（net10.0，targetAbi 12.0.0.0）；不再提供 10.11 包。
- 插件包随 GitHub Release 分发，插件仓库清单位于仓库根 `manifest.json`。
