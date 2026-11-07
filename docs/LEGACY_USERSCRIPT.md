# 旧版用户脚本（保留）

原仓库的浏览器脚本方案仍然可用，适合「不想装插件、只想在一台机器的一个浏览器里看」的场景。
插件版与脚本版**不要同时启用**，否则详情页会出现两套评分图标。

脚本文件（均在 `legacy/` 目录，不参与编译、不打进插件包）：

| 文件 | 说明 |
| --- | --- |
| `legacy/jellyfin_ratings_v2.js` | V2 主逻辑（插件版就是按它重构的），无 userscript 头，供注入器使用 |
| `legacy/jellyfin_ratings_v2.user.js` | V2 的 Tampermonkey / Violentmonkey 版本 |
| `legacy/ratings.js` | V1 精简版（只走 MDBList） |
| `legacy/jellyfin_ratings.user.js` | V1 的 userscript 版本 |

## V2 与 V1 的差异

1. 增加 Metacritic、Rotten Tomatoes 的评分与认证信息；
2. 数据源变多，首屏加载比 V1 慢；
3. 受技术限制：AlloCiné、豆瓣的评分与 Metacritic/RottenTomatoes 认证依赖免费公共代理，**不稳定**；
4. 附加评分（AniList、Kinopoisk、AlloCiné、豆瓣）只有标题+年份在对应站点能匹配时才显示，以避免错配。

V2 的单集评分需要 **TMDb API Key**，且单集评分只对 **Jellyfin 10.11+** 生效（旧版本回退显示 Jellyfin 自带评分）。

## 安装方式一：浏览器扩展（仅该浏览器生效）

需要先准备密钥：

- MDBList：<https://mdblist.com/preferences>
- Kinopoisk（可选）：<https://kinopoiskapiunofficial.tech>
- TMDb（V2 单集评分需要）

安装 [Tampermonkey](https://www.tampermonkey.net/) / [Violentmonkey](https://violentmonkey.github.io/) / [Greasemonkey](https://addons.mozilla.org/firefox/addon/greasemonkey/)，然后导入 `legacy/jellyfin_ratings_v2.user.js`，在脚本开头的 `MDBLIST_API_KEY` / `KINOPOISK_API_KEY` / `TMDB_API_KEY` 处填入自己的 key。

## 安装方式二：JavaScript 注入器插件

- V2：安装 [Jellyfin-JavaScript-Injector](https://github.com/n00bcodr/Jellyfin-JavaScript-Injector)，把 `legacy/jellyfin_ratings_v2.js` 全文粘进插件窗口，保存并刷新页面。
- V1：安装 [jellyfin-plugin-custom-javascript](https://github.com/johnpc/jellyfin-plugin-custom-javascript)，粘贴 `legacy/ratings.js`。

装完可能需要清缓存，Jellyfin Media Player 的缓存位于
`C:\Users\<用户名>\AppData\Local\Jellyfin Media Player\cache`。

## 安装方式三：改 `index.html`

在 web 根目录（如 `/usr/share/jellyfin/web`）建 `ratings` 目录，放入 `legacy/jellyfin_ratings_v2.js`，然后在 `index.html` 的 `</body>` 前加：

```html
<script src="ratings/jellyfin_ratings_v2.js"></script>
```

保存后重启 Jellyfin。**此方式在容器/只读 rootfs 下不可用，且升级会被覆盖** —— 插件版正是为了消除这一步。

## 已知问题（脚本版）

- API Key 明文暴露在浏览器，任何人 F12 可见；
- 依赖 `api.allorigins.win` / `api.codetabs.com` 公共代理，代理挂掉即全部失效；
- 每个浏览器每次进详情页都重新请求 5~7 个外部接口，无缓存；
- 站点改版即解析失败（豆瓣、AlloCiné、烂番茄均属此类）。

以上问题在插件版中分别以「服务端存密钥」「服务端直连」「服务端缓存」「失败隔离 + 健康度」处理，见根目录 `README.md`。
