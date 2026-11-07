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
/// 为什么不用改磁盘文件：原 README 的「改 index.html」方式在容器/只读 rootfs 下不可用，
/// 且升级 Jellyfin 会被覆盖。中间件在响应阶段追加一行脚本，零文件写入、随插件启停自动生效。
/// </summary>
public sealed class RatingsHtmlInjector
{
    private const string ScriptUrl = "/Ratings/v1/client.js";
    private const string Marker = "data-jellyfin-ratings";

    private static int _loggedActive;

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

        var config = ConfigProvider.Current;
        if (!config.EnableWebInjection)
        {
            // 配置被关掉后要复位运行期状态，否则 health 会一直显示注入仍然生效
            RatingsRuntimeState.MarkInjectionDisabled();
            await _next(context).ConfigureAwait(false);
            return;
        }

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();

        try
        {
            context.Response.Body = buffer;
            await _next(context).ConfigureAwait(false);

            if (context.Response.StatusCode != StatusCodes.Status200OK || buffer.Length == 0)
            {
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

            var patched = Patch(html, context.Request);
            context.Response.Headers.Remove("Content-Encoding");
            context.Response.Headers.Remove("Content-Length");

            var bytes = Encoding.UTF8.GetBytes(patched);
            context.Response.ContentLength = bytes.Length;
            context.Response.Body = originalBody;
            await originalBody.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);

            RatingsRuntimeState.MarkInjectionActive();

            if (Interlocked.Exchange(ref _loggedActive, 1) == 0)
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

    private string Patch(string html, HttpRequest request)
    {
        if (html.Contains(Marker, StringComparison.Ordinal))
        {
            return html;
        }

        var index = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return html;
        }

        var pathBase = request.PathBase.HasValue ? request.PathBase.Value : string.Empty;

        // PathBase 可能来自反向代理透传的头部（如 X-Forwarded-Prefix），
        // 直接拼进 HTML 属性会被闭合引号注入脚本，这里统一做 HTML 编码。
        var src = HtmlEncoder.Default.Encode(pathBase + ScriptUrl);
        var tag = $"<script {Marker}=\"true\" src=\"{src}\" defer></script>";

        _logger.LogDebug("[Ratings] 已注入客户端脚本: {Tag}", LogText.Clean(tag));
        return html.Insert(index, tag + Environment.NewLine);
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
