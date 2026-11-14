using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ratings.Api;
using Jellyfin.Plugin.Ratings.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Ratings.Web;

/// <summary>
/// 向 Web 客户端 index.html 注入脚本标签的中间件。
///
/// 不改磁盘文件：容器 / 只读 rootfs 下不可写，升级 Jellyfin 也会被覆盖。
/// 两个必须刻意处理的点：条件请求头会让静态中间件回 304（无响应体，注入永远不执行），所以要主动摘掉；
/// 没注入成功时必须留痕，否则外部只表现为「页面没有评分」。
/// </summary>
public sealed class RatingsHtmlInjector
{
    private const string ScriptUrl = "/Ratings/v1/client.js";
    private const string Marker = "data-jellyfin-ratings";

    /// <summary>前几次 index 请求按 Information 记录，之后降到 Debug，避免刷屏。</summary>
    private const int VerboseRequests = 3;

    private static int _loggedActive;
    private static int _loggedSkipped;

    private readonly RequestDelegate _next;
    private readonly ILogger<RatingsHtmlInjector> _logger;

    public RatingsHtmlInjector(RequestDelegate next, ILogger<RatingsHtmlInjector> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method) || !IsIndexRequest(context.Request))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var indexNumber = RatingsRuntimeState.IndexRequests + 1;

        // 只记路径：health 匿名可读，别把别人的 query 与请求头带出去
        RatingsRuntimeState.MarkIndexRequest(LogText.Clean(context.Request.Path.ToString()));

        var config = ConfigProvider.Current;
        if (!config.EnableWebInjection)
        {
            // 配置被关掉后要复位运行期状态，否则 health 会一直显示注入仍然生效
            RatingsRuntimeState.MarkInjectionDisabled();
            await _next(context).ConfigureAwait(false);
            return;
        }

        // 条件请求头会让静态文件中间件回 304，注入永远没机会执行
        var wasConditional = RemoveConditionalHeaders(context.Request);

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();

        try
        {
            context.Response.Body = buffer;
            await _next(context).ConfigureAwait(false);

            if (context.Response.StatusCode != StatusCodes.Status200OK || buffer.Length == 0)
            {
                if (Interlocked.Increment(ref _loggedSkipped) <= VerboseRequests)
                {
                    _logger.LogWarning(
                        "[Ratings] index.html 未注入：状态码 {Status}，响应 {Length} 字节（条件请求={Conditional}），路径 {Path}",
                        context.Response.StatusCode,
                        buffer.Length,
                        wasConditional,
                        LogText.Clean(context.Request.Path.ToString()));
                }

                await CopyBackAsync(buffer, originalBody, context).ConfigureAwait(false);
                return;
            }

            buffer.Seek(0, SeekOrigin.Begin);
            string html;
            var contentStream = Decompress(buffer, context.Response.Headers.ContentEncoding.ToString());
            try
            {
                using var reader = new StreamReader(contentStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
                html = await reader.ReadToEndAsync().ConfigureAwait(false);
            }
            finally
            {
                if (!ReferenceEquals(contentStream, buffer))
                {
                    await contentStream.DisposeAsync().ConfigureAwait(false);
                }
            }

            var originalBytes = Encoding.UTF8.GetByteCount(html);
            var patched = Patch(html, context.Request, out var reason);
            context.Response.Headers.Remove("Content-Encoding");
            context.Response.Headers.Remove("Content-Length");

            var bytes = Encoding.UTF8.GetBytes(patched);
            context.Response.ContentLength = bytes.Length;
            context.Response.Body = originalBody;
            await originalBody.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);

            if (reason == "ok")
            {
                RatingsRuntimeState.MarkInjectionActive();
                RatingsRuntimeState.MarkPatched();
            }
            else if (reason == "no-body-tag")
            {
                _logger.LogWarning(
                    "[Ratings] index.html 里没有 </body>，无法注入（宿主 Web 客户端结构可能已改版）");
            }

            if (indexNumber <= VerboseRequests)
            {
                _logger.LogInformation(
                    "[Ratings] index.html 请求 #{Index} 路径 {Path}：注入结果 {Reason}（原 {Original} 字节 → 新 {PatchedLength} 字节）",
                    indexNumber,
                    LogText.Clean(context.Request.Path.ToString()),
                    reason,
                    originalBytes,
                    bytes.Length);
            }

            if (reason == "ok" && Interlocked.Exchange(ref _loggedActive, 1) == 0)
            {
                _logger.LogInformation(
                    "[Ratings] 脚本注入已生效：宿主 {ServerVersion}",
                    JellyfinCompat.ServerVersion);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Ratings] index.html 注入失败，已回退原始响应");
            context.Response.Body = originalBody;
            if (buffer.Length > 0)
            {
                await CopyBackAsync(buffer, originalBody, context).ConfigureAwait(false);
            }
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    private static async Task CopyBackAsync(MemoryStream buffer, Stream target, HttpContext context)
    {
        buffer.Seek(0, SeekOrigin.Begin);
        context.Response.Body = target;
        await buffer.CopyToAsync(target, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// 摘掉条件请求头，让静态文件中间件返回 200 而不是 304。
    /// </summary>
    /// <returns>是否真的摘掉了东西（用于诊断日志）。</returns>
    private static bool RemoveConditionalHeaders(HttpRequest request)
    {
        var headers = request.Headers;
        var removed = false;

        foreach (var name in ConditionalHeaders)
        {
            if (headers.ContainsKey(name))
            {
                headers.Remove(name);
                removed = true;
            }
        }

        return removed;
    }

    private static readonly string[] ConditionalHeaders =
    {
        "If-None-Match",
        "If-Modified-Since",
        "If-Range",
        "If-Match",
        "If-Unmodified-Since",
        "Range"
    };

    private string Patch(string html, HttpRequest request, out string reason)
    {
        if (html.Contains(Marker, StringComparison.Ordinal))
        {
            // 上游已经带过标签（例如另一个中间件、或者反向代理改写过），没必要重复注入
            reason = "already-present";
            return html;
        }

        var index = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            reason = "no-body-tag";
            return html;
        }

        // PathBase 可能来自反向代理透传的头部（如 X-Forwarded-Prefix），
        // 直接拼进 HTML 属性会被闭合引号注入脚本，这里统一做 HTML 编码。
        var src = HtmlEncoder.Default.Encode(ResolveScriptUrl(request));
        var tag = $"<script {Marker}=\"true\" src=\"{src}\" defer></script>";

        _logger.LogDebug("[Ratings] 已注入客户端脚本: {Tag}", LogText.Clean(tag));
        reason = "ok";
        return html.Insert(index, tag + Environment.NewLine);
    }

    /// <summary>
    /// 计算脚本地址。Jellyfin 的 BaseUrl 映射（app.Map(BaseUrl, ...)）发生在更靠后的管道里，
    /// 本中间件是最外层，此时路径里还带着前缀（/jellyfin/web/index.html），PathBase 是空的。
    /// 只拼 PathBase 会让反代/子路径部署拿到 /Ratings/v1/client.js 这种错地址（404 → 页面无评分）。
    /// </summary>
    private static string ResolveScriptUrl(HttpRequest request)
    {
        var path = request.Path.Value ?? "/";
        var prefix = request.PathBase.HasValue ? request.PathBase.Value!.TrimEnd('/') : string.Empty;

        var tail = IndexOfSuffix(path, "/web/index.html");
        if (tail < 0)
        {
            tail = IndexOfSuffix(path, "/index.html");
        }

        if (tail > 0)
        {
            prefix += path.Substring(0, tail);
        }

        return prefix + ScriptUrl;
    }

    private static int IndexOfSuffix(string path, string suffix)
    {
        if (!path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        return path.Length - suffix.Length;
    }

    private static bool IsIndexRequest(HttpRequest request)
    {
        var accept = request.Headers.Accept.ToString();
        if (accept.Length > 0 && accept.IndexOf("text/html", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        var path = request.Path.Value ?? "/";
        return path.Equals("/", StringComparison.Ordinal)
               || path.EndsWith("/index.html", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
               || path.Equals("/web", StringComparison.OrdinalIgnoreCase);
    }

    private static Stream Decompress(Stream source, string contentEncoding)
    {
        if (string.IsNullOrEmpty(contentEncoding))
        {
            return source;
        }

        if (contentEncoding.Contains("br", StringComparison.OrdinalIgnoreCase))
        {
            return new BrotliStream(source, CompressionMode.Decompress, leaveOpen: true);
        }

        if (contentEncoding.Contains("gzip", StringComparison.OrdinalIgnoreCase))
        {
            return new GZipStream(source, CompressionMode.Decompress, leaveOpen: true);
        }

        if (contentEncoding.Contains("deflate", StringComparison.OrdinalIgnoreCase))
        {
            return new DeflateStream(source, CompressionMode.Decompress, leaveOpen: true);
        }

        return source;
    }
}
