# Jellyfin Ratings 插件

用 MDBList / TMDb / 豆瓣 / AniList 等外部来源的评分替换 Jellyfin 内置评分的**服务端插件**，仅支持 **Jellyfin 12（net10.0）**。

---

## 核心功能

- **多来源评分聚合**：MDBList（IMDb、TMDb、烂番茄、Popcornmeter、Metacritic、Trakt、Letterboxd、MyAnimeList）、TMDb、AniList、Kinopoisk、豆瓣、AlloCiné。
- **分集 / 分季评分**：从 `Episode` / `Season` 回溯到 `Series` 取 TMDb ID，按季集号定位，只显示该集评分。
- **认证角标**：烂番茄「新鲜认证 / 观众认证」、Metacritic「必看」替换为认证版图标。
- **服务端缓存与并发去重**：默认缓存 720 分钟，内存 + 磁盘双层（重启不丢），同一部片多人同时打开只发一轮请求；过期后先返回旧值再后台刷新，定时任务每 6 小时清理超期项。
- **首屏不闪旧评分**：进入详情页立刻占位，取到外部评分再渲染，不会出现「先显示 Jellyfin 原始评分、几秒后被替换」的跳变；同一会话内回看直接预渲染。
- **失败隔离与熔断**：单源异常不影响其他来源；连续失败进入冷却期，避免拖慢详情页。
- **零文件写入的脚本注入**：不修改磁盘上的 `index.html`，容器 / 只读 rootfs / 升级不丢配置；所有来源 logo 以嵌入资源打包，不依赖外部 CDN。
- **密钥不暴露**：API Key 只存服务端配置，浏览器永远拿不到；日志输出前自动脱敏。
- **接口默认要求登录态**：查询接口需 Jellyfin 登录态（浏览器端由注入脚本自动带上），并按客户端 120 次/分钟限流——否则任何人都能把它当成免费的外部 API 代理，刷光你的 MDBList 配额。静态资源（logo、`client.js`）不受影响。
- **可诊断**：`/Ratings/v1/health` 一次性给出注入状态、缓存条数与各数据源健康度（未登录时只返回版本、注入状态与缓存条数）。

---

## 安装与启用

**环境要求**：Jellyfin 12.x（10.11 及更早版本不支持）。

### 方式一：插件仓库安装（推荐）

仓库清单位于仓库根 `manifest.json`，插件包托管在 GitHub Release 资产上。把下面地址添加到 Jellyfin：

```
https://raw.githubusercontent.com/zian-yan/jellyfin-plugin-ratings/main/manifest.json
```

1. 控制台 → **插件** → 右上角 **仓库**（Repositories）→ 点 **+**
2. 名称填 `Ratings`，URL 粘贴上面那行 → **保存**
3. 切到 **目录**（Catalog）标签，找到 **Ratings** → 点 **安装**
4. 按提示重启 Jellyfin

> `raw.githubusercontent.com` 访问不稳定时，可改用 jsDelivr 镜像（缓存约 12 小时）：
> `https://cdn.jsdelivr.net/gh/zian-yan/jellyfin-plugin-ratings@main/manifest.json`
>
> 旧地址 `https://zian-yan.github.io/jellyfin-plugin-ratings/manifest.json`（GitHub Pages）已下线，若仓库里还留着请删除并重建为上面的地址。

### 方式二：手动安装

1. 从 [Releases](https://github.com/zian-yan/jellyfin-plugin-ratings/releases) 下载 `ratings-1.0.0.0-jf12.zip`；本地执行 `bash build.sh` 也会产出同样内容的 `dist/ratings-jf12.zip`。
2. 解压到插件目录，目录名建议带版本号：

   ```
   <Jellyfin 数据目录>/plugins/Ratings_1.0.0.0/
       Jellyfin.Plugin.Ratings.dll
       meta.json
   ```

   - Linux 默认：`/var/lib/jellyfin/plugins/`（Docker 为 `/config/plugins/`）
   - Windows 默认：`%LOCALAPPDATA%\jellyfin\plugins\` 或安装目录下的 `data/plugins`
   - 目录权限需与 Jellyfin 运行用户一致（`chown -R jellyfin:jellyfin`）

3. 重启 Jellyfin，在 **控制台 → 插件** 确认 **Ratings** 状态为 `Active`。

### 启用与验证

1. 进 **Ratings → 设置**，填 MDBList / TMDb API Key，按需勾选数据源，保存。
   （页面入口：`/web/index.html#/configurationpage?name=ratings`）
2. 打开任意影片详情页，硬刷新一次（`Ctrl+F5`，或清 Jellyfin Media Player 的缓存目录）。
3. 打开 `/Ratings/v1/health` 确认：
   - `injectionActive: true` → 注入生效
   - `sources.mdblist.keyConfigured: true` → 密钥已读取（需登录后查看；未登录只返回版本、注入状态与缓存条数）
   - 点开一部电影后 `cacheEntries` 应大于 0

> **关于「API 需要登录态」**：默认开启。浏览器端由注入脚本自动带 token，用户无感知；但用 `curl` 或第三方脚本直接调 `/Ratings/v1/lookup` 时需要带 Jellyfin API Key（`?api_key=...` 或 `Authorization` 头）。若你的场景需要匿名调用（例如公开库），可在设置里关掉，同时建议把「单客户端查询上限」调小。

> **卸载 / 回滚**：删除插件目录并重启即可，插件不写数据库、不改媒体元数据。只想临时停用前端渲染，把 `EnableWebInjection` 关掉即可，服务端接口仍可单独使用。

---

## 附：来源与许可

评分数据来自 MDBList、TMDb、AniList、Kinopoisk、豆瓣、AlloCiné、Rotten Tomatoes、Metacritic 等第三方服务，使用时请遵守各自条款（尤其 TMDb 要求署名）。来源 logo 归各自权利人所有，仅用于标识数据出处。
