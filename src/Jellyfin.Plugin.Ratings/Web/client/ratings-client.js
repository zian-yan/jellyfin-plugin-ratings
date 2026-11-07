/*
 * Jellyfin Ratings 客户端脚本（服务端插件版）
 * ---------------------------------------------------------------
 * 与旧版用户脚本的区别：
 *   - 不再在浏览器里直连 MDBList / TMDb / 豆瓣，也不再依赖公共 CORS 代理；
 *   - API Key、代理、缓存全部留在服务端，浏览器只发一个同源请求；
 *   - 只做「取数 → 渲染 → 隐藏内置星级」，逻辑尽量短，降低被 Jellyfin 前端改版打断的概率。
 */
(function () {
    'use strict';

    if (window.__jellyfinRatingsLoaded) {
        return;
    }
    window.__jellyfinRatingsLoaded = true;

    var API = 'Ratings/v1';
    var CONTAINER_CLASS = 'jf-ratings-container';
    var SCAN_INTERVAL = 1200;

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

    function currentItemId() {
        var match = (location.hash || '').match(/[?&]id=([0-9a-fA-F-]{32,36})/);
        return match ? match[1] : null;
    }

    function loadConfig() {
        if (state.config) {
            return Promise.resolve(state.config);
        }
        return fetch(apiUrl(API + '/config'), { credentials: 'same-origin' })
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

    function setBuiltInHidden(box, hide) {
        var targets = box.querySelectorAll('.starRatingContainer.mediaInfoItem, .mediaInfoItem.mediaInfoCriticRating');
        Array.prototype.forEach.call(targets, function (element) {
            if (hide) {
                if (!Object.prototype.hasOwnProperty.call(element.dataset, 'jfOrigStyle')) {
                    element.dataset.jfOrigStyle = element.getAttribute('style') || '';
                }
                element.style.color = 'transparent';
                element.style.fontSize = '0';
            } else if (Object.prototype.hasOwnProperty.call(element.dataset, 'jfOrigStyle')) {
                element.setAttribute('style', element.dataset.jfOrigStyle);
                delete element.dataset.jfOrigStyle;
            }
        });
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
            setBuiltInHidden(box, hasData);
        }
    }

    function clearRendered() {
        Array.prototype.forEach.call(document.querySelectorAll('.' + CONTAINER_CLASS), function (node) {
            node.remove();
        });
        primaryBoxes().forEach(function (box) {
            setBuiltInHidden(box, false);
        });
        state.renderedItemId = null;
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

        loadConfig()
            .then(function () {
                return fetch(apiUrl(API + '/item/' + itemId), { credentials: 'same-origin' });
            })
            .then(function (response) {
                if (!response.ok) {
                    return null;
                }
                return response.json();
            })
            .then(function (data) {
                state.pendingItemId = null;
                if (!data) {
                    return;
                }
                state.renderedItemId = itemId;
                render(data, targetBox);
            })
            .catch(function () {
                state.pendingItemId = null;
            });
    }

    function scheduleScan(delay) {
        window.setTimeout(scan, typeof delay === 'number' ? delay : 0);
    }

    window.addEventListener('hashchange', function () {
        clearRendered();
        scheduleScan(800);
    });

    window.addEventListener('popstate', function () {
        clearRendered();
        scheduleScan(800);
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
            }, 300);
        });
        observer.observe(document.documentElement, { childList: true, subtree: true });
    }

    window.setInterval(scan, SCAN_INTERVAL);
    scheduleScan(500);
})();
