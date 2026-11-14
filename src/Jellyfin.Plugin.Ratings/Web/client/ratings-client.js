/*
 * Jellyfin Ratings 客户端脚本（服务端插件版）
 * 只做「取数 → 渲染 → 隐藏内置评分」：数据来自同源 /Ratings/v1，密钥与缓存都在服务端。
 *
 * 锚点策略：Jellyfin Web 会把旧页面留在 DOM 里并隐藏，只按选择器取第一个会命中隐藏页（甚至是上一部片的残留）。
 *   1. 只在「可见且含内置评分元素」的盒子里工作；
 *   2. 隐藏内置评分的作用域限定在那个盒子，不全局乱隐；
 *   3. 容器带 data-jf-itemid 幂等，并监听 viewshow / pageshow / hashchange / popstate + MutationObserver。
 */
(function () {
    'use strict';

    if (window.__jellyfinRatingsLoaded) {
        return;
    }
    window.__jellyfinRatingsLoaded = true;

    // 兜底版本号：拿到 /v1/config 后用服务端 PluginVersion 覆盖，避免脚本内常量与插件版本漂移
    var VERSION = '1.2.4';
    var API = 'Ratings/v1';
    var CONTAINER_CLASS = 'jf-ratings-container';
    var HIDE_SOFT_CLASS = 'jf-hide-soft';
    var HIDE_HARD_CLASS = 'jf-hide-hard';
    var STYLE_ID = 'jf-ratings-style';
    var SCAN_INTERVAL = 800;
    var CLIENT_CACHE_TTL = 30 * 60 * 1000;
    var BUILTIN_GUARD_MS = 8000;
    var RETRY_DELAYS = [200, 500, 1200, 2500];
    var MAX_ATTEMPTS = 12;

    // 详情页元信息盒子：优先只认这两个 class，Jellyfin 各版本都在用
    var BOX_SELECTOR = '.itemMiscInfo.itemMiscInfo-primary, .itemMiscInfo';

    // 内置评分元素：盒子里有它，才说明这是「真正渲染出来的」详情页
    var RATING_CHIP_SELECTOR =
        'div.starRatingContainer.mediaInfoItem, div.mediaInfoItem.mediaInfoCriticRating,' +
        'div.mediaInfoItem.mediaInfoOfficialRating';

    // 没有 .itemMiscInfo 时的兜底插入点（新 UI / 改版后）
    var FALLBACK_SELECTORS = [
        '.mediaInfoList',
        '.detailPagePrimaryContainer .mediaInfoList',
        '.mediaInfoItem.mediaInfoOfficialRating',
        '.starRatingContainer',
        '.detailPagePrimaryContent'
    ];

    var HIDE_HARD = 1;
    var HIDE_SOFT = 2;

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

    function ensureStyle() {
        if (document.getElementById(STYLE_ID)) {
            return;
        }
        var style = document.createElement('style');
        style.id = STYLE_ID;
        style.textContent =
            '.' + HIDE_SOFT_CLASS + '{visibility:hidden !important}' +
            '.' + HIDE_HARD_CLASS + '{visibility:hidden !important;color:transparent !important;font-size:0 !important}';
        (document.head || document.documentElement).appendChild(style);
    }

    /**
     * 元素是否真的能被用户看到。
     * Jellyfin 把旧页面留在 DOM 里（display:none），所以「存在」不等于「可见」。
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

    function hasRatingChip(box) {
        try {
            return !!box.querySelector(RATING_CHIP_SELECTOR);
        } catch (e) {
            return false;
        }
    }

    /**
     * 选出当前真正在显示的详情页元信息盒子。
     * 判据顺序：可见 + 含内置评分 → 可见 → 最后一个（新插入的页面通常在后面）。
     */
    function getActiveDetailsBox() {
        var boxes = document.querySelectorAll(BOX_SELECTOR);
        if (!boxes || !boxes.length) {
            return null;
        }

        var i;
        for (i = 0; i < boxes.length; i++) {
            if (isVisible(boxes[i]) && hasRatingChip(boxes[i])) {
                return boxes[i];
            }
        }

        for (i = 0; i < boxes.length; i++) {
            if (isVisible(boxes[i])) {
                return boxes[i];
            }
        }

        // 全都不可见时不硬撑，交给 render 的可见性校验决定是否重试
        return null;
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
                if (state.config.pluginVersion) {
                    VERSION = state.config.pluginVersion;
                }
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

    /** 清掉插在隐藏/已废弃页面里的容器，避免它们冒充「已渲染」。 */
    function dropInvisibleContainers() {
        Array.prototype.forEach.call(document.querySelectorAll('.' + CONTAINER_CLASS), function (node) {
            if (!isVisible(node)) {
                node.remove();
            }
        });
    }

    function findAnchor() {
        var box = getActiveDetailsBox();
        if (box) {
            return { host: box, inside: true, box: box };
        }

        for (var i = 0; i < FALLBACK_SELECTORS.length; i++) {
            var element = document.querySelector(FALLBACK_SELECTORS[i]);
            if (!element || !isVisible(element)) {
                continue;
            }

            var inside = element.classList
                && (element.classList.contains('itemMiscInfo') || element.classList.contains('mediaInfoList'));
            return { host: element, inside: inside, box: null };
        }

        return null;
    }

    function ensureContainer(anchor, itemId) {
        var scope = anchor.host;
        var existing = null;
        try {
            existing = scope.querySelector ? scope.querySelector('.' + CONTAINER_CLASS) : null;
        } catch (e) {
            existing = null;
        }

        // 其它条目/其它页面残留的容器先清掉，避免同屏出现两套评分
        Array.prototype.forEach.call(document.querySelectorAll('.' + CONTAINER_CLASS), function (node) {
            if (node !== existing) {
                node.remove();
            }
        });

        if (existing) {
            existing.setAttribute('data-jf-itemid', itemId);
            return existing;
        }

        var container = document.createElement('div');
        // mediaInfoItem：直接用 Jellyfin 自己的样式（间距、字号、对齐），比手写 cssText 更贴合
        container.className = 'mediaInfoItem ' + CONTAINER_CLASS;
        container.setAttribute('data-jf-itemid', itemId);
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
     * mode: HIDE_SOFT 软隐藏（等数据）/ HIDE_HARD 硬隐藏（已有外部评分）/ 0 恢复
     * 作用域必须是「当前显示的那个盒子」：全局隐藏会把别的（隐藏）页面一起改，
     * 一旦我们的容器插错位置，就会出现原始评分没了、插件评分也没出现。
     */
    function setBuiltInHidden(scope, mode) {
        var targets = (scope || document).querySelectorAll(RATING_CHIP_SELECTOR);
        Array.prototype.forEach.call(targets, function (element) {
            element.classList.remove(HIDE_SOFT_CLASS);
            element.classList.remove(HIDE_HARD_CLASS);
            if (mode === HIDE_SOFT) {
                element.classList.add(HIDE_SOFT_CLASS);
            } else if (mode === HIDE_HARD) {
                element.classList.add(HIDE_HARD_CLASS);
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
     * 返回 false 时调用方不得隐藏内置评分——否则会出现「原始评分没了、插件评分也没出现」。
     */
    function render(data, anchor, itemId) {
        if (!anchor) {
            return false;
        }

        var config = state.config || {};
        var container = ensureContainer(anchor, itemId);
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
            setBuiltInHidden(anchor.box || document, 0);
            return true;
        }

        // 插进去不等于看得见：盒子可能在一个还没显示（或已废弃）的页面里
        if (!isVisible(container)) {
            debug('插入点不可见，放弃这次渲染，稍后重试');
            container.remove();
            return false;
        }

        if (config.hideBuiltInRatings !== false) {
            setBuiltInHidden(anchor.box || document, hasData ? HIDE_HARD : 0);
        }

        debug('渲染完成', { count: container.childElementCount, box: !!anchor.box });
        return true;
    }

    function clearRendered() {
        Array.prototype.forEach.call(document.querySelectorAll('.' + CONTAINER_CLASS), function (node) {
            node.remove();
        });
        setBuiltInHidden(document, 0);
        state.renderedItemId = null;
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
        if (render(data, anchor, itemId)) {
            state.renderedItemId = itemId;
            state.attempts = 0;
            return;
        }

        if (attempt >= MAX_ATTEMPTS) {
            warnOnce(itemId, '多次尝试仍无法把评分插入详情页，请反馈以下信息：', {
                url: location.href,
                boxes: document.querySelectorAll(BOX_SELECTOR).length,
                status: window.jellyfinRatings ? window.jellyfinRatings.status() : null
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

        var box = getActiveDetailsBox();
        var visibleContainer = currentContainer();

        // 已经渲染在「看得见的盒子里」才算数；
        // 容器插在隐藏/已翻页的盒子里时要重来。
        if (state.renderedItemId === itemId && visibleContainer) {
            var containerBox = visibleContainer.closest ? visibleContainer.closest(BOX_SELECTOR) : null;
            if (!box || !containerBox || containerBox === box || isVisible(containerBox)) {
                return;
            }
        }

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
            warnOnce(itemId, '找到了条目但没有定位到详情页元信息区，无法插入评分。请把这条信息反馈给插件作者：', {
                url: location.href,
                boxes: document.querySelectorAll(BOX_SELECTOR).length
            });
            return;
        }

        state.pendingItemId = itemId;
        debug('开始取数', itemId, 'box=' + !!anchor.box);

        var local = state.lastData && state.lastData.itemId === itemId ? state.lastData : readClientCache(itemId);
        if (local) {
            mount(itemId, local, 0);
        } else if ((state.config || {}).hideBuiltInRatings !== false) {
            setBuiltInHidden(anchor.box || document, HIDE_SOFT);
        }

        // 兜底：接口异常/超时时不能让原生评分一直不可见
        var guard = window.setTimeout(function () {
            guard = null;
            if (state.pendingItemId === itemId && !currentContainer()) {
                setBuiltInHidden(document, 0);
            }
        }, BUILTIN_GUARD_MS);

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
                // 已经渲染成功的话不要把内置评分放回来（会变成两套评分并存）
                if (!currentContainer()) {
                    setBuiltInHidden(document, 0);
                }
            });
    }

    var scanTimer = null;

    function scheduleScan(delay) {
        if (scanTimer) {
            return;
        }
        scanTimer = window.setTimeout(function () {
            scanTimer = null;
            scan();
        }, typeof delay === 'number' ? delay : 120);
    }

    // Jellyfin Web 的视图切换事件（比只监听 hashchange 可靠）
    document.addEventListener('viewshow', function () { scheduleScan(60); });
    document.addEventListener('pageshow', function () { scheduleScan(60); });

    window.addEventListener('hashchange', function () {
        clearRendered();
        scheduleScan(120);
    });

    window.addEventListener('popstate', function () {
        clearRendered();
        scheduleScan(120);
    });

    if (window.MutationObserver) {
        // 无条件重扫：同一条目也会被 Jellyfin 重绘（占位 → 完整数据），
        // 只比较 itemId 会漏掉这类「容器被换掉」的情况。
        var observer = new MutationObserver(function () {
            scheduleScan(150);
        });
        observer.observe(document.documentElement, { childList: true, subtree: true });
    }

    window.jellyfinRatings = {
        version: VERSION,
        status: function () {
            var box = getActiveDetailsBox();
            var container = currentContainer();
            return {
                version: VERSION,
                url: location.href,
                itemId: currentItemId(),
                boxes: document.querySelectorAll(BOX_SELECTOR).length,
                activeBox: !!box,
                container: !!container,
                containerInActiveBox: !!(box && container && box.contains(container)),
                renderedItemId: state.renderedItemId,
                pendingItemId: state.pendingItemId,
                attempts: state.attempts,
                apiBase: apiUrl(API)
            };
        },
        rescan: function () {
            clearRendered();
            scan();
        }
    };

    ensureStyle();
    window.setInterval(scan, SCAN_INTERVAL);
    scheduleScan(0);
    debug('client v' + VERSION + ' 已加载');
})();
