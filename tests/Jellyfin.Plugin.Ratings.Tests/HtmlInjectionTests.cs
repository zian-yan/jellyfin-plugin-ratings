using System.Text;
using Xunit;

namespace Jellyfin.Plugin.Ratings.Tests;

[Collection(InjectionWebCollection.Name)]
public class HtmlInjectionTests
{
    private readonly InjectionWebFixture _fixture;

    public HtmlInjectionTests(InjectionWebFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task InjectsClientScriptIntoIndexHtml()
    {
        var response = await _fixture.Client.GetAsync("/web/index.html");
        var html = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode);
        Assert.Contains("Ratings/v1/client.js", html);
        Assert.Contains("data-jellyfin-ratings", html);
    }

    [Fact]
    public async Task InjectsBeforeClosingBodyTag()
    {
        var html = await _fixture.Client.GetStringAsync("/web/index.html");

        var scriptIndex = html.LastIndexOf("client.js", StringComparison.Ordinal);
        var bodyIndex = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);

        Assert.True(scriptIndex >= 0 && bodyIndex >= 0);
        Assert.True(scriptIndex < bodyIndex, "脚本标签必须插在 </body> 之前");
    }

    [Fact]
    public async Task InjectedResponseHasCorrectContentLength()
    {
        var response = await _fixture.Client.GetAsync("/web/index.html");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(Encoding.UTF8.GetByteCount(html), response.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task InjectsIntoGzipCompressedResponse()
    {
        var response = await _fixture.Client.GetAsync("/web/compressed/index.html");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("Ratings/v1/client.js", html);
        Assert.Empty(response.Content.Headers.ContentEncoding);
    }

    [Fact]
    public async Task LeavesNonHtmlResponsesUntouched()
    {
        var json = await _fixture.Client.GetStringAsync("/web/plain.json");

        Assert.Equal("{\"a\":1}", json);
    }
}
