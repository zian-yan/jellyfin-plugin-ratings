using System.Text.Json;
using Jellyfin.Plugin.Ratings.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Ratings.Tests;

/// <summary>
/// 嵌入资源（logo / 客户端脚本）与配置接口的契约测试。
/// 控制器不依赖库管理器，因此这两个动作可以脱离 Jellyfin 实例直接调用。
/// </summary>
public class EmbeddedAssetTests
{
    private static RatingsController CreateController()
        => new(null!, null!, NullLogger<RatingsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    [Fact]
    public void ServesEmbeddedLogo()
    {
        var result = CreateController().GetLogo("douban");

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("image/png", file.ContentType);
        Assert.True(file.FileContents.Length > 500, "logo 字节数异常，可能是嵌入资源没打进去");
    }

    [Theory]
    [InlineData("../appsettings")]
    [InlineData("..\\appsettings")]
    [InlineData("a/b")]
    [InlineData("logo.png")]
    public void RejectsUnsafeLogoName(string name)
    {
        var result = CreateController().GetLogo(name);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void UnknownLogoReturnsNotFound()
    {
        var result = CreateController().GetLogo("not_a_logo");

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public void ServesClientScriptFromEmbeddedResource()
    {
        var result = CreateController().GetClientScript();

        var content = Assert.IsType<ContentResult>(result);
        Assert.NotNull(content.Content);
        Assert.Contains("__jellyfinRatingsLoaded", content.Content);
    }

    [Fact]
    public void ClientConfigExposesNoSecrets()
    {
        var result = CreateController().GetClientConfig();

        var content = Assert.IsType<ContentResult>(result);
        var json = content.Content!;

        Assert.Contains("hideBuiltInRatings", json);
        Assert.DoesNotContain("ApiKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apikey", json, StringComparison.OrdinalIgnoreCase);

        var dto = JsonSerializer.Deserialize<ClientConfigDto>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(dto);
        Assert.True(dto!.MaxRatingsPerItem > 0);
    }
}
