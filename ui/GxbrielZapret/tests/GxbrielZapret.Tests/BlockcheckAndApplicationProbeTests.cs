using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using GxbrielZapret.Services.Infrastructure;
using GxbrielZapret.Services.Network;
using GxbrielZapret.Services.Strategies;
using Xunit;

namespace GxbrielZapret.Tests;

public sealed class BlockcheckAndApplicationProbeTests
{
    [Theory]
    [InlineData("winws2 --lua-desync=fake\nfailed")]
    [InlineData("!!!!! working strategy found: winws2 --lua-desync=fake !!!!!")]
    [InlineData("* SUMMARY\nhttps_tls12 ipv4 youtube.com : winws2 not working")]
    [InlineData("* SUMMARY\nhttps_tls12 ipv4 youtube.com : working without bypass")]
    public void TraceAndFailuresNeverBecomeRecommendations(string output) =>
        Assert.Null(Blockcheck2Runner.ExtractPreset(output, new[] { "youtube.com" }));

    [Fact]
    public void OnlyCommonStrategiesCanBeImportedForMultipleDomains()
    {
        const string summary = "* SUMMARY\nhttps_tls12 ipv4 youtube.com : winws2 --wf-tcp=443 --lua-desync=fake";
        Assert.Null(Blockcheck2Runner.ExtractPreset(summary, new[] { "youtube.com", "discord.com" }));
        var preset = Blockcheck2Runner.ExtractPreset(summary + "\n* COMMON\nhttps_tls12 ipv4 : winws2 --wf-tcp=443 --lua-desync=multisplit:pos=1", new[] { "youtube.com", "discord.com" });
        Assert.NotNull(preset);
        Assert.Equal("UNTESTED", preset.Status);
        Assert.False(preset.IsGenerated);
        Assert.Equal("UPSTREAM_BLOCKCHECK2", preset.Provenance);
        Assert.Contains("--lua-desync=multisplit:pos=1", preset.Args);
    }

    [Fact]
    public void ScriptLaunchDoesNotDependOnLoginProfileOrWorkingDirectory()
    {
        const string root = @"C:\Temp\Другой ПК\runtime";
        var runtime = new Blockcheck2Environment(root, root + @"\cygwin\bin\bash.exe", root + @"\blockcheck2.sh", root + @"\cygwin\usr\local\bin\curl.exe", root + @"\winws2.exe", root + @"\mdig.exe");
        var psi = Blockcheck2Runner.CreateStartInfo(runtime, new[] { "YouTube.com", "discord.com", "youtube.com" });
        Assert.Equal(new[] { "--noprofile", "--norc", "/cygdrive/c/Temp/Другой ПК/runtime/blockcheck2.sh" }, psi.ArgumentList);
        Assert.Equal("youtube.com discord.com", psi.Environment["DOMAINS"]);
        Assert.Equal("1", psi.Environment["CURL_HTTPS_GET"]);
        Assert.DoesNotContain("-lc", psi.ArgumentList);
        Assert.False(psi.UseShellExecute);
        Assert.True(psi.RedirectStandardError);
    }

    [Theory]
    [InlineData("'youtube.com'")]
    [InlineData("https://youtube.com/")]
    [InlineData("youtube.com;exit")]
    [InlineData("a b.com")]
    public void InvalidDomainsNeverReachShell(string domain) =>
        Assert.ThrowsAny<ArgumentException>(() => Blockcheck2Runner.NormalizeDomains(new[] { domain }));

    [Fact]
    public async Task BundledEnvironmentReallyStartsOutsideRepository()
    {
        using var runner = new Blockcheck2Runner();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var result = await runner.CheckEnvironmentAsync(deadline.Token);
        Assert.True(result.ExitCode == 0, $"exit={result.ExitCode}\n{result.Output}\n{result.Error}\n{result.LogPath}");
        Assert.Contains("BLOCKCHECK2_ENVIRONMENT_OK", result.Output);
        Assert.True(File.Exists(result.LogPath));
    }

    [Fact]
    public async Task NativeOutputIsLiveAndCancellationKillsChildren()
    {
        var runtime = Blockcheck2Runtime.EnsureInstalled();
        var psi = Blockcheck2Runner.CreateStartInfo(runtime, new[] { "example.org" });
        psi.ArgumentList.Clear();
        foreach (string arg in new[] { "--noprofile", "--norc", "-c", "echo LIVE; echo ERROR >&2; sleep 120 & child=$!; echo CHILD=$child; wait $child" }) psi.ArgumentList.Add(arg);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var lines = new List<string>();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = Blockcheck2Runner.RunProcessAsync(psi, runtime.Script, stop.Token,
            new InlineProgress(line => { lock (lines) lines.Add(line); if (line.StartsWith("CHILD=")) ready.TrySetResult(); }));
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(run.IsCompleted);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(12)));
        Assert.Contains("LIVE", lines);
        Assert.Contains("[stderr] ERROR", lines);
    }

    [Fact]
    public async Task Http200ErrorPageIsNotYouTubeSuccess()
    {
        using var client = Client(_ => Response("<html>access denied</html>"));
        var result = await YouTubeApplicationProbe.CheckAsync(client, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal("HTML", result.Stage);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task HttpErrorsAreNotHealthy(HttpStatusCode status)
    {
        using var client = Client(_ => new HttpResponseMessage(status));
        Assert.False((await YouTubeApplicationProbe.CheckAsync(client, CancellationToken.None)).Ok);
    }

    [Fact]
    public async Task HtmlWithoutWorkingJavaScriptIsNotHealthy()
    {
        using var client = Client(request => request.RequestUri!.AbsolutePath == "/"
            ? Response(Page) : Response("<html>blocked</html>"));
        var result = await YouTubeApplicationProbe.CheckAsync(client, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal("JavaScript", result.Stage);
    }

    [Fact]
    public async Task SuccessfulPageProbeNeverClaimsVideoPlayback()
    {
        using var client = Client(request => request.RequestUri!.AbsolutePath == "/" ? Response(Page) : Response("function main(){}" + new string(' ', 2048)));
        var result = await YouTubeApplicationProbe.CheckAsync(client, CancellationToken.None);
        Assert.True(result.Ok);
        Assert.Contains("не проверены", result.Details);
    }

    [Fact]
    public void UntrustedScriptUrlsAreNotRequested()
    {
        Assert.Null(YouTubeApplicationProbe.FindApplicationScript("<script src='https://evil.test/s/desktop/app.js'></script>"));
        Assert.Null(YouTubeApplicationProbe.FindApplicationScript("<script src='http://www.youtube.com/s/desktop/app.js'></script>"));
        Assert.NotNull(YouTubeApplicationProbe.FindApplicationScript(Page));
    }

    [Fact]
    public async Task ApplicationProbePreservesUserCancellation()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        using var client = Client(_ => Response(Page));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => YouTubeApplicationProbe.CheckAsync(client, stop.Token));
    }

    private const string Page = "<html><ytd-app></ytd-app><script>var ytInitialData = {};</script><script src='/s/desktop/123/jsbin/desktop_polymer.vflset/desktop_polymer.js'></script></html>";
    private static HttpResponseMessage Response(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) => new(new Handler(respond));
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); }
    }
    private sealed class InlineProgress(Action<string> report) : IProgress<string> { public void Report(string value) => report(value); }
}
