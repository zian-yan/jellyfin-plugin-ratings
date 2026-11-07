# Jellyfin Ratings 插件

把原先的浏览器用户脚本 `jellyfin_ratings_v2.js` 重构成**原生 Jellyfin 服务端插件**，目标 **Jellyfin 12（net10.0）**。


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

需要 .NET SDK 10（`global.json` 锁 `10.0.100` + `latestFeature`）。

```bash
# 还原 + 编译
dotnet restore Jellyfin.Plugin.Ratings.sln
dotnet build   Jellyfin.Plugin.Ratings.sln -c Release

# 单元测试（xunit，21 个用例：注入中间件含 gzip、嵌入资源、缓存/熔断、JSON 契约）
dotnet test Jellyfin.Plugin.Ratings.sln -c Release

# 一键产出插件包
bash build.sh
#  → dist/ratings-jf12.zip  (net10.0, targetAbi 12.0.0.0)
```

`build.sh` 只负责出包，不改清单；发布走 tag（见 [8.2.1](#821-发布新版本)）。

只编译（调试用）：

```bash
dotnet build src/Jellyfin.Plugin.Ratings -c Debug -f net10.0
```

用 jprm 走官方仓库流程时（可选）：

```bash
jprm plugin build --spec build.yaml   # Jellyfin 12
```

> 当前验证状态：解决方案整体 `0 warning / 0 error`；`Jellyfin.Plugin.Ratings.dll` 的引用经反射确认是 `MediaBrowser.Controller 12.4.0.0`；`dotnet test` 21/21 通过。**尚未**在真实 Jellyfin 实例上做端到端点击验证。

---

## 8. 安装与启用

### 8.1 手动安装（推荐，最稳）

1. 下载 `ratings-1.0.0.0-jf12.zip`（[Releases](https://github.com/zian-yan/jellyfin-plugin-ratings/releases)）。需要 **Jellyfin 12.x**；10.11 及更早版本不再支持。

   自己编译也行：`bash build.sh` 产出同样内容的 `dist/ratings-jf12.zip`。
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

清单放在**仓库根目录** `manifest.json`，插件包托管在 **GitHub Release** 资产上（与
[jellyfin-plugin-faststrm](https://github.com/zian-yan/jellyfin-plugin-faststrm) 同一套发布方式，不使用 GitHub Pages）。

把下面这个地址加到 Jellyfin 即可：

```
https://raw.githubusercontent.com/zian-yan/jellyfin-plugin-ratings/main/manifest.json
```

操作步骤：

1. 控制台 → **插件** → 右上角 **仓库**（Repositories）→ 点 **+**
2. 名称填 `Ratings`，URL 粘贴上面那行 → **保存**
3. 切到 **目录**（Catalog）标签，列表里会出现 **Ratings**；点 **安装**
4. 按提示重启 Jellyfin
5. 控制台 → 插件 → Ratings → 设置，填 API Key 后保存

插件只发 Jellyfin 12（net10.0）一个包，清单里每个版本只有一条记录，无需按服务器版本挑包：

| 你的 Jellyfin | 结果 |
| --- | --- |
| 12.x | 显示 Ratings，可安装 |
| ≤ 10.11 | 列表里不显示（不支持） |

> `raw.githubusercontent.com` 访问不稳定时，可改用 jsDelivr 镜像（缓存约 12 小时）：
> `https://cdn.jsdelivr.net/gh/zian-yan/jellyfin-plugin-ratings@main/manifest.json`
>
> 旧版本的 `https://zian-yan.github.io/jellyfin-plugin-ratings/manifest.json`（GitHub Pages）已下线，
> 如果仓库里还留着，删掉重建一条上面的地址即可。

清单结构（根 `manifest.json`，发版时由 `scripts/update_manifest.py` 自动写入真实 MD5）：

```json
[
  {
    "guid": "7f1e5b2a-9c3d-4e8b-a1f6-2d4c6b8e9a10",
    "name": "Ratings",
    "imageUrl": "https://raw.githubusercontent.com/zian-yan/jellyfin-plugin-ratings/main/assets/icon.png",
    "versions": [
      { "version": "1.0.0.0", "targetAbi": "12.0.0.0", "sourceUrl": "https://github.com/zian-yan/jellyfin-plugin-ratings/releases/download/v1.0.0/ratings-1.0.0.0-jf12.zip", "checksum": "f117…", "timestamp": "…" }
    ]
  }
]
```

### 8.2.1 发布新版本

只打 tag，构建、打包、发 Release、回写清单全部由 `.github/workflows/release.yml` 完成：

```bash
# 1. 改版本号（以下 3 处必须一致，workflow 会校验，不一致直接 fail）
#    Directory.Build.props   <Version>1.0.1</Version>
#    meta.json               "version": "1.0.1.0"
#    build.yaml              version: "1.0.1.0"
# 2. 在 CHANGELOG.md 里加 `## [1.0.1]` 段落（Release 说明与清单 changelog 都取自它）
git commit -am "release: 1.0.1"
git tag -a v1.0.1 -m "Ratings v1.0.1"
git push origin main && git push origin v1.0.1   # 推 tag 即触发发布
```

workflow 依次执行：校验版本 → `dotnet test` → `build.sh` 出 zip → 重命名为
`ratings-1.0.1.0-jf12.zip` 上传 Release → 运行
`scripts/update_manifest.py` 把新版本（含 MD5）写入 `manifest.json` 并提交回 `main`。

几个容易踩的点：

- **版本号归一化**：tag 只写三段（`v1.0.1`），文件里写 `1.0.1` 或 `1.0.1.0` 都可以，脚本会自动补第四段。
- **`CHANGELOG.md` 缺对应段落不会失败**：只会 warning，Release 说明退化为 `v1.0.1`。
- **漏触发可手动补**：Actions → `Release` → **Run workflow**，填 `tag_name`（如 `v1.0.1`）。
- **不要手改清单里的旧条目**：脚本只做「删掉同版本旧条目 → 插入新条目 → 按版本降序重排」。
- Jellyfin 端在「插件 → 目录」会看到新版本，点更新即可；旧包名带版本号，不会互相覆盖。
- `main` 上的提交会触发 `build.yml` 做常规 CI（只验证编译与测试），发布逻辑只在 tag 上跑，两者不冲突。

### 8.3 卸载 / 回滚

删除插件目录并重启即可，插件不写数据库、不改媒体元数据。想要更细的控制：把 `EnableWebInjection` 关掉，前端立即恢复原生评分，服务端接口仍可单独使用。

---

## 9. 错误处理与日志

**分层隔离**，任何一层失败都不会让详情页白屏或报错：

| 层级 | 策略 |
| --- | --- |
| 单次 HTTP | 统一超时（`TimeoutSeconds`）+ `CancellationToken` 链接；区分 `timeout` / `cancelled` / `HttpRequestException` / 未知异常，分别落到不同日志级别 |
| 单个数据源 | `RunAsync` 包裹 `try/catch`，异常转成 `SourceResult.Failed("source: 类型")` 写入响应 `errors[]`，其他来源照常返回 |
| 数据源熔断 | 连续失败达 `FailureThreshold` → 冷却 `CooldownMinutes`，冷却期内直接跳过（Debug 日志说明跳过原因） |
| 响应解析 | `JsonException` 单独捕获，按 `Debug` 记录并返回 `xxx: 响应解析失败`，绝不让解析异常冒泡 |
| 聚合器 | 顶层 `try/catch` 兜底，返回部分结果 + `errors[]`；`OperationCanceledException` 单独放行（客户端断开不算错误） |
| 中间件 | 注入异常 → 回退原始响应 + `Warning` 日志；响应体空/非 200 → 原样透传 |
| 控制器 | 条目不存在 → 404；条目读取异常 → 500 + 结构化 `{error}`；logo 名称含 `.` `/` `\` `:` → 400 |

**日志约定**（统一 `[Ratings]` 前缀，便于 `grep`）：

```
Info   [Ratings] 聚合完成 Fight Club (1999) 来源 5 个，错误 0 条，耗时 412ms
Debug  [Ratings] mdblist 完成 7 条，388ms，error=-
Warn   [Ratings] 请求超时 8s: movie.douban.com/subject_search
Warn   [Ratings] douban 连续失败 3 次，冷却 10 分钟
Warn   [Ratings] index.html 注入失败，已回退原始响应
Error  [Ratings] 聚合评分失败: show|1399|-|2|5
Info   [Ratings] 缓存维护完成，清理 12 条，剩余 301 条
```

密钥安全：`RatingsHttp` 在打日志前会把查询串里的 `*key*` / `*token*` 参数替换成 `***`（`SafePath`），URL 只记录 host + 脱敏 path。响应里的 `errors[]` 由 `ExposeErrors` 控制，生产环境可关。

排障顺序建议：`/Ratings/v1/health` → Jellyfin 日志过滤 `[Ratings]` → 单独用 `/Ratings/v1/lookup?tmdbId=550&type=movie` 验证链路 → 再查前端控制台。

---

## 10. 已知限制

1. **未在真实实例上做端到端点击验证**。已完成：net10.0 编译、程序集引用核对、`dotnet test` 21 项用例（注入/压缩/嵌入资源/缓存/熔断/JSON 契约）。首次上机请按 8.1 第 7 步核对 `health`。
2. **前端 DOM 依赖 Jellyfin Web 结构**（`.itemMiscInfo.itemMiscInfo-primary`、`.mediaInfoItem`）。Jellyfin 12 若整体换用新 UI 框架，`client.js` 的选择器需同步调整 —— 服务端接口不受影响。
3. **豆瓣 / AlloCiné / 烂番茄 均为页面解析**，对方改版即失效；这类来源默认关闭，失败只影响自身。
4. **`IStartupFilter` 注入依赖宿主在构建 Host 前收集插件服务**，Jellyfin 12 的官方启动流程满足该前提；若被第三方启动器改写，按 8.3 降级。
5. **仅支持 Jellyfin 12**；10.11 及更早版本不再提供包（`BasePlugin` 构造签名与目标 ABI 均不同）。
6. 评分只做展示，不写回 `BaseItem.CriticRating`/`CommunityRating`，因此**排序/筛选仍按 Jellyfin 原生评分**。需要改元数据的场景应走 Provider 插件而非本插件。

---

## 11. 旧版用户脚本

`legacy/` 目录下的 `jellyfin_ratings_v2.js` / `ratings.js` / `*.user.js` 保留原样，适用于不想装插件的场景：

- Tampermonkey / Violentmonkey 安装 `.user.js`（仅该浏览器生效，API Key 在脚本里改）；
- 或配合 `Jellyfin-JavaScript-Injector` 插件粘贴 `legacy/jellyfin_ratings_v2.js`。

详细步骤见 [`docs/LEGACY_USERSCRIPT.md`](docs/LEGACY_USERSCRIPT.md)。插件版与脚本版**不要同时启用**，否则会出现两套评分图标。

---


## 附：来源与许可

评分数据来自 MDBList、TMDb、AniList、Kinopoisk、豆瓣、AlloCiné、Rotten Tomatoes、Metacritic 等第三方服务，使用时请遵守各自条款（尤其 TMDb 要求署名）。来源 logo 归各自权利人所有，仅用于标识数据出处。
