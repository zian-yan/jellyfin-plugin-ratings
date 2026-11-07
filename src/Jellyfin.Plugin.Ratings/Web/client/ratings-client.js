/*
 * Jellyfin Ratings 客户端脚本（服务端插件版）
 * ---------------------------------------------------------------
 * 与旧版用户脚本的区别：
 *   - 不再在浏览器里直连 MDBList / TMDb / 豆瓣，也不再依赖公共 CORS 代理；
 *   - API Key、代理、缓存全部留在服务端，浏览器只发一个同源请求；
 *   - 只做「取数 → 渲染 → 隐藏内置星级」，逻辑尽量短，降低被 Jellyfin 前端改版打断的概率。
 *
 * v1.2.1 的「首帧落到不可见节点」修复：
 *   Jellyfin Web 的详情页会先把元信息区渲染一次、拿到完整条目数据后再重绘，
 *   期间同一选择器可能命中一个還沒显示（或已废弃）的节点。旧逻辑只看「容器存不存在」，
 *   一旦容器插进了这种节点，就会永远判定「已渲染」而不再重试 ——
 *   表现就是：原始评分被隐了、插件评分没出现，刷新一次（走 sessionStorage 预渲染）才正常。
 *   现在所有「已渲染」判定都要求容器**真的可见**（isConnected + getClientRects + computedStyle），
 *   不可见就删掉重来，并在取到数据后按 200/500/1200/2500ms 重试重新挂载。
 */
(function () {
    'use strict';

    if (window.__jellyfinRatingsLoaded) {
        return;
    }
    window.__jellyfinRatingsLoaded = true;

    var VERSION = '1.2.1';
    var API = 'Ratings/v1';
    var CONTAINER_CLASS = 'jf-ratings-container';
    var SCAN_INTERVAL = 800;
    var CLIENT_CACHE_TTL = 30 * 60 * 1000;
    var BUILTIN_GUARD_MS = 8000;
    var RETRY_DELAYS = [200, 500, 1200, 2500];
    var MAX_ATTEMPTS = 12;

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
        warnedItemId: null,
        lastData: null,
        attempts: 0,
        attemptItemId: null
    };

    function debug() {
        if (!window.__jellyfinRatingsDebug) {
            return;
        }
        var args = Array.prototype.slice.call(arguments);
        args.unshift('[Ratings]');
        console.log.apply(console, args);
    }

    /**
     * 元素是否真的能被用户看到。
     * 这是本文件最关键的一个判断：details 页的节点会先插入后显示/被替换，
     * 只看 querySelector 命中会让「插进了隐藏节点」被误判成已渲染。
     */
    function isVisible(element) {
        if (!element || element.isConnected === false) {
            return false;
        }
        if (typeof element.getClientRects === 'function' && element.getClientRects().length === 0) {
            return false;
        }
        var style = window.getComputedStyle ? window.getComputedStyle(element) : null;
        if (style) {
            if (style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse') {
                return false;
            }
            if (style.opacity !== '' && Number(style.opacity) === 0) {
                return false;
            }
        }
        return true;
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

    /** 所有可见的评分容器（正常情况下只应有一个）。 */
    function currentContainer() {
        var nodes = document.querySelectorAll('.' + CONTAINER_CLASS);
        for (var i = 0; i < nodes.length; i++) {
            if (isVisible(nodes[i])) {
                return nodes[i];
            }
        }
        return null;
    }

    /** 清掉插在隐藏/已废弃节点里的容器，避免它们冒充「已渲染」。 */
    function dropInvisibleContainers() {
        var dropped = 0;
        Array.prototype.forEach.call(document.querySelectorAll('.' + CONTAINER_CLASS), function (node) {
            if (!isVisible(node)) {
                node.remove();
                dropped++;
            }
        });
        return dropped;
    }

    function findAnchor() {
        for (var i = 0; i < ANCHOR_SELECTORS.length; i++) {
            var element = document.querySelector(ANCHOR_SELECTORS[i]);
            if (!element || !isVisible(element)) {
                continue;
            }

            var inside = element.classList
                && (element.classList.contains('itemMiscInfo') || element.classList.contains('mediaInfoList'));
            return { host: element, inside: inside };
        }

        return null;
    }

    function ensureContainer(anchor) {
        var existing = currentContainer();
        if (existing) {
            return existing;
        }

        var container = document.createElement('div');
        container.className = CONTAINER_CLASS;
        container.style.cssText = 'display:inline-flex;align-items:center;flex-wrap:wrap;margin-left:6px;';

        if (anchor.inside) {
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

    /**
     * 渲染并返回是否真的显示出来了。
     * 返回 false 时调用方不应隐藏内置评分——否则会出现「原始评分没了、插件评分也没出现」。
     */
    function render(data, anchor) {
        if (!anchor) {
            return false;
        }

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

        // 一条都没有也没有提示语：没什么可显示的，不算失败，也别再重试
        if (container.childElementCount === 0) {
            container.remove();
            if (config.hideBuiltInRatings !== false) {
                setBuiltInHidden(document, 0);
            }
            return true;
        }

        // 插进去不等于看得见：节点可能在一个还没显示（或已废弃）的父级里
        if (!isVisible(container)) {
            debug('插入点不可见，放弃这次渲染，稍后重试');
            container.remove();
            return false;
        }

        // 只有我们自己有数据时才隐藏内置星级，否则保留 Jellyfin 原生评分
        if (config.hideBuiltInRatings !== false) {
            setBuiltInHidden(document, hasData ? HIDE_HARD : 0);
        }

        debug('渲染完成', { count: container.childElementCount });
        return true;
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

    /**
     * 数据到手后尝试挂载；挂不上就按 RETRY_DELAYS 重试。
     * 详情页在条目数据加载完后还会重绘一次，首帧插进去的节点可能被丢掉，
     * 这类「数据有、位置没了」的情况全部由这里兜住。
     */
    function mount(itemId, data, attempt) {
        if (state.attemptItemId !== itemId) {
            state.attemptItemId = itemId;
            state.attempts = 0;
        }

        if (currentItemId() !== itemId) {
            return;
        }

        state.attempts = attempt;

        var anchor = findAnchor();
        if (render(data, anchor)) {
            state.renderedItemId = itemId;
            state.attempts = 0;
            return;
        }

        if (attempt >= MAX_ATTEMPTS) {
            warnOnce(itemId, '多次尝试仍无法把评分插入详情页，请反馈以下信息：', {
                url: location.href,
                anchors: ANCHOR_SELECTORS
            });
            setBuiltInHidden(document, 0);
            return;
        }

        var delay = RETRY_DELAYS[Math.min(attempt, RETRY_DELAYS.length - 1)];
        window.setTimeout(function () {
            mount(itemId, data, attempt + 1);
        }, delay);
    }

    function scan() {
        var itemId = currentItemId();
        if (!itemId) {
            return;
        }

        var visibleContainer = currentContainer();
        if (state.renderedItemId === itemId && visibleContainer) {
            return;
        }

        // 走到这里说明：没渲染过 / 渲染过但不可见 / 换了条目。
        // 「不可见」是最坑的一种：容器在 DOM 里却看不见，旧逻辑会一直判定已渲染。
        if (visibleContainer || state.renderedItemId === itemId) {
            clearRendered();
        } else {
            dropInvisibleContainers();
        }

        if (state.pendingItemId === itemId) {
            return;
        }

        var anchor = findAnchor();
        if (!anchor) {
            // 以前这里是静默 return：页面没评分、日志也没有任何线索
            warnOnce(itemId, '找到了条目但没有定位到详情页元信息区，无法插入评分。请把这条信息反馈给插件作者：', {
                url: location.href,
                selectors: ANCHOR_SELECTORS
            });
            return;
        }

        state.pendingItemId = itemId;
        debug('开始取数', itemId);

        // ① 手上已有结果就先渲染，不空等网络（刷新/回看走的就是这条）
        var local = state.lastData && state.lastData.itemId === itemId ? state.lastData : readClientCache(itemId);
        if (local) {
            mount(itemId, local, 0);
        } else if ((state.config || {}).hideBuiltInRatings !== false) {
            // ② 没有缓存时先软隐藏原生评分：避免「先看到 Jellyfin 评分、几秒后被替换」的跳变
            setBuiltInHidden(document, HIDE_SOFT);
        }

        // ③ 兜底：接口异常/超时时不能让原生评分一直不可见
        var guard = window.setTimeout(function () {
            guard = null;
            if (state.pendingItemId === itemId && !currentContainer()) {
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
                    if (!currentContainer()) {
                        setBuiltInHidden(document, 0);
                    }
                    return;
                }

                state.lastData = data;
                writeClientCache(itemId, data);
                mount(itemId, data, 0);
            })
            .catch(function (error) {
                state.pendingItemId = null;
                window.clearTimeout(guard);
                debug('取数失败', error);
                // 已经用缓存渲染出来的话，不要把内置评分放回来（会变成两套评分并存）
                if (!currentContainer()) {
                    setBuiltInHidden(document, 0);
                }
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
                container: !!currentContainer(),
                renderedItemId: state.renderedItemId,
                pendingItemId: state.pendingItemId,
                attempts: state.attempts,
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
