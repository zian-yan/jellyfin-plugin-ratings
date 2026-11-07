using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Ratings.Configuration;

/// <summary>
/// 插件配置。字段名即 XML 配置文件名，改名会丢失用户配置，请保持兼容。
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    // ---------- 数据源凭据 ----------
    public string MdbListApiKey { get; set; } = string.Empty;

    public string TmdbApiKey { get; set; } = string.Empty;

    public string KinopoiskApiKey { get; set; } = string.Empty;

    // ---------- 数据源开关 ----------
    public bool EnableMdbList { get; set; } = true;

    public bool EnableTmdb { get; set; } = true;

    public bool EnableAniList { get; set; } = false;

    public bool EnableKinopoisk { get; set; } = false;

    public bool EnableDouban { get; set; } = false;

    public bool EnableAllocine { get; set; } = false;

    public bool EnableCertifications { get; set; } = false;

    /// <summary>剧集分集页只显示该集 TMDb 评分（与原脚本行为一致）。</summary>
    public bool EnableEpisodeRatings { get; set; } = true;

    // ---------- 展示 ----------
    /// <summary>用外部评分替换 Jellyfin 内置星级/影评人评分。</summary>
    public bool HideBuiltInRatings { get; set; } = true;

    /// <summary>单个条目最多展示多少个来源。</summary>
    public int MaxRatingsPerItem { get; set; } = 6;

    /// <summary>鼠标悬停显示「来源: 分数」提示。</summary>
    public bool ShowSourceTooltip { get; set; } = true;

    // ---------- 运行参数 ----------
    /// <summary>缓存分钟数，0 表示不缓存。</summary>
    public int CacheMinutes { get; set; } = 720;

    /// <summary>单个外部请求超时（秒）。</summary>
    public int TimeoutSeconds { get; set; } = 8;

    /// <summary>可选的 HTTP 出口代理，例如 http://127.0.0.1:7890 。</summary>
    public string HttpProxy { get; set; } = string.Empty;

    /// <summary>自动向前端 index.html 注入脚本。</summary>
    public bool EnableWebInjection { get; set; } = true;

    /// <summary>为 true 时，API 需要 Jellyfin 登录态或 API Key。</summary>
    public bool RequireAuthentication { get; set; } = false;

    /// <summary>请求失败时对外暴露的错误摘要（调试用）。</summary>
    public bool ExposeErrors { get; set; } = true;

    /// <summary>同一数据源连续失败多少次后进入冷却。</summary>
    public int FailureThreshold { get; set; } = 3;

    /// <summary>失败冷却时长（分钟）。</summary>
    public int CooldownMinutes { get; set; } = 10;
}
