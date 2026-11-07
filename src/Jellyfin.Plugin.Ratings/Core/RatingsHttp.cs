using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ratings.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Ratings.Core;

/// <summary>统一的 HTTP 结果，避免各数据源重复处理异常分支。</summary>
public readonly record struct HttpResult(int Status, string Body, string? Error)
{
    public bool Ok => Status >= 200 && Status < 300;

    public bool TransportFailed => Status == 0;
}

/// <summary>
/// 服务端 HTTP 封装。原用户脚本依赖 GM_xmlhttpRequest + 公共 CORS 代理，
/// 迁到服务端后不再需要代理；仅在配置了 HttpProxy 时走显式代理（应对区域限制）。
/// </summary>
public sealed class RatingsHttp : IDisposable
{
    private const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36";

    private readonly ConcurrentDictionary<string, HttpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<RatingsHttp> _logger;

    public RatingsHttp(ILogger<RatingsHttp> logger)
    {
        _logger = logger;
    }

    private HttpClient GetClient(PluginConfiguration config, int timeoutSeconds)
    {
        var proxy = config.HttpProxy?.Trim() ?? string.Empty;
        var key = proxy + "|" + timeoutSeconds;
        return _clients.GetOrAdd(key, _ =>
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(Math.Max(3, timeoutSeconds))
            };

            if (!string.IsNullOrEmpty(proxy))
            {
                try
                {
                    handler.Proxy = new WebProxy(proxy);
                    handler.UseProxy = true;
                }
                catch (UriFormatException ex)
                {
                    _logger.LogWarning(ex, "[Ratings] 代理地址无效，已回退直连: {Proxy}", proxy);
                }
            }

            return new HttpClient(handler, disposeHandler: true)
            {
                Timeout = Timeout.InfiniteTimeSpan // 超时由调用方的 CancellationToken 控制
            };
        });
    }

    public Task<HttpResult> GetAsync(
        string url,
        PluginConfiguration config,
        ILogger logger,
        IDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Get, url, null, config, logger, headers, cancellationToken);

    public Task<HttpResult> PostJsonAsync(
        string url,
        string jsonBody,
        PluginConfiguration config,
        ILogger logger,
        IDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/json"
        };
        if (headers is not null)
        {
            foreach (var kv in headers)
            {
                merged[kv.Key] = kv.Value;
            }
        }

        return SendAsync(HttpMethod.Post, url, jsonBody, config, logger, merged, cancellationToken);
    }

    private async Task<HttpResult> SendAsync(
        HttpMethod method,
        string url,
        string? body,
        PluginConfiguration config,
        ILogger logger,
        IDictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(config.TimeoutSeconds, 2, 60));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        using var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json,text/html;q=0.9,*/*;q=0.8");

        if (headers is not null)
        {
            foreach (var kv in headers)
            {
                request.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            }
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        try
        {
            var client = GetClient(config, (int)timeout.TotalSeconds);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token)
                .ConfigureAwait(false);

            var text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;

            if (status is >= 300 and < 400)
            {
                logger.LogDebug("[Ratings] {Host} 返回 {Status}（已跟随重定向）", SafeHost(url), status);
            }

            return new HttpResult(status, text, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("[Ratings] 请求超时 {Seconds}s: {Host}{Path}", timeout.TotalSeconds, SafeHost(url), SafePath(url));
            return new HttpResult(0, string.Empty, "timeout");
        }
        catch (OperationCanceledException)
        {
            return new HttpResult(0, string.Empty, "cancelled");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "[Ratings] 请求失败: {Host}{Path}", SafeHost(url), SafePath(url));
            return new HttpResult(0, string.Empty, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Ratings] 未预期错误: {Host}{Path}", SafeHost(url), SafePath(url));
            return new HttpResult(0, string.Empty, ex.Message);
        }
    }

    /// <summary>日志里只保留域名与路径，避免把 apikey 查询串打进日志。</summary>
    private static string SafeHost(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "?";

    private static string SafePath(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            return string.Empty;
        }

        var path = u.AbsolutePath;
        var query = u.Query;
        if (string.IsNullOrEmpty(query))
        {
            return path;
        }

        // 查询串里去掉疑似密钥字段
        var parts = query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>();
        foreach (var part in parts)
        {
            var name = part.Split('=', 2)[0];
            if (name.Contains("key", StringComparison.OrdinalIgnoreCase)
                || name.Contains("token", StringComparison.OrdinalIgnoreCase)
                || name.Contains("apikey", StringComparison.OrdinalIgnoreCase))
            {
                kept.Add(name + "=***");
            }
            else
            {
                kept.Add(part);
            }
        }

        return path + "?" + string.Join('&', kept);
    }

    public void Dispose()
    {
        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }

        _clients.Clear();
    }
}
