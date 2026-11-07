using System;
using Jellyfin.Plugin.Ratings.Api;
using Jellyfin.Plugin.Ratings.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Ratings.Web;

/// <summary>
/// 把注入中间件挂到 Jellyfin 的 ASP.NET Core 管道最前面。
///
/// 该接口（Microsoft.AspNetCore.Hosting.IStartupFilter）在 Jellyfin 12 中可用，
/// 且插件由 IPluginServiceRegistrator 注册时，Jellyfin 会在构建 Host 之前收集到这些注册
/// （见 Jellyfin.Server/Program.cs 的 ConfigureServices(services =&gt; appHost.Init(services))）。
/// 如宿主未收集（例如被第三方启动器提前构建 Host），插件会自动退化为「手动引入脚本」模式，
/// 可通过 /Ratings/v1/health 的 middlewareMounted 字段确认。
/// </summary>
public sealed class RatingsStartupFilter : IStartupFilter
{
    private static int _logged;

    private readonly ILogger<RatingsStartupFilter> _logger;

    public RatingsStartupFilter(ILogger<RatingsStartupFilter> logger)
    {
        _logger = logger;
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        // 这行日志是排障的第一个锚点：出现在启动日志里 = 中间件确实进了管道；
        // 没出现 = 宿主的 Host 在插件注册之前就 Build 完了，注入不可能生效。
        if (System.Threading.Interlocked.Exchange(ref _logged, 1) == 0)
        {
            RatingsRuntimeState.MarkMiddlewareMounted();
            _logger.LogInformation(
                "[Ratings] Web 注入中间件已挂载（宿主 {ServerVersion}，插件 {PluginVersion}）",
                JellyfinCompat.ServerVersion,
                typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "?");
        }

        return app =>
        {
            app.UseMiddleware<RatingsHtmlInjector>();
            next(app);
        };
    }
}
