using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.Ratings.Tests;

/// <summary>测试用的最小 IApplicationPaths 实现，只需 DataPath 指向临时目录。</summary>
public sealed class FakeApplicationPaths : IApplicationPaths
{
    public FakeApplicationPaths(string dataPath)
    {
        DataPath = dataPath;
        ProgramDataPath = dataPath;
        CachePath = Path.Combine(dataPath, "cache");
        TempDirectory = Path.Combine(dataPath, "temp");
        LogDirectoryPath = Path.Combine(dataPath, "log");
        ConfigurationDirectoryPath = Path.Combine(dataPath, "config");
        PluginConfigurationsPath = Path.Combine(dataPath, "plugins", "configurations");
        PluginsPath = Path.Combine(dataPath, "plugins");
        VirtualDataPath = "%AppDataPath%";
        WebPath = Path.Combine(dataPath, "web");
        ProgramSystemPath = dataPath;
        ImageCachePath = Path.Combine(dataPath, "cache", "images");
        SystemConfigurationFilePath = Path.Combine(dataPath, "config", "system.xml");
    }

    public string ProgramDataPath { get; }

    public string WebPath { get; }

    public string ProgramSystemPath { get; }

    public string DataPath { get; }

    public string ImageCachePath { get; }

    public string PluginsPath { get; }

    public string PluginConfigurationsPath { get; }

    public string LogDirectoryPath { get; }

    public string ConfigurationDirectoryPath { get; }

    public string SystemConfigurationFilePath { get; }

    public string CachePath { get; }

    public string TempDirectory { get; }

    public string VirtualDataPath { get; }

    public string TrickplayPath => Path.Combine(CachePath, "trickplay");

    public string BackupPath => Path.Combine(DataPath, "backups");

    public void MakeSanityCheckOrThrow()
    {
    }

    public void CreateAndCheckMarker(string path, string markerName, bool recursive)
    {
    }
}
