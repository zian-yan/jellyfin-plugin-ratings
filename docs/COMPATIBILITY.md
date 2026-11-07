# Jellyfin 10.11 与 12 的 API 差异对照

本文件是插件兼容层的依据。做法：下载 `Jellyfin.Controller` / `Jellyfin.Model` / `Jellyfin.Common` / `Jellyfin.Data` 的 `10.11.11` 与 `12.4.0` 包，用 `MetadataLoadContext` 逐成员 dump 后对比（不依赖任何"印象"或旧模板）。

## 0. 包与目标框架

| | 10.11.11 | 12.4.0 |
| --- | --- | --- |
| `Jellyfin.Controller` 的 `lib/` | `net9.0` | `net10.0` |
| 依赖的 `Jellyfin.Model` / `Common` / `Naming` | `10.11.11` | `12.4.0` |
| 插件 `targetAbi` | `10.11.0.0` | `12.0.0.0` |

**结论**：两个版本必须产出**两个 zip**，`net9.0` 装不进 12，`net10.0` 装不进 10.11。csproj 用 `TargetFrameworks=net9.0;net10.0` + 条件 `PackageReference` 解决。

```xml
<!-- Directory.Build.props：版本集中一处 -->
<JellyfinAbi10Version>10.11.11</JellyfinAbi10Version>
<JellyfinAbi12Version>12.4.0</JellyfinAbi12Version>

<!-- src/Jellyfin.Plugin.Ratings/Jellyfin.Plugin.Ratings.csproj：按 TFM 条件引用 -->
<ItemGroup Condition="'$(TargetFramework)' == 'net9.0'">
  <PackageReference Include="Jellyfin.Controller" Version="$(JellyfinAbi10Version)" PrivateAssets="all" ExcludeAssets="runtime" />
</ItemGroup>
<ItemGroup Condition="'$(TargetFramework)' == 'net10.0'">
  <PackageReference Include="Jellyfin.Controller" Version="$(JellyfinAbi12Version)" PrivateAssets="all" ExcludeAssets="runtime" />
</ItemGroup>
```

`ExcludeAssets="runtime"` 必须加，否则插件包里会混进 `MediaBrowser.*.dll`，宿主加载时会冲突。

## 1. 完全一致的成员（可放心直接用）

| 类型 | 签名 | 说明 |
| --- | --- | --- |
| `MediaBrowser.Controller.Plugins.IPluginServiceRegistrator` | `void RegisterServices(IServiceCollection, IServerApplicationHost)` | 两版相同；注意参数是 `IServerApplicationHost`（`MediaBrowser.Controller` 命名空间），不是 `IApplicationHost` |
| `MediaBrowser.Common.Plugins.BasePlugin<T>` | `.ctor(IApplicationPaths, IXmlSerializer)` | 两版相同。**10.10 及更早是 `.ctor(IApplicationHost)`**，所以本插件不支持 10.10 |
| `BasePlugin<T>` 成员 | `Configuration`、`ConfigurationType`、`ConfigurationFileName`、`ConfigurationFilePath`、`ConfigurationChanged`、`SaveConfiguration()`、`UpdateConfiguration(BasePluginConfiguration)`、`GetPluginInfo()` | 一致 |
| `MediaBrowser.Model.Plugins.IHasWebPages` | `IEnumerable<PluginPageInfo> GetPages()` | 一致 |
| `PluginPageInfo` | `Name`、`DisplayName`、`EmbeddedResourcePath`、`EnableInMainMenu`、`MenuSection`、`MenuIcon` | 一致 |
| `MediaBrowser.Controller.IServerApplicationHost` | `CoreStartupHasCompleted`、`HttpPort`、`HttpsPort`、`ListenWithHttps`、`FriendlyName`、`RestoreBackupPath`、`GetSmartApiUrl(...)`、`GetLocalApiUrl(...)` | 一致（`ApplicationVersion` 继承自 `MediaBrowser.Common.IApplicationHost`，同样存在） |
| `MediaBrowser.Model.Tasks.IScheduledTask` | `Name`、`Key`、`Description`、`Category`、`ExecuteAsync(IProgress<double>, CancellationToken)`、`GetDefaultTriggers()` | 一致 |
| `TaskTriggerInfo` | `Type`、`TimeOfDayTicks`、`IntervalTicks`、`DayOfWeek`、`MaxRuntimeTicks` | 一致 |

## 2. 有差异、需要兼容处理的成员

### 2.1 `ProviderIds`（未变，但用法要稳）

```csharp
// 两版都是 Dictionary<string, string>
public interface IHasProviderIds { Dictionary<string, string> ProviderIds { get; set; } }
```

12 在 `ProviderIdsExtensions` 上**新增**了 `IsValidProviderId(string, string)` 与 `SetProviderIds(IReadOnlyDictionary)`。写操作在两版上语义不同（12 会校验值合法性），因此插件**只读不写**，且统一走扩展方法：

```csharp
item.TryGetProviderId(MetadataProvider.Tmdb, out var id);
```

### 2.2 `ILibraryManager`

| | 10.11 | 12 |
| --- | --- | --- |
| `GetItemById(Guid)` | ✅ | ✅ |
| `GetItemById<T>(Guid)` / `(Guid, Guid)` / `(Guid, User)` | ✅ | ✅ |
| `ValidatePeopleAsync(...)` | ✅ | ❌ 移除 |
| `ResolveAlternateVersion(...)`、`GetOrCreatePerson(...)`、`GetItemById` 之外的若干方法 | ❌ | ✅ 新增 |
| `ResolvePath(...)` | `(FileSystemMetadata, Folder, IDirectoryService)` | 多一个 `CollectionType?` 参数 |

**结论**：插件只用 `GetItemById(Guid)`，两版通吃；不要碰 `ResolvePath`。

### 2.3 控制器基类（最容易踩的坑）

`Jellyfin.Api.Controllers.ControllerBase` 的命名空间/位置在版本演进中变动过，且它自身还会带来一堆 API 版本相关的依赖。

**处理**：插件控制器直接继承 `Microsoft.AspNetCore.Mvc.ControllerBase` + `[ApiController]` + `[Route("Ratings")]`，不引用 `Jellyfin.Api` 的任何类型。插件程序集中的控制器会被宿主的 MVC ApplicationPart 扫描到，10.11 与 12 行为一致。

### 2.4 `ControllerBase.MetadataProvider` 名称遮蔽

`ControllerBase` 有一个属性 `MetadataProvider`（类型 `IModelMetadataProvider`）。在控制器内部写 `MetadataProvider.Tmdb` 会编译失败：

```
error CS1061: "IModelMetadataProvider" 未包含 "Tmdb" 的定义
```

必须全限定：

```csharp
MediaBrowser.Model.Entities.MetadataProvider.Tmdb
```

这个错误只在把代码放在 `ControllerBase` 派生类里时出现，是「两版都编译、但一改就炸」的典型陷阱，已在 `RatingsController.BuildQuery` 中规避。

### 2.5 运行时版本探测

```csharp
public static Version ServerVersion { get; private set; } = new(10, 11, 0);
public static bool IsV12OrLater => ServerVersion.Major >= 12;
```

来源：`IServerApplicationHost.ApplicationVersion`。用途：
- 日志首行打印「宿主版本 + 兼容分支」，排障时一眼看出装的哪个包；
- `/Ratings/v1/health` 输出 `isV12`、`targetFramework`；
- 为后续可能的 12 专属分支（如新 UI 的 DOM 选择器）预留判断点，而不是散落在业务代码里。

## 3. 反射核对结果（构建产物）

```
net9.0  : REF MediaBrowser.Common 10.11.11.0 / MediaBrowser.Controller 10.11.11.0 / MediaBrowser.Model 10.11.11.0
net10.0 : REF MediaBrowser.Common 12.4.0.0   / MediaBrowser.Controller 12.4.0.0   / MediaBrowser.Model 12.4.0.0
```

两个 TFM 均为 `0 warning / 0 error`；嵌入资源（`Web.client.ratings-client.js`、`Configuration.configPage.html`、21 个 logo）已确认打进程序集。

## 4. 版本支持矩阵

| Jellyfin | 插件包 | 状态 |
| --- | --- | --- |
| 10.11.x | `ratings-jf10.11.zip`（net9.0） | 目标版本，编译 + 反射核对通过 |
| 12.0 ~ 12.4 | `ratings-jf12.zip`（net10.0） | 目标版本，编译 + 反射核对通过 |
| 10.10 及更早 | — | 不支持（`BasePlugin` 构造签名不同） |
| 13.x（未来） | — | 需按新版 `Jellyfin.Controller` 重新编译并复核本文件的差异项 |
