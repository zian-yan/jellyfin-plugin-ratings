using System.IO.Compression;
using System.Text;
using Jellyfin.Plugin.Ratings.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Ratings.Tests;

/// <summary>
/// 起一个真实的 Kestrel 站点，只挂载注入中间件与几个模拟响应，
/// 用于验证 index.html 注入（含压缩响应处理）在不依赖 Jellyfin 实例的情况下是否正确。
/// </summary>
public sealed class InjectionWebFixture : IAsyncLifetime
{
    private WebApplication? _app;

    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        app.UseMiddleware<RatingsHtmlInjector>();

        app.MapGet("/web/index.html", async ctx =>
        {
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync("<html><body><div id=\"app\"></div></body></html>");
        });

        app.MapGet("/web/compressed/index.html", async ctx =>
        {
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.Headers.ContentEncoding = "gzip";
            await using var gzip = new GZipStream(ctx.Response.Body, CompressionLevel.Fastest, leaveOpen: true);
            await gzip.WriteAsync(Encoding.UTF8.GetBytes("<html><body></body></html>"));
        });

        app.MapGet("/web/plain.json", async ctx =>
        {
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("{\"a\":1}");
        });

        // 模拟静态文件中间件的 304 行为：带 If-None-Match 就不再返回响应体
        app.MapGet("/web/conditional/index.html", async ctx =>
        {
            if (ctx.Request.Headers.IfNoneMatch.Count > 0)
            {
                ctx.Response.StatusCode = StatusCodes.Status304NotModified;
                return;
            }

            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.Headers.ETag = "\"v1\"";
            await ctx.Response.WriteAsync("<html><body><div id=\"app\"></div></body></html>");
        });

        // 模拟反代子路径（BaseUrl）部署：路径里带前缀，PathBase 为空
        app.MapGet("/jf/web/index.html", async ctx =>
        {
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync("<html><body></body></html>");
        });

        await app.StartAsync();
        _app = app;
        Client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class InjectionWebCollection : ICollectionFixture<InjectionWebFixture>
{
    public const string Name = "injection-web";
}
