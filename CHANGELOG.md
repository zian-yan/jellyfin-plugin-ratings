# Changelog

版本号与 `Directory.Build.props` 的 `<Version>` 保持一致；发布时从本文档抽取对应段落作为
Release 说明与 `manifest.json` 的 `changelog` 字段。

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

- 双目标打包：`net9.0` → Jellyfin 10.11（targetAbi 10.11.0.0），`net10.0` → Jellyfin 12（targetAbi 12.0.0.0）。
- 插件包随 GitHub Release 分发，插件仓库清单位于仓库根 `manifest.json`。
