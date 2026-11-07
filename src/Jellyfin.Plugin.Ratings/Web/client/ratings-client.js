/*
 * Jellyfin Ratings 客户端脚本（服务端插件版）
 * ---------------------------------------------------------------
 * 与旧版用户脚本的区别：
 *   - 不再在浏览器里直连 MDBList / TMDb / 豆瓣，也不再依赖公共 CORS 代理；
 *   - API Key、代理、缓存全部留在服务端，浏览器只发一个同源请求；
 *   - 只做「取数 → 渲染 → 隐藏内置星级」，逻辑尽量短，降低被 Jellyfin 前端改版打断的概率。
 *
 * v1.1.1 增强的抗改版能力：
 *   - itemId 不再只认 hash 里的 ?id=，路径式路由（/details/<guid>）与任意 GUID 片段都能识别；
 *   - 插入点找不到时不再静默什么都不做，控制台会给出可复制的诊断信息；
 *   - 数据接口返回 401 时自动换成 api_key 查询参数重试一次；
 *   - 设置 window.__jellyfinRatingsDebug = true 可看到完整过程日志。
 */
(function () {
    'use strict';

    if (window.__jellyfinRatingsLoaded) {
        return;
    }
    window.__jellyfinRatingsLoaded = true;

    var VERSION = '1.1.1';
    var API = 'Ratings/v1';
    var CONTAINER_CLASS = 'jf-ratings-container';
    var SCAN_INTERVAL = 800;
    var CLIENT_CACHE_TTL = 30 * 60 * 1000;
    var BUILTIN_GUARD_MS = 8000;

    var HIDE_HARD = 1;   // 有插件数据：彻底收起原生评分，释放占位
    var HIDE_SOFT = 2;   // 等待中：仅不可见，保留占位，避免布局抖动

    // 详情页「元信息区」的候选选择器，按优先级排列。
    // Jellyfin 各版本/新 UI 的 class 不同，逐个尝试比写死一个稳。
    var ANCHOR_SELECTORS = [
        '.itemMiscInfo.itemMiscInfo-primary',
        '.itemMiscInfo',
        '.detailPagePrimaryContainer .mediaInfoList',
        '.mediaInfoList',
        '.mediaInfoItem.mediaInfoOfficialRating',
        '.starRatingContainer',
        '.detailPagePrimaryContent',
        '.itemDetailPage .detailPageContent'
    ];

    var state = {
        config: null,
        renderedItemId: null,
        pendingItemId: null,
        warnedItemId: null
    };

    function debug() {
        if (!window.__jellyfinRatingsDebug) {
            return;
        }
        var args = Array.prototype.slice.call(arguments);
        args.unshift('[Ratings]');
        console.log.apply(console, args);
    }

    function apiUrl(path) {
        if (window.ApiClient && typeof window.ApiClient.getUrl === 'function') {
            try {
                return window.ApiClient.getUrl(path);
            } catch (e) {
                debug('ApiClient.getUrl 失败，回退到根路径', e);
            }
        }
        return '/' + path;
    }

    function tokenValue() {
        try {
            var client = window.ApiClient;
            return client && typeof client.accessToken === 'function' ? client.accessToken() : null;
        } catch (e) {
            return null;
        }
    }

    /**
     * 数据接口默认要求登录态（见插件设置里的「API 需要登录态」）。
     * 原生 fetch 不会像 ApiClient.fetch 那样自动带 token，必须显式补上；
     * logo 与 client.js 是静态资源，走匿名通道，不在这里处理。
     */
    function authHeaders() {
        var headers = {};
        var token = tokenValue();
        if (token) {
            // 与 Jellyfin Web 自身 ApiClient 用的格式一致
            headers['Authorization'] = 'MediaBrowser Token="' + token + '"';
        }
        return headers;
    }

    /**
     * 从当前 URL 里取条目 ID，兼容三种情况：
     *   1. hash 路由：#/details?id=xxxx
     *   2. 路径路由：/details/xxxx 或 /details/xxxx/...
     *   3. 任意位置出现的 32 位 GUID 片段
     * Jellyfin 12 换过前端布局，路由形式在不同客户端/版本上并不统一。
     */
    function currentItemId() {
        var href = '';
        try {
            href = location.href || '';
        } catch (e) {
            return null;
        }

        var match = href.match(/[?&#]id=([0-9a-fA-F]{32}|[0-9a-fA-F]{8}-[0-9a-fA-F-]{27})/);
        if (match) {
            return match[1];
        }

        match = href.match(/\/([0-9a-fA-F]{32})(?:[/?#]|$)/);
        return match ? match[1] : null;
    }

    function loadConfig() {
        if (state.config) {
            return Promise.resolve(state.config);
        }
        return fetch(apiUrl(API + '/config'), { credentials: 'same-origin', headers: authHeaders() })
            .then(function (response) { return response.ok ? response.json() : {}; })
            .then(function (config) {
                state.config = config || {};
                return state.config;
            })
            .catch(function () {
                state.config = {};
                return state.config;
            });
    }

    /**
     * 取评分。401 时换 api_key 查询参数再来一次——
     * 某些版本/反代会把 Authorization 头丢掉，留一条可用的退路。
     */
    function fetchRatings(itemId) {
        var url = apiUrl(API + '/item/' + itemId);

        return fetch(url, { credentials: 'same-origin', headers: authHeaders() })
            .then(function (response) {
                if (response.status !== 401) {
                    return response;
                }

                var token = tokenValue();
                if (!token) {
                    return response;
                }

                debug('数据接口 401，改用 api_key 查询参数重试');
                var separator = url.indexOf('?') >= 0 ? '&' : '?';
                return fetch(url + separator + 'api_key=' + encodeURIComponent(token), { credentials: 'same-origin' });
            });
    }

    function findAnchor() {
        for (var i = 0; i < ANCHOR_SELECTORS.length; i++) {
            var element = document.querySelector(ANCHOR_SELECTORS[i]);
            if (!element) {
                continue;
            }

            var inside = element.classList
                && (element.classList.contains('itemMiscInfo') || element.classList.contains('mediaInfoList'));
            return { host: element, inside: inside };
        }

        return null;
    }

    function ensureContainer(anchor) {
        var existing = document.querySelector('.' + CONTAINER_CLASS);
        if (existing) {
            return existing;
        }

        var container = document.createElement('div');
        container.className = CONTAINER_CLASS;
        container.style.cssText = 'display:inline-flex;align-items:center;flex-wrap:wrap;margin-left:6px;';

        if (anchor.inside) {
            container.style.display = 'inline-flex';
            anchor.host.appendChild(container);
        } else if (anchor.host.parentNode) {
            anchor.host.insertAdjacentElement('afterend', container);
        } else {
            anchor.host.appendChild(container);
        }

        return container;
    }

    /**
     * mode: HIDE_SOFT 软隐藏（等数据，保持占位）/ HIDE_HARD 硬隐藏（已有外部评分）/ 0 恢复
     * scope 传 document 即可作用于整个详情页，不依赖某一层容器还在原位。
     */
    function setBuiltInHidden(scope, mode) {
        var targets = scope.querySelectorAll('.starRatingContainer.mediaInfoItem, .mediaInfoItem.mediaInfoCriticRating');
        Array.prototype.forEach.call(targets, function (element) {
            if (mode) {
                if (!Object.prototype.hasOwnProperty.call(element.dataset, 'jfOrigStyle')) {
                    element.dataset.jfOrigStyle = element.getAttribute('style') || '';
                }
                if (mode === HIDE_SOFT) {
                    element.style.visibility = 'hidden';
                } else {
                    element.style.visibility = '';
                    element.style.color = 'transparent';
                    element.style.fontSize = '0';
                }
            } else if (Object.prototype.hasOwnProperty.call(element.dataset, 'jfOrigStyle')) {
                element.setAttribute('style', element.dataset.jfOrigStyle);
                delete element.dataset.jfOrigStyle;
            }
        });
    }

    function cacheStorageKey(itemId) {
        return 'jfr:v1:' + itemId;
    }

    function readClientCache(itemId) {
        try {
            var raw = window.sessionStorage.getItem(cacheStorageKey(itemId));
            if (!raw) {
                return null;
            }
            var parsed = JSON.parse(raw);
            if (!parsed || !parsed.t || !parsed.d) {
                return null;
            }
            if (Date.now() - parsed.t > CLIENT_CACHE_TTL) {
                window.sessionStorage.removeItem(cacheStorageKey(itemId));
                return null;
            }
            return parsed.d;
        } catch (e) {
            return null;
        }
    }

    function writeClientCache(itemId, data) {
        try {
            // 只缓存有结果的响应，空结果不值得预渲染
            if (!data || !data.ratings || !data.ratings.length) {
                return;
            }
            window.sessionStorage.setItem(
                cacheStorageKey(itemId),
                JSON.stringify({ t: Date.now(), d: data })
            );
        } catch (e) {
            // 隐私模式 / 配额已满：忽略
        }
    }

    function badgeLogo(source, badges) {
        if (!badges || !badges.length) {
            return null;
        }
        if (source === 'Rotten Tomatoes' && badges.indexOf('tomatoes_certified') >= 0) {
            return 'rotten-tomatoes-certified';
        }
        if (source === 'Popcornmeter' && badges.indexOf('audience_certified') >= 0) {
            return 'roten_tomatoes_ver';
        }
        if (source === 'Metacritic' && badges.indexOf('metacritic_must_see') >= 0) {
            return 'metacriticms';
        }
        return null;
    }

    function render(data, anchor) {
        var config = state.config || {};
        var container = ensureContainer(anchor);
        container.innerHTML = '';

        var ratings = (data && data.ratings) || [];
        var limit = Math.max(1, Math.min(config.maxRatingsPerItem || 6, 20));
        var badges = (data && data.badges) || [];

        ratings.slice(0, limit).forEach(function (rating) {
            var logo = badgeLogo(rating.source, badges) || rating.logo;
            if (!logo) {
                return;
            }

            var img = document.createElement('img');
            img.src = apiUrl(API + '/logo/' + encodeURIComponent(logo));
            img.alt = rating.source;
            img.style.cssText = 'height:1.5em;margin-right:4px;vertical-align:middle;';
            if (config.showSourceTooltip !== false) {
                img.title = rating.source + (rating.display ? ': ' + rating.display : '');
            }

            var value = document.createElement('span');
            value.textContent = rating.display || '';
            value.style.cssText = 'margin-right:8px;font-size:1em;vertical-align:middle;';
            if (config.showSourceTooltip !== false) {
                value.title = rating.source;
            }

            container.appendChild(img);
            container.appendChild(value);
        });

        var hasData = container.childElementCount > 0;
        if (!hasData && data && data.message) {
            var hint = document.createElement('span');
            hint.textContent = data.message;
            hint.style.cssText = 'font-size:0.8em;opacity:0.7;';
            container.appendChild(hint);
        }

        // 只有我们自己有数据时才隐藏内置星级，否则保留 Jellyfin 原生评分
        if (config.hideBuiltInRatings !== false) {
            setBuiltInHidden(document, hasData ? HIDE_HARD : 0);
        }

        debug('渲染完成', { itemId: data && data.itemId, count: container.childElementCount });
    }

    function clearRendered() {
        Array.prototype.forEach.call(document.querySelectorAll('.' + CONTAINER_CLASS), function (node) {
            node.remove();
        });
        setBuiltInHidden(document, 0);
        state.renderedItemId = null;
        // 清空 pending 后，上一轮遗留的兜底定时器即使触发也不会再操作新页面
        state.pendingItemId = null;
    }

    function warnOnce(itemId, message, extra) {
        if (state.warnedItemId === itemId) {
            return;
        }
        state.warnedItemId = itemId;
        console.warn('[Ratings] ' + message, extra || '');
    }

    function scan() {
        var itemId = currentItemId();
        if (!itemId) {
            return;
        }

        var anchor = findAnchor();
        if (!anchor) {
            // 这条以前是静默 return：页面没评分、日志也没有任何线索。
            warnOnce(itemId, '找到了条目但没有定位到详情页元信息区，无法插入评分。请把这条信息反馈给插件作者：', {
                url: location.href,
                selectors: ANCHOR_SELECTORS
            });
            return;
        }

        if (state.renderedItemId === itemId && document.querySelector('.' + CONTAINER_CLASS)) {
            return;
        }

        if (state.pendingItemId === itemId) {
            return;
        }

        state.pendingItemId = itemId;
        debug('开始取数', itemId);

        // ① 手上已有上次的结果就先渲染，不空等网络
        var local = readClientCache(itemId);
        if (local) {
            state.renderedItemId = itemId;
            render(local, anchor);
        } else if ((state.config || {}).hideBuiltInRatings !== false) {
            // ② 没有缓存时先软隐藏原生评分：避免「先看到 Jellyfin 评分、几秒后被替换」的跳变
            setBuiltInHidden(document, HIDE_SOFT);
        }

        // ③ 兜底：接口异常/超时时不能让原生评分一直不可见。
        var guard = window.setTimeout(function () {
            guard = null;
            if (state.pendingItemId === itemId && !local) {
                setBuiltInHidden(document, 0);
            }
        }, BUILTIN_GUARD_MS);

        // ④ 配置与评分并行取，少一个串行往返
        Promise.all([loadConfig(), fetchRatings(itemId)])
            .then(function (results) {
                var response = results[1];
                if (!response || !response.ok) {
                    debug('数据接口返回非 2xx', response && response.status);
                    return null;
                }
                return response.json();
            })
            .then(function (data) {
                state.pendingItemId = null;
                window.clearTimeout(guard);
                if (!data) {
                    setBuiltInHidden(document, 0);
                    return;
                }

                var target = findAnchor();
                if (!target) {
                    warnOnce(itemId, '取到数据但插入点消失了（页面又被重绘），本次放弃渲染。');
                    setBuiltInHidden(document, 0);
                    return;
                }

                state.renderedItemId = itemId;
                writeClientCache(itemId, data);
                render(data, target);
            })
            .catch(function (error) {
                state.pendingItemId = null;
                window.clearTimeout(guard);
                debug('取数失败', error);
                setBuiltInHidden(document, 0);
            });
    }

    function scheduleScan(delay) {
        window.setTimeout(scan, typeof delay === 'number' ? delay : 0);
    }

    window.addEventListener('hashchange', function () {
        clearRendered();
        scheduleScan(300);
    });

    window.addEventListener('popstate', function () {
        clearRendered();
        scheduleScan(300);
    });

    if (window.MutationObserver) {
        var pending = null;
        var observer = new MutationObserver(function () {
            if (pending) {
                return;
            }
            pending = window.setTimeout(function () {
                pending = null;
                var itemId = currentItemId();
                if (itemId && itemId !== state.renderedItemId) {
                    scheduleScan();
                }
            }, 150);
        });
        observer.observe(document.documentElement, { childList: true, subtree: true });
    }

    // 手动排障入口：控制台执行 jellyfinRatings.status()
    window.jellyfinRatings = {
        version: VERSION,
        status: function () {
            return {
                version: VERSION,
                url: location.href,
                itemId: currentItemId(),
                anchor: !!findAnchor(),
                renderedItemId: state.renderedItemId,
                pendingItemId: state.pendingItemId,
                config: state.config,
                apiBase: apiUrl(API)
            };
        },
        rescan: function () {
            clearRendered();
            scan();
        }
    };

    window.setInterval(scan, SCAN_INTERVAL);
    scheduleScan(0);
    debug('client v' + VERSION + ' 已加载');
})();
