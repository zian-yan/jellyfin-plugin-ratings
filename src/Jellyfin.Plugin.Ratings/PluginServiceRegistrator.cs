using Jellyfin.Plugin.Ratings.Core;
using Jellyfin.Plugin.Ratings.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

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
        serviceCollection.AddSingleton<RatingCache>();
        serviceCollection.AddSingleton<SourceHealth>();
        serviceCollection.AddSingleton<RatingAggregator>();

        serviceCollection.AddTransient<RatingsHtmlInjector>();
        serviceCollection.AddTransient<IStartupFilter, RatingsStartupFilter>();
    }
}
