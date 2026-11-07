/*
 * Jellyfin Ratings 客户端脚本（服务端插件版）
 * ---------------------------------------------------------------
 * 与旧版用户脚本的区别：
 *   - 不再在浏览器里直连 MDBList / TMDb / 豆瓣，也不再依赖公共 CORS 代理；
 *   - API Key、代理、缓存全部留在服务端，浏览器只发一个同源请求；
 *   - 只做「取数 → 渲染 → 隐藏内置星级」，逻辑尽量短，降低被 Jellyfin 前端改版打断的概率。
 *
 * 关于「首次打开慢、一开始显示的是 Jellyfin 原生评分」：
 *   - 请求发出时就先把原生评分软隐藏，避免看到旧值再跳变；
 *   - 同一会话内回看过的条目直接用 sessionStorage 里的结果预渲染，不等网络；
 *   - 接口异常时有兜底，原生评分会恢复显示，不会一直不可见。
 */
(function () {
    'use strict';

    if (window.__jellyfinRatingsLoaded) {
        return;
    }
    window.__jellyfinRatingsLoaded = true;

    var API = 'Ratings/v1';
    var CONTAINER_CLASS = 'jf-ratings-container';
    var SCAN_INTERVAL = 800;
    var CLIENT_CACHE_TTL = 30 * 60 * 1000;
    var BUILTIN_GUARD_MS = 8000;

    var HIDE_HARD = 1;   // 有插件数据：彻底收起原生评分，释放占位
    var HIDE_SOFT = 2;   // 等待中：仅不可见，保留占位，避免布局抖动

    var state = {
        config: null,
        renderedItemId: null,
        pendingItemId: null
    };

    function apiUrl(path) {
        if (window.ApiClient && typeof window.ApiClient.getUrl === 'function') {
            return window.ApiClient.getUrl(path);
        }
        return '/' + path;
    }

    /**
     * 数据接口默认要求登录态（见插件设置里的「API 需要登录态」）。
     * 原生 fetch 不会像 ApiClient.fetch 那样自动带 token，必须显式补上；
     * logo 与 client.js 是静态资源，走匿名通道，不在这里处理。
     */
    function authHeaders() {
        var headers = {};
        try {
            var client = window.ApiClient;
            var token = client && typeof client.accessToken === 'function' ? client.accessToken() : null;
            if (token) {
                // 与 Jellyfin Web 自身 ApiClient 用的格式一致
                headers['Authorization'] = 'MediaBrowser Token="' + token + '"';
            }
        } catch (e) {
            // 取不到 token 就按匿名请求；服务端关闭认证时同样可用
        }
        return headers;
    }

    function currentItemId() {
        var match = (location.hash || '').match(/[?&]id=([0-9a-fA-F-]{32,36})/);
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

    function primaryBoxes() {
        return Array.prototype.slice.call(
            document.querySelectorAll('.itemMiscInfo.itemMiscInfo-primary')
        );
    }

    function ensureContainer(box) {
        var existing = box.querySelector('.' + CONTAINER_CLASS);
        if (existing) {
            return existing;
        }

        var items = box.querySelectorAll('.mediaInfoItem');
        var anchor = box.querySelector('.mediaInfoItem.mediaInfoText.mediaInfoOfficialRating')
            || (items.length ? items[items.length - 1] : null);

        var container = document.createElement('div');
        container.className = CONTAINER_CLASS;
        container.style.cssText = 'display:inline-flex;align-items:center;flex-wrap:wrap;margin-left:6px;';

        if (anchor && anchor.parentNode === box) {
            anchor.insertAdjacentElement('afterend', container);
        } else {
            box.appendChild(container);
        }

        return container;
    }

    /**
     * mode: HIDE_SOFT 软隐藏（等数据，保持占位）/ HIDE_HARD 硬隐藏（已有外部评分）/ 0 恢复
     */
    function setBuiltInHidden(box, mode) {
        var targets = box.querySelectorAll('.starRatingContainer.mediaInfoItem, .mediaInfoItem.mediaInfoCriticRating');
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

    function render(data, box) {
        var config = state.config || {};
        var container = ensureContainer(box);
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
            setBuiltInHidden(box, hasData ? HIDE_HARD : 0);
        }
    }

    function clearRendered() {
        Array.prototype.forEach.call(document.querySelectorAll('.' + CONTAINER_CLASS), function (node) {
            node.remove();
        });
        primaryBoxes().forEach(function (box) {
            setBuiltInHidden(box, 0);
        });
        state.renderedItemId = null;
        // 清空 pending 后，上一轮遗留的兜底定时器即使触发也不会再操作新页面
        state.pendingItemId = null;
    }

    function scan() {
        var itemId = currentItemId();
        if (!itemId) {
            return;
        }

        var boxes = primaryBoxes();
        if (!boxes.length) {
            return;
        }

        if (state.renderedItemId === itemId && document.querySelector('.' + CONTAINER_CLASS)) {
            return;
        }

        if (state.pendingItemId === itemId) {
            return;
        }

        state.pendingItemId = itemId;
        var targetBox = boxes[0];

        // ① 手上已有上次的结果就先渲染，不空等网络
        var local = readClientCache(itemId);
        if (local) {
            state.renderedItemId = itemId;
            render(local, targetBox);
        } else if ((state.config || {}).hideBuiltInRatings !== false) {
            // ② 没有缓存时先软隐藏原生评分：避免「先看到 Jellyfin 评分、几秒后被替换」的跳变
            setBuiltInHidden(targetBox, HIDE_SOFT);
        }

        // ③ 兜底：接口异常/超时时不能让原生评分一直不可见。
        // 定时器用局部变量持有：如果只往 state 里塞一个，快速切换条目时
        // 早先那次的定时器会一直挂着，8 秒后再去操作一个已经销毁的节点。
        var guard = window.setTimeout(function () {
            guard = null;
            if (state.pendingItemId === itemId && !local && document.body.contains(targetBox)) {
                setBuiltInHidden(targetBox, 0);
            }
        }, BUILTIN_GUARD_MS);

        // ④ 配置与评分并行取，少一个串行往返
        Promise.all([
            loadConfig(),
            fetch(apiUrl(API + '/item/' + itemId), { credentials: 'same-origin', headers: authHeaders() })
        ])
            .then(function (results) {
                var response = results[1];
                if (!response || !response.ok) {
                    return null;
                }
                return response.json();
            })
            .then(function (data) {
                state.pendingItemId = null;
                window.clearTimeout(guard);
                if (!data) {
                    setBuiltInHidden(targetBox, 0);
                    return;
                }
                state.renderedItemId = itemId;
                writeClientCache(itemId, data);
                render(data, targetBox);
            })
            .catch(function () {
                state.pendingItemId = null;
                window.clearTimeout(guard);
                setBuiltInHidden(targetBox, 0);
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

    window.setInterval(scan, SCAN_INTERVAL);
    scheduleScan(0);
})();
