# Jellyfin Ratings 插件

把原先的浏览器用户脚本 `jellyfin_ratings_v2.js` 重构成**原生 Jellyfin 服务端插件**，同时兼容 **Jellyfin 10.11（net9.0）** 与 **Jellyfin 12（net10.0）**。


---

## 1. 功能定位

原脚本是一个「在浏览器里跑」的补丁：它监听详情页 DOM，用 `GM_xmlhttpRequest` 直连 MDBList / TMDb / Wikidata / 豆瓣，再把 logo + 分数插到 Jellyfin 的信息栏里。这种形态有三个结构性缺陷：

| 问题 | 原脚本表现 | 插件形态的解决方式 |
| --- | --- | --- |
| 密钥暴露 | MDBList / TMDb / Kinopoisk 的 API Key 硬编码在 JS 里，任何访客按 F12 就能拿到 | 密钥只存服务端配置，浏览器永远看不到 |
| 跨域与代理 | 依赖 `api.allorigins.win`、`api.codetabs.com` 等公共 CORS 代理，随时可能失效 | 服务端 `HttpClient` 直连（可选自建代理），无 CORS 问题 |
| 重复请求 | 每个浏览器、每次进详情页都重新打 5~7 个外部接口 | 服务端集中缓存 + 并发去重，多人浏览只打一轮 |

**定位**：一个「元数据增强型」插件 —— 不碰媒体库数据、不写数据库、不改 Jellyfin 内置 Provider 流程，只在**详情页展示层**用外部评分替换/补充 Jellyfin 的星级与影评人评分。

### 集成方式（数据流）

```
浏览器 (Jellyfin Web)
  └─ index.html  ← 插件中间件在此注入 <script src="/Ratings/v1/client.js">
        └─ client.js：从 URL 取 itemId，请求 /Ratings/v1/item/{id}
              └─ RatingsController（ASP.NET Core MVC，插件程序集内）
                    └─ RatingAggregator
                         ├─ 缓存命中 → 直接返回
                         └─ 未命中 → Wave1: MDBList / TMDb(external_ids)
                                      Wave2: AniList / 豆瓣 / AlloCiné / Kinopoisk / 认证角标（并行）
                                        └─ 结果写缓存 + JSON 返回
```

服务端全程使用 `HttpClient`，不依赖任何前端代理；前端脚本只负责「取一个同源 JSON → 渲染图标和数字 → 隐藏内置星级」。

---

## 2. 核心功能

- **多来源评分聚合**：MDBList（IMDb、烂番茄、Popcornmeter、Metacritic、Trakt、Letterboxd、MyAnimeList）、TMDb、AniList、Kinopoisk、豆瓣、AlloCiné。
- **单集 / 单季评分**：从 `Episode` / `Season` 条目回溯到 `Series` 取 TMDb ID，用 `ParentIndexNumber` + `IndexNumber` 拼 TMDb 分集地址，命中即只显示该集评分（与原脚本一致）。
- **认证角标**：烂番茄「新鲜认证 / 观众认证」、Metacritic「必看」，前端把对应 logo 换成认证版图标。
- **服务端缓存**：默认 720 分钟；并发去重（同一部片同时被多人打开只发一轮请求）；`IScheduledTask` 每 6 小时清理过期项。
- **失败隔离与熔断**：单个数据源异常不影响其他来源；连续失败 N 次进入冷却期，避免拖慢每个详情页。
- **零文件写入的脚本注入**：不修改磁盘上的 `index.html`，容器 / 只读 rootfs / 升级不丢配置。
- **可诊断**：`/Ratings/v1/health` 一次性给出注入状态、缓存条数、各数据源健康度。
- **离线可用**：所有来源 logo 以嵌入资源打包，不依赖 jsDelivr / GitHub CDN。

```

**页面入口**：`/web/index.html#/configurationpage?name=ratings`（控制台 → 插件 → Ratings → 设置）。

除 `/v1/health` 与 `/v1/item`（当 `RequireAuthentication=true` 时）外，接口默认 `[AllowAnonymous]` —— 它们只返回公开评分数据、不含任何用户信息与密钥。需要收紧时把 `RequireAuthentication` 打开，控制器会校验 `User.Identity.IsAuthenticated`。

---
## 7. 构建

需要 .NET SDK 10（`global.json` 锁 `10.0.100` + `latestFeature`，可同时编译 `net9.0` 与 `net10.0`）。

```bash
# 还原 + 编译（解决方案级，一次产出两个 TFM）
dotnet restore Jellyfin.Plugin.Ratings.sln
dotnet build   Jellyfin.Plugin.Ratings.sln -c Release

# 单元测试（xunit，21 个用例：注入中间件含 gzip、嵌入资源、缓存/熔断、JSON 契约）
dotnet test Jellyfin.Plugin.Ratings.sln -c Release

# 一键产出两个插件包
bash build.sh
#  → dist/ratings-jf10.11.zip  (net9.0,  targetAbi 10.11.0.0)
#  → dist/ratings-jf12.zip     (net10.0, targetAbi 12.0.0.0)
```

只编译单个目标（调试用）：

```bash
dotnet build src/Jellyfin.Plugin.Ratings -c Debug -f net10.0
```

用 jprm 走官方仓库流程时（可选）：

```bash
jprm plugin build --spec build.yaml      # 10.11
jprm plugin build --spec build.12.yaml   # 12
```

> 当前验证状态：解决方案整体 `0 warning / 0 error`；`Jellyfin.Plugin.Ratings.dll` 的引用经反射确认分别是 `MediaBrowser.Controller 10.11.11.0` 与 `12.4.0.0`；`dotnet test` 21/21 通过。**尚未**在真实 Jellyfin 实例上做端到端点击验证（见第 10 节）。

---

## 8. 安装与启用

### 8.1 手动安装（推荐，最稳）

1. 选对包：
   - Jellyfin **10.11.x** → `dist/ratings-jf10.11.zip`
   - Jellyfin **12.x** → `dist/ratings-jf12.zip`
2. 解压到插件目录，目录名建议带版本号：

   ```
   <Jellyfin 数据目录>/plugins/Ratings_1.0.0.0/
       Jellyfin.Plugin.Ratings.dll
       meta.json
   ```

   - Linux 默认：`/var/lib/jellyfin/plugins/`（Docker 为 `/config/plugins/`）
   - Windows 默认：`%LOCALAPPDATA%\jellyfin\plugins\` 或安装目录下的 `data/plugins`
   - 目录权限需与 Jellyfin 运行用户一致（`chown -R jellyfin:jellyfin`）
3. 重启 Jellyfin。
4. 控制台 → 插件 → 应看到 **Ratings**，状态 `Active`。
5. 进 **Ratings → 设置**，填 MDBList / TMDb API Key，按需勾选数据源，保存。
6. 打开任意影片详情页，硬刷新一次（`Ctrl+F5`，或清 `Jellyfin Media Player` 的 cache 目录）。
7. 打开 `/Ratings/v1/health` 确认：
   - `injectionActive: true` → 注入生效；
   - `sources.mdblist.keyConfigured: true` → 密钥读到了；
   - 随便点开一部电影后 `cacheEntries` 应大于 0。

### 8.2 通过插件仓库安装（推荐）

仓库清单已随代码一起发布，直接把这个地址加到 Jellyfin 即可：

```
https://zian-yan.github.io/jellyfin-plugin-ratings/manifest.json
```

操作步骤：

1. 控制台 → **插件** → 右上角 **仓库**（Repositories）→ 点 **+**
2. 名称填 `Ratings`，URL 粘贴上面那行 → **保存**
3. 切到 **目录**（Catalog）标签，列表里会出现 **Ratings**；点 **安装**
4. 按提示重启 Jellyfin
5. 控制台 → 插件 → Ratings → 设置，填 API Key 后保存

插件会自动按你的服务器版本挑包：

| 你的 Jellyfin | 安装到的包 |
| --- | --- |
| 10.11.x | `net9.0` 包（targetAbi 10.11.0.0） |
| 12.x | `net10.0` 包（targetAbi 12.0.0.0） |
| ≤ 10.10 | 列表里不显示（不支持） |

> 该地址由 GitHub Pages 提供（仓库 Settings → Pages → Source: `main` / `/docs`，已启用）。
> 若 Pages 在你所在网络不可达，可退回到仓库原始文件地址：
> `https://raw.githubusercontent.com/zian-yan/jellyfin-plugin-ratings/main/docs/manifest.json`
> （两者内容一致，区别只在 `sourceUrl` 的域名）。
>
> 想换成自建托管（对象存储 / Nginx）：把 `docs/` 整个上传，然后
> `REPO_BASE_URL=https://你的域名/jellyfin-plugin-ratings python tools/make-manifest.py` 重新生成清单并提交。

清单结构（`docs/manifest.json`，由 `tools/make-manifest.py` 自动生成，含真实 MD5）：

## 附：来源与许可

评分数据来自 MDBList、TMDb、AniList、Kinopoisk、豆瓣、AlloCiné、Rotten Tomatoes、Metacritic 等第三方服务，使用时请遵守各自条款（尤其 TMDb 要求署名）。来源 logo 归各自权利人所有，仅用于标识数据出处。
