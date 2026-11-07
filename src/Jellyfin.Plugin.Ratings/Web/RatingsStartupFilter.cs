using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Jellyfin.Plugin.Ratings.Web;

/// <summary>
/// 把注入中间件挂到 Jellyfin 的 ASP.NET Core 管道最前面。
///
/// 该接口（Microsoft.AspNetCore.Hosting.IStartupFilter）在 Jellyfin 12 中可用，
/// 且插件由 IPluginServiceRegistrator 注册时，Jellyfin 会在构建 Host 之前收集到这些注册。
/// 如宿主未收集（例如被第三方启动器提前构建 Host），插件会自动退化为「手动引入脚本」模式，
/// 可通过 /Ratings/v1/health 的 injectionActive 字段确认。
/// </summary>
public sealed class RatingsStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseMiddleware<RatingsHtmlInjector>();
            next(app);
        };
    }
}
