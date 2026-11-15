using System.Net;
using System.Net.Http;
using System.Text.Json;
using GxbrielZapret.Services.Infrastructure;
using GxbrielZapret.Services.Platform;
using Xunit;

namespace GxbrielZapret.Tests;

public sealed class EngineUpdatePolicyTests
{
    [Fact]
    public async Task ApiFetchesPinnedReleaseRatherThanLatest()
    {
        string tag = UpdaterService.SupportedEngineTag;
        using var http = new HttpClient(new Handler(r =>
        {
            Assert.EndsWith("/releases/tags/" + tag, r.RequestUri!.AbsolutePath);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {tag_name = tag, assets = new[] {
                new {name=$"zapret2-{tag}.zip", browser_download_url=$"https://github.com/bol-van/zapret2/releases/download/{tag}/zapret2-{tag}.zip",size=1},
                new {name="sha256sum.txt",browser_download_url=$"https://github.com/bol-van/zapret2/releases/download/{tag}/sha256sum.txt",size=1}
            }})) };
        }));
        var release = await new UpdaterService(http).FetchLatestAsync();
        Assert.Equal(tag, release.Tag);
        Assert.NotNull(release.Sha256Url);
    }

    [Fact]
    public async Task UnsupportedReleaseIsRejectedBeforeDownloadOrFilesystemChanges()
    {
        using var http = new HttpClient(new Handler(_ => throw new Exception("Download must not run")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new UpdaterService(http).InstallAsync(new("v999.0.0", "https://example.com/engine.zip", null, 0)));
    }

    [Fact]
    public async Task HtmlFallbackAlsoUsesPinnedTag()
    {
        string tag = UpdaterService.SupportedEngineTag;
        using var http = new HttpClient(new Handler(r =>
        {
            Assert.DoesNotContain("/latest", r.RequestUri!.AbsoluteUri);
            if (r.RequestUri.Host == "api.github.com") return new(HttpStatusCode.ServiceUnavailable);
            string body = r.RequestUri.AbsolutePath.Contains("expanded_assets")
                ? $"<a href=\"/bol-van/zapret2/releases/download/{tag}/zapret2-{tag}.zip\">zip</a><a href=\"/bol-van/zapret2/releases/download/{tag}/sha256sum.txt\">sha</a>" : "release";
            return new(HttpStatusCode.OK) {RequestMessage=r, Content=new StringContent(body)};
        }));
        Assert.Equal(tag, (await new UpdaterService(http).FetchLatestAsync()).Tag);
    }

    [Fact]
    public void RemoteRegionFailureIsSeparateFromLocalPatchAndDoesNotLeakLogs()
    {
        string message = AntigravityServerDiagnostic.Describe("secret-token prompt-data FAILED_PRECONDITION: User location is not supported for the API use.");
        Assert.Contains("серверный отказ", message);
        Assert.DoesNotContain("secret-token", message);
        Assert.DoesNotContain("prompt-data", message);
        Assert.Contains("не проверен", message);
        Assert.DoesNotContain("серверный отказ", AntigravityServerDiagnostic.Describe("HTTP 200"));
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(response(request));
    }
}
