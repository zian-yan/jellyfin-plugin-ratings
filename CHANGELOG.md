# Changelog

版本号与 `Directory.Build.props` 的 `<Version>` 保持一致；发布时从本文档抽取对应段落作为
Release 说明与 `manifest.json` 的 `changelog` 字段。

## [1.2.2]

按 `Jellyfin.Plugin.MDBList_Ratings` 的锚点思路重写详情页渲染，彻底解决「第一次进详情页原始评分消失、插件评分不出来」。

### 🐛 修复

- **锚点选错页面**：Jellyfin Web 会把**旧页面留在 DOM 里并隐藏**，`document.querySelector('.itemMiscInfo-primary')` 第一个命中的常常是隐藏页（甚至上一部片的残留）。旧逻辑在其中插入容器，同时全局隐藏了当前页的内置评分 —— 于是「原始评分没了、插件评分也不出现」，刷新（只剩当前页在 DOM）才正常。现在只在「可见 **且** 含内置评分元素（`starRatingContainer` / `mediaInfoCriticRating`）」的盒子里工作。
- **隐藏作用域收敛到该盒子**：不再全局隐藏内置评分，避免误伤隐藏页/其它条目。
- 容器改用 `mediaInfoItem` 类，直接继承 Jellyfin 自己的间距与排版；同时带 `data-jf-itemid` 做幂等复渲染。
- **重扫不再只看 itemId**：同一条目也会被 Jellyfin 重绘（占位 → 完整数据），改为无条件防抖重扫（`MutationObserver` + `viewshow` / `pageshow` / `hashchange` / `popstate`）。
- 容器所在盒子与当前显示的盒子不一致时自动重建，避免停在隐藏页里。

## [1.2.1]

修复「第一次进详情页：原始评分被隐藏，插件评分却要刷新一次才出现」。

### 🐛 修复

- **首帧渲染落到不可见节点**：Jellyfin Web 的详情页会先渲染一次元信息区，条目数据到齐后再整块重绘。插件在重绘前插入的容器可能落在随后被隐藏/替换的节点里，而 `scan()` 只判断「容器存不存在」就认为已渲染，自愈循环被永久挡住 —— 表现就是原始评分没了、插件评分也没出现，刷新（走 sessionStorage 预渲染）才恢复正常。现在所有「已渲染」判定都要求容器**真的可见**（`isConnected` + `getClientRects` + computedStyle），不可见就删掉重来。
- 取到数据后按 200 / 500 / 1200 / 2500ms 重试挂载，最多 12 次；仍失败则在控制台给出诊断信息并恢复内置评分，不再出现「两边都不显示」。
- 渲染失败（插入点不可见）时不再隐藏内置评分；已用缓存渲染成功时，接口异常也不会把内置评分放回来（避免两套评分并存）。
- 没有可显示内容（既无评分也无提示语）时不再进入重试循环。

## [1.2.0]

修复「详情页完全没有评分、日志里也看不到插件活动」的静默失效。

### 🐛 修复

- **注入被 304 吞掉**：Jellyfin 给 `index.html` 设了 `Cache-Control: no-cache`，浏览器每次都会带 `If-None-Match` 回来验证，静态文件中间件直接回 304（无响应体），原逻辑见「非 200」就跳过 —— 于是浏览器永远用缓存里那份不带脚本的 `index.html`，插件侧一行日志都没有。现在主动摘掉条件请求头（`If-None-Match` / `If-Modified-Since` / `If-Range` / `Range` 等），强制走 200 + 完整 HTML。
- **反代子路径部署注入地址错误**：`BaseUrl` 映射发生在更靠后的管道里，中间件拿到的路径还带前缀，只拼 `PathBase` 会得到 `/Ratings/v1/client.js` 这种错地址（404 → 无评分）。改为从请求路径反推前缀。
- **前端取不到 itemId**：原来只认 hash 里的 `?id=`，路径式路由（`/details/<guid>`）直接静默不取数。现在兼容 hash / path / URL 中任意 GUID 片段。
- **401 不再干等**：数据接口返回 401 时自动改用 `api_key` 查询参数重试一次。

### 🔍 可观测性

- 启动日志新增「Web 注入中间件已挂载」——出现在日志里就说明中间件确实进了管道。
- 注入落空（状态码非 200、找不到 `</body>`）从静默改为 WARNING，不再无声失败。
- `/Ratings/v1/health` 新增 `middlewareMounted` / `indexRequests` / `patched` / `lastIndexRequest`，一次就能判断问题在「没注入」还是「取数失败」。
- 未认证请求从 Debug 提到 Warning（每分钟最多一条），401 一眼可见。
- 前端控制台：找不到插入点会打印诊断信息；`jellyfinRatings.status()` 可随时查看当前状态，`window.__jellyfinRatingsDebug = true` 打开过程日志。

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
