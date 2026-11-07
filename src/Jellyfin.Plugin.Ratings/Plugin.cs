using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Ratings.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Ratings;

/// <summary>
/// 插件入口。
///
/// 构造函数签名是 10.11 与 12 的公共交集：(IApplicationPaths, IXmlSerializer)。
/// 10.10 及更早版本用的是 BasePlugin(IApplicationHost)，本插件不支持那些版本，
/// 因此这里不需要任何条件编译 —— 单签名即可双版本安装。
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static readonly Guid PluginId = new("7f1e5b2a-9c3d-4e8b-a1f6-2d4c6b8e9a10");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>Gets 全局单例，供无 DI 场景（如中间件、客户端脚本配置）读取配置。</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Ratings";

    /// <inheritdoc />
    public override string Description =>
        "以 MDBList / TMDb 等外部评分替换 Jellyfin 内置评分，支持单集评分与多来源角标。";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = "ratings",
            EmbeddedResourcePath = "Jellyfin.Plugin.Ratings.Configuration.configPage.html",
            EnableInMainMenu = false
        };
    }
}
