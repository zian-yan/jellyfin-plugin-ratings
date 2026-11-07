# Jellyfin Ratings 插件

把原先的浏览器用户脚本 `jellyfin_ratings_v2.js` 重构成**原生 Jellyfin 服务端插件**，同时兼容 **Jellyfin 10.11（net9.0）** 与 **Jellyfin 12（net10.0）**。

> 旧版脚本保留在 `legacy/` 目录，作为对照与降级方案，见文末「旧版用户脚本」。

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

---

## 3. 配置项

后台路径：**控制台 → 插件 → Ratings → 设置**（页面文件 `Configuration/configPage.html`）。

| 配置键 | 类型 | 默认值 | 说明 |
| --- | --- | --- | --- |
| `MdbListApiKey` | string | 空 | 主数据源密钥，`mdblist.com/preferences` 获取；未填时仅剩 TMDb 单集评分 |
| `TmdbApiKey` | string | 空 | 单集/单季评分 + TMDb→IMDb ID 反查 |
| `KinopoiskApiKey` | string | 空 | 可选 |
| `EnableMdbList` | bool | true | 启用 MDBList |
| `EnableTmdb` | bool | true | 启用 TMDb |
| `EnableAniList` | bool | false | 番剧评分（走 Wikidata P8729 精确反查） |
| `EnableKinopoisk` | bool | false | 俄语区评分 |
| `EnableDouban` | bool | false | 豆瓣（需服务器能访问 `movie.douban.com`） |
| `EnableAllocine` | bool | false | 法国 AlloCiné |
| `EnableCertifications` | bool | false | 烂番茄 / Metacritic 认证角标 |
| `EnableEpisodeRatings` | bool | true | 分集页只显示该集 TMDb 评分 |
| `HideBuiltInRatings` | bool | true | 用外部评分替换内置星级（仅在有数据时隐藏） |
| `MaxRatingsPerItem` | int | 6 | 单条目最多显示来源数（1~20） |
| `ShowSourceTooltip` | bool | true | 悬停提示「来源: 分数」 |
| `CacheMinutes` | int | 720 | 缓存分钟数，0 = 不缓存 |
| `TimeoutSeconds` | int | 8 | 单次外部请求超时（2~60） |
| `HttpProxy` | string | 空 | 出口代理，如 `http://127.0.0.1:7890` |
| `FailureThreshold` | int | 3 | 连续失败多少次触发冷却 |
| `CooldownMinutes` | int | 10 | 冷却时长 |
| `EnableWebInjection` | bool | true | 自动往 `index.html` 注入脚本 |
| `RequireAuthentication` | bool | false | 为 true 时 API 需要登录态 |
| `ExposeErrors` | bool | true | 响应里带错误摘要（排障用，生产可关） |

配置文件落盘位置：`<Jellyfin 数据目录>/plugins/configurations/Jellyfin.Plugin.Ratings.xml`。

---

## 4. 对外接口与页面入口

所有接口挂在**站点根路径**（Jellyfin 10.x/12 的 API 没有 `/api` 前缀）。

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/Ratings/v1/item/{itemId}` | 按 Jellyfin 条目 ID 取评分，前端脚本默认入口；可选 `?season=&episode=` 覆盖季/集号 |
| GET | `/Ratings/v1/lookup` | 按 Provider ID 直接查：`?type=movie\|show&tmdbId=&imdbId=&season=&episode=&title=&year=` |
| GET | `/Ratings/v1/config` | 前端展示配置（不含任何密钥） |
| GET | `/Ratings/v1/client.js` | 客户端脚本本体（嵌入资源，可手动 `<script>` 引用） |
| GET | `/Ratings/v1/logo/{name}` | 来源 logo（`image/png`，长缓存，含路径穿越防护） |
| GET | `/Ratings/v1/health` | 诊断：版本、注入状态、缓存条数、数据源健康度 |

响应示例：

```json
{
  "itemId": "b1f0…",
  "mediaType": "movie",
  "tmdbId": "550",
  "imdbId": "tt0137523",
  "title": "Fight Club",
  "year": 1999,
  "ratings": [
    { "source": "IMDb", "logo": "IMDb", "value": 8.4, "scale": 10, "display": "8.4", "order": 10 },
    { "source": "Rotten Tomatoes", "logo": "Rotten_Tomatoes", "value": 79, "scale": 100, "display": "79", "order": 20 }
  ],
  "badges": ["tomatoes_certified"],
  "fromCache": false,
  "fetchedAt": "2026-11-07T06:29:05+00:00",
  "errors": ["douban: timeout"]
}
```

**页面入口**：`/web/index.html#/configurationpage?name=ratings`（控制台 → 插件 → Ratings → 设置）。

除 `/v1/health` 与 `/v1/item`（当 `RequireAuthentication=true` 时）外，接口默认 `[AllowAnonymous]` —— 它们只返回公开评分数据、不含任何用户信息与密钥。需要收紧时把 `RequireAuthentication` 打开，控制器会校验 `User.Identity.IsAuthenticated`。

---

## 5. 双版本兼容处理

版本差异**不是靠猜**，而是用 `MetadataLoadContext` 对 `Jellyfin.Controller 10.11.11` 与 `12.4.0` 两个 NuGet 包逐成员比对后落实的。完整对比见 [`docs/COMPATIBILITY.md`](docs/COMPATIBILITY.md)，结论如下：

| 差异点 | 10.11 | 12.x | 处理方式 |
| --- | --- | --- | --- |
| 目标框架 | `net9.0` | `net10.0` | csproj 多目标 `net9.0;net10.0`，按 TFM 条件引用不同 Jellyfin 包版本；两个 ABI 各出一个 zip |
| `BasePlugin<T>` 构造 | `(IApplicationPaths, IXmlSerializer)` | 同左 | 单一签名即可，无需条件编译（10.10 及更早的 `IApplicationHost` 签名不在支持范围内） |
| `ProviderIds` | `Dictionary<string,string>` | 同左，但新增 `IsValidProviderId` / `SetProviderIds` | 统一走 `TryGetProviderId()` 扩展方法，不直接下标访问 |
| `ILibraryManager` | `GetItemById(Guid)` | `GetItemById(Guid)` 仍在，新增/移除若干方法 | 只用 `GetItemById` + `ResolveSeries` 回溯 |
| 控制器基类 | `Jellyfin.Api.Controllers.ControllerBase` 位置随版本变动 | 同上 | **不继承 Jellyfin 的控制器基类**，改用 `Microsoft.AspNetCore.Mvc.ControllerBase` + `[ApiController]` |
| `MetadataProvider` 枚举 | `MediaBrowser.Model.Entities` | 同左 | 必须全限定：`ControllerBase` 自身有 `MetadataProvider` 属性，会遮蔽枚举 |
| `IScheduledTask` / `TaskTriggerInfo` | 一致 | 一致 | 无需处理 |
| `IPluginServiceRegistrator` | `RegisterServices(IServiceCollection, IServerApplicationHost)` | 一致 | 无需处理 |

运行时再做一层软适配（`Core/JellyfinCompat.cs`）：

```csharp
JellyfinCompat.ServerVersion      // IServerApplicationHost.ApplicationVersion
JellyfinCompat.IsV12OrLater       // 供日志/诊断与未来分支使用
JellyfinCompat.TargetFramework    // net9.0 / net10.0（编译期常量）
JellyfinCompat.GetProviderId(…)   // 屏蔽 ProviderIds 读写差异
```

**为什么能一次写完两版**：插件只依赖 `Jellyfin.Controller` / `Jellyfin.Model` 中两版都存在且签名一致的成员，所有"有分歧"的地方都被收敛到 `JellyfinCompat` 与 csproj 的条件引用里。

### 脚本注入的兼容性

注入用 `IStartupFilter` + 自定义中间件实现，挂载在 ASP.NET Core 管道最前，拦截 `index.html` 响应并在 `</body>` 前插入一行 `<script>`：

- 自动处理 **gzip / brotli / deflate** 压缩响应（解压→改写→清理 `Content-Encoding`→重算 `Content-Length`）；
- 只处理 `text/html` 的 `GET`，其余响应原样透传；
- 注入失败自动回退原始响应，并打 `Warning` 日志；
- 若宿主没有收集 `IStartupFilter`（极少见），脚本注入不生效但**接口仍然可用**，可手动在 `index.html` 加 `<script src="/Ratings/v1/client.js" defer></script>`，或用 `legacy/jellyfin_ratings_v2.user.js` 方式降级。

---

## 6. 目录结构

```
jellyfin-plugin-ratings/
├── Jellyfin.Plugin.Ratings.sln            # 解决方案：src + tests（VS / Rider 直接打开）
├── Directory.Build.props                  # 解决方案级公共属性 + Jellyfin 双版本包版本
├── global.json                            # SDK 版本锁定（10.0.100，latestFeature）
├── .editorconfig / .gitattributes         # 代码风格与换行统一
├── .github/workflows/build.yml            # CI：restore → build(双 TFM) → test → 打包
├── build.yaml / build.12.yaml             # jprm 构建规格（10.11 / 12）
├── build.sh                               # 一键产出两个插件 zip
├── assets/
│   ├── logo/                              # 来源图标（编译期嵌入，唯一来源）
│   └── screenshots/                       # 效果截图
├── legacy/                                # 旧版浏览器脚本（保留，不再维护）
│   ├── jellyfin_ratings_v2.js
│   ├── jellyfin_ratings_v2.user.js
│   ├── jellyfin_ratings.user.js
│   └── ratings.js
├── docs/                                  # 同时是插件仓库的静态根（Jellyfin 从这里拉清单）
│   ├── manifest.json                      # 仓库清单（自动生成，含 MD5）
│   ├── packages/*.zip                     # 供 Jellyfin 下载的插件包
│   ├── index.html / icon.png              # 仓库落地页与目录图标
│   ├── COMPATIBILITY.md                   # 10.11 vs 12 逐项 API 对比
│   └── LEGACY_USERSCRIPT.md               # 旧版用户脚本安装方式
├── tools/make-manifest.py                 # 由 dist/*.zip 生成 manifest（md5/timestamp/排序）
├── src/Jellyfin.Plugin.Ratings/           # 插件主工程（net9.0 + net10.0）
│   ├── Jellyfin.Plugin.Ratings.csproj     # 条件引用 Jellyfin 包，版本取自 Directory.Build.props
│   ├── Plugin.cs                          # 插件入口（继承 BasePlugin<PluginConfiguration>）
│   ├── PluginServiceRegistrator.cs        # DI 注册：HTTP/缓存/聚合器/注入中间件
│   ├── Configuration/
│   │   ├── PluginConfiguration.cs         # 全部配置项（XML 序列化）
│   │   └── configPage.html                # 后台设置页（嵌入资源）
│   ├── Api/
│   │   ├── RatingsController.cs           # 6 个 REST 接口
│   │   └── RatingModels.cs                # DTO / RatingQuery / CacheKey
│   ├── Core/
│   │   ├── JellyfinCompat.cs              # 版本兼容层
│   │   ├── RatingsHttp.cs                 # HttpClient 封装（超时/代理/日志脱敏）
│   │   ├── RatingCache.cs                 # 进程内缓存 + SourceHealth 熔断
│   │   ├── SourceRegistry.cs              # 数据源注册表 + 配置读取入口
│   │   └── RatingAggregator.cs            # 两波并行聚合、去重、缓存、错误隔离
│   ├── Sources/                           # 每个数据源一个文件
│   │   ├── IRatingSource.cs               # 契约 + SourceResult
│   │   ├── MdbListSource.cs               # 主数据源 + logo/量纲映射
│   │   ├── TmdbSource.cs                  # 单集/单季评分、IMDb ID 反查
│   │   ├── WikidataHelper.cs              # SPARQL 反查 AniList/RT/豆瓣/AlloCiné
│   │   ├── AniListSource.cs
│   │   ├── KinopoiskSource.cs
│   │   ├── DoubanSource.cs
│   │   ├── AllocineSource.cs
│   │   └── CertificationsSource.cs        # 认证角标
│   ├── Web/
│   │   ├── RatingsStartupFilter.cs        # IStartupFilter
│   │   ├── RatingsHtmlInjector.cs         # index.html 注入中间件
│   │   └── client/ratings-client.js       # 前端脚本（嵌入资源）
│   └── Tasks/CacheMaintenanceTask.cs      # 每 6 小时清理过期缓存
├── tests/Jellyfin.Plugin.Ratings.Tests/   # xunit：注入/嵌入资源/缓存/聚合契约（dotnet test）
└── dist/                                  # 构建产物（gitignore）
```

单个插件（`src/Jellyfin.Plugin.Ratings/`）对外只产出一个程序集 `Jellyfin.Plugin.Ratings.dll` + `meta.json`。

### 布局约定

| 位置 | 规则 |
| --- | --- |
| `src/<ProjectName>/` | 只有一个可打包项目，命名与程序集名一致；插件包只含 `Jellyfin.Plugin.Ratings.dll` + `meta.json` |
| `tests/<ProjectName>.Tests/` | 纯 xunit 工程，`dotnet test` 直接可跑，不依赖 Jellyfin 实例与网络 |
| `Directory.Build.props` | 版本号、语言版本、可空性、**Jellyfin 双 ABI 包版本**集中在此，升级只改一处 |
| `global.json` | 锁定 SDK 10 系列（`latestFeature` 允许 10.0.x 小版本滚动） |
| `assets/` | 只放构建或文档用的静态资源；`assets/logo` 是嵌入资源的唯一来源 |
| `legacy/` | 历史脚本，不参与编译、不打进插件包 |
| `dist/` | 构建产物，已 `.gitignore` |

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
https://raw.githubusercontent.com/zian-yan/jellyfin-plugin-ratings/main/docs/manifest.json
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

> 若你的网络无法直连 `raw.githubusercontent.com`，把 `docs/` 目录整个放到任意 HTTPS 静态托管（GitHub Pages、对象存储、Nginx），
> 用 `REPO_BASE_URL=https://你的域名` 重新生成清单：`python tools/make-manifest.py`，然后把新的 `manifest.json` 地址加进 Jellyfin。
>
> 启用 GitHub Pages 的写法：仓库 Settings → Pages → Source 选 `Deploy from a branch` → 分支 `main`、目录 `/docs`，
> 之后仓库地址就是 `https://zian-yan.github.io/jellyfin-plugin-ratings/manifest.json`；
> 生成时加 `REPO_BASE_URL=https://zian-yan.github.io/jellyfin-plugin-ratings` 即可让 `sourceUrl` 指向 Pages。

清单结构（`docs/manifest.json`，由 `tools/make-manifest.py` 自动生成，含真实 MD5）：

```json
[
  {
    "guid": "7f1e5b2a-9c3d-4e8b-a1f6-2d4c6b8e9a10",
    "name": "Ratings",
    "imageUrl": "…/docs/icon.png",
    "versions": [
      { "version": "1.0.0.0", "targetAbi": "12.0.0.0",  "sourceUrl": "…/ratings-1.0.0.0-jf12.zip",    "checksum": "f117…", "timestamp": "…" },
      { "version": "1.0.0.0", "targetAbi": "10.11.0.0", "sourceUrl": "…/ratings-1.0.0.0-jf10.11.zip", "checksum": "2726…", "timestamp": "…" }
    ]
  }
]
```

**`versions` 的顺序不能调换**：Jellyfin 的选版逻辑是「先过滤 `TargetAbi ≤ 服务器版本`，再按版本号降序取第一条」
（`InstallationManager.GetPackageVersions`）。两个 ABI 版本号相同，`OrderByDescending` 又是稳定排序，
所以 12 的条目必须写在前面 —— 10.11 服务器会把 12 条目过滤掉，12 服务器则两条都通过、先命中 12 条目。

### 8.2.1 发布新版本

```bash
# 1. 改版本号（唯一位置）
#    Directory.Build.props → <Version>1.0.1</Version>
# 2. 重建 + 重新生成清单（会更新 md5 与 timestamp，并覆盖 docs/packages 下的同名包）
bash build.sh
# 3. 提交 docs/manifest.json 与 docs/packages/*.zip
git add docs && git commit -m "release: 1.0.1 [skip ci]" && git push
```

Jellyfin 端在「插件 → 目录」里会看到新版本，点更新即可（旧版本缓存的包名带版本号，不会互相覆盖）。

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

1. **未在真实实例上做端到端点击验证**。已完成：双 TFM 编译、程序集引用核对、`dotnet test` 21 项用例（注入/压缩/嵌入资源/缓存/熔断/JSON 契约）。首次上机请按 8.1 第 7 步核对 `health`。
2. **前端 DOM 依赖 Jellyfin Web 结构**（`.itemMiscInfo.itemMiscInfo-primary`、`.mediaInfoItem`）。Jellyfin 12 若整体换用新 UI 框架，`client.js` 的选择器需同步调整 —— 服务端接口不受影响。
3. **豆瓣 / AlloCiné / 烂番茄 均为页面解析**，对方改版即失效；这类来源默认关闭，失败只影响自身。
4. **`IStartupFilter` 注入依赖宿主在构建 Host 前收集插件服务**，10.11/12 的官方启动流程满足该前提；若被第三方启动器改写，按 8.3 降级。
5. **不支持 10.10 及更早版本**（`BasePlugin` 构造签名不同）。
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
