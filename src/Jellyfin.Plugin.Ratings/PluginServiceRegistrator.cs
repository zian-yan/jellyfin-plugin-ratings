using Jellyfin.Plugin.Ratings.Core;
using Jellyfin.Plugin.Ratings.Web;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Ratings;

/// <summary>
/// 向 Jellyfin 的 DI 容器注册服务。Jellyfin 12 以
/// RegisterServices(IServiceCollection, IServerApplicationHost) 为约定签名。
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        JellyfinCompat.Initialize(applicationHost);

        serviceCollection.AddSingleton<RatingsHttp>();
        // 用工厂注册：IApplicationPaths 取不到时退化为纯内存缓存，不让插件起不来
        serviceCollection.AddSingleton(sp => new RatingCache(
            sp.GetService<IApplicationPaths>(),
            sp.GetRequiredService<ILogger<RatingCache>>()));
        serviceCollection.AddSingleton<SourceHealth>();
        serviceCollection.AddSingleton<RequestThrottle>();
        serviceCollection.AddSingleton<RatingAggregator>();

        serviceCollection.AddTransient<RatingsHtmlInjector>();
        serviceCollection.AddTransient<IStartupFilter, RatingsStartupFilter>();
    }
}
