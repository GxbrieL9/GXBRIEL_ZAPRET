using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using GxbrielZapret.Models;
using GxbrielZapret.Services.Network;
using Xunit;

namespace GxbrielZapret.Tests;

public sealed class ApplicationFlowProbeTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); }
    }
    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) => new(new Handler(respond));
    private static string Page(string format) => "<script>var ytInitialPlayerResponse = {\"streamingData\":{\"adaptiveFormats\":[" + format + "]}};</script>";
    private static string Format(string url) => JsonSerializer.Serialize(new { mimeType = "video/mp4; codecs=avc1", url });
    private static HttpResponseMessage Video(int length = ApplicationFlowProbe.VideoSampleBytes, bool valid = true)
    {
        byte[] bytes = new byte[length];
        if (valid && length >= 8) Encoding.ASCII.GetBytes("ftyp").CopyTo(bytes, 4);
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new("video/mp4");
        return response;
    }

    [Fact]
    public async Task PartialJavaScriptReadDoesNotRequireTheResponseToFitTheLimit()
    {
        using var content = new ByteArrayContent(new byte[65537]);
        string sample = await YouTubeApplicationProbe.ReadBoundedAsync(content, 65535, false, CancellationToken.None);
        Assert.Equal(65535, sample.Length);
    }

    [Fact]
    public async Task VideoBenchmarkReadsMegabytesAndReportsTransferMeasurements()
    {
        Assert.True(ApplicationFlowProbe.VideoSampleBytes >= 2 * 1024 * 1024);
        using var client = Client(_ => Video());
        var reports = new List<string>();
        var result = await ApplicationFlowProbe.ReadVideoSampleAsync(client, new("https://rr.googlevideo.com/video?sig=secret"), CancellationToken.None, reports.Add);
        Assert.Contains(reports, text => text.Contains("ожидание первых данных"));
        Assert.Contains(reports, text => text.Contains("2048 / 2048"));
        Assert.DoesNotContain(reports, text => text.Contains("secret"));
        Assert.True(result.Passed);
        Assert.Contains("Мбит/с", result.Detail);
        Assert.Contains("первые данные", result.Detail);
    }

    [Fact]
    public async Task VideoProofRequiresAnActualBoundedMediaSample()
    {
        int calls = 0;
        using var client = Client(request =>
        {
            if (++calls == 1) return new(HttpStatusCode.OK) { Content = new StringContent(Page(Format("https://rr1---test.googlevideo.com/videoplayback?id=test"))) };
            Assert.Equal("rr1---test.googlevideo.com", request.RequestUri!.Host);
            Assert.Contains("range=0-2097151", request.RequestUri.Query);
            Assert.Equal("bytes=0-2097151", request.Headers.Range!.ToString());
            return Video(ApplicationFlowProbe.VideoSampleBytes * 2);
        });
        var result = await ApplicationFlowProbe.ProbeYouTubeVideoAsync(client, CancellationToken.None);
        Assert.True(result.Passed);
        Assert.Equal(ApplicationFlowProbe.VideoSampleBytes, result.Bytes);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("https://evilgooglevideo.com/video")]
    [InlineData("https://rr.googlevideo.com.evil.test/video")]
    [InlineData("http://rr.googlevideo.com/video")]
    [InlineData("https://user@rr.googlevideo.com/video")]
    [InlineData("https://rr.googlevideo.com:444/video")]
    public void RejectsUntrustedVideoAddresses(string url) => Assert.Null(ApplicationFlowProbe.FindDirectVideoUrl(Page(Format(url))));

    [Fact]
    public async Task PlayerFallbackAndSessionCacheStillRequireFreshBytesForEveryCandidate()
    {
        int pages = 0, players = 0, downloads = 0;
        using var client = Client(request =>
        {
            if (request.RequestUri!.Host.EndsWith(".googlevideo.com")) { downloads++; return Video(); }
            if (request.Method == HttpMethod.Post)
            {
                players++;
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"streamingData\":{\"formats\":[" + Format("https://rr.googlevideo.com/video?sig=test") + "]}}") };
            }
            pages++;
            return new(HttpStatusCode.OK) { Content = new StringContent("\"VISITOR_DATA\":\"anonymous\";" + Page("{\"mimeType\":\"video/mp4\",\"signatureCipher\":\"s=encrypted\"}")) };
        });
        var session = new YouTubeMediaSession();
        Assert.True((await ApplicationFlowProbe.ProbeYouTubeVideoAsync(client, session, CancellationToken.None)).Passed);
        Assert.True((await ApplicationFlowProbe.ProbeYouTubeVideoAsync(client, session, CancellationToken.None)).Passed);
        Assert.Equal(1, pages);
        Assert.Equal(1, players);
        Assert.Equal(2, downloads);
    }

    [Fact]
    public async Task UnavailablePlayerStopsAfterTwoContextsAndIsNotBroken()
    {
        int players = 0;
        using var client = Client(request =>
        {
            if (request.Method == HttpMethod.Post) players++;
            return new(HttpStatusCode.OK) { Content = new StringContent(request.Method == HttpMethod.Post ? "{}" : "\"VISITOR_DATA\":\"anonymous\"") };
        });
        var result = await ApplicationFlowProbe.ProbeYouTubeVideoAsync(client, CancellationToken.None);
        Assert.Null(result.Passed);
        Assert.Equal(2, players);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(410)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task ExpiredOrRejectedMediaUrlIsUnknown(int status)
    {
        using var client = Client(_ => new((HttpStatusCode)status));
        Assert.Null((await ApplicationFlowProbe.ReadVideoSampleAsync(client, new("https://rr.googlevideo.com/video"), CancellationToken.None)).Passed);
    }

    [Fact]
    public void WindowAssignmentAndPlainSignedCipherAreSupportedWithoutExecutingScripts()
    {
        string cipher = "url=" + Uri.EscapeDataString("https://rr.googlevideo.com/video?id=test") + "&sig=abc&sp=signature";
        string format = JsonSerializer.Serialize(new { mimeType = "video/mp4", signatureCipher = cipher });
        var stream = ApplicationFlowProbe.FindDirectVideoUrl(Page(format).Replace("var ytInitialPlayerResponse", "window[\"ytInitialPlayerResponse\"]"));
        Assert.NotNull(stream);
        Assert.Contains("signature=abc", stream.Query);
    }

    [Theory]
    [InlineData("{\"mimeType\":\"video/mp4\",\"signatureCipher\":\"s=encrypted\"}")]
    [InlineData("{\"mimeType\":null,\"url\":\"https://rr.googlevideo.com/video\"}")]
    [InlineData("{\"mimeType\":42,\"url\":42}")]
    [InlineData("null")]
    public async Task MissingPlayableUrlIsUnknownRatherThanGreen(string format)
    {
        using var client = Client(_ => new(HttpStatusCode.OK) { Content = new StringContent(Page(format)) });
        var result = await ApplicationFlowProbe.ProbeYouTubeVideoAsync(client, CancellationToken.None);
        Assert.Null(result.Passed);
    }

    [Theory]
    [InlineData(1024, true)]
    [InlineData(ApplicationFlowProbe.VideoSampleBytes, false)]
    public async Task ShortMediaAndHtmlDisguisedAsVideoNeverPass(int size, bool valid)
    {
        using var client = Client(_ => Video(size, valid));
        var result = await ApplicationFlowProbe.ReadVideoSampleAsync(client, new("https://rr.googlevideo.com/video"), CancellationToken.None);
        Assert.False(result.Passed);
    }

    [Fact]
    public async Task VideoCancellationPropagatesInsteadOfSavingSuccess()
    {
        using var client = Client(_ => Video());
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ApplicationFlowProbe.ProbeYouTubeVideoAsync(client, stop.Token));
    }

    [Theory]
    [InlineData("{\"op\":10,\"d\":{\"heartbeat_interval\":41250}}", true)]
    [InlineData("{\"op\":0,\"d\":{\"heartbeat_interval\":41250}}", false)]
    [InlineData("{\"op\":10,\"d\":{\"heartbeat_interval\":0}}", false)]
    [InlineData("{\"op\":\"10\",\"d\":null}", false)]
    [InlineData("{\"op\":10,\"d\":42}", false)]
    [InlineData("[]", false)]
    public void OnlyProtocolHelloConfirmsGateway(string json, bool expected) => Assert.Equal(expected, ApplicationFlowProbe.IsGatewayHello(json));

    [Theory]
    [InlineData("{\"url\":\"wss://gateway.discord.gg\"}", true)]
    [InlineData("{\"url\":\"wss://gateway.discord.gg.evil.test\"}", false)]
    [InlineData("{\"url\":\"ws://gateway.discord.gg\"}", false)]
    [InlineData("{\"url\":42}", false)]
    [InlineData("null", false)]
    public void DiscordApiMustReturnTheRealGateway(string json, bool expected) => Assert.Equal(expected, ApplicationFlowProbe.IsDiscordGatewayResponse(json));

    [Fact]
    public async Task MissingMediaEvidenceIsNotReportedAsNetworkFailure()
    {
        using var monitor = new ServiceHealthMonitor(
            _ => Task.FromResult(((bool?)true, "API + Hello")),
            _ => Task.FromResult(((bool?)null, "no direct media URL")));
        var result = await monitor.ProbeAllAsync(CancellationToken.None);
        Assert.Equal(ServiceHealthState.Working, result.Discord);
        Assert.Equal(ServiceHealthState.Inconclusive, result.YouTube);
        Assert.False(result.IsHealthyFor(AutoScope.Both));
        Assert.False(result.IsConclusiveFor(AutoScope.Both));
        Assert.True(result.IsConclusiveFor(AutoScope.Discord));
    }

    [Theory]
    [InlineData(ServiceHealthState.Failed, true)]
    [InlineData(ServiceHealthState.NotChecked, false)]
    [InlineData(ServiceHealthState.Checking, false)]
    [InlineData(ServiceHealthState.Inconclusive, false)]
    public void OnlyConclusiveResultsMayDriveAutomaticStrategyChanges(ServiceHealthState youtube, bool conclusive)
    {
        var result = new ServiceHealthSnapshot(ServiceHealthState.Working, youtube, ServiceHealthState.NotChecked, DateTime.UtcNow);
        Assert.Equal(conclusive, result.IsConclusiveFor(AutoScope.Both));
    }

    [Fact]
    public void ABooleanWithoutReceivedVideoBytesIsNotCompleteEvidence()
    {
        var flows = new ApplicationFlowResult(new(true, "claimed success"), new(true, "hello"));
        Assert.False(flows.Passes(AutoScope.Both));
        Assert.False(flows.Passes(AutoScope.YouTube));
        Assert.True(flows.Passes(AutoScope.Discord));
    }

    [Fact]
    public async Task VisionOs403FallsBackToAndroidContextAndCompletesMediaSample()
    {
        int players = 0, downloads = 0;
        using var client = Client(request =>
        {
            if (request.RequestUri!.Host == "rr-vision.googlevideo.com")
            {
                downloads++;
                return new(HttpStatusCode.Forbidden);
            }
            if (request.RequestUri.Host == "rr-android.googlevideo.com")
            {
                downloads++;
                return Video();
            }
            if (request.Method == HttpMethod.Post)
            {
                players++;
                string host = players == 1 ? "rr-vision.googlevideo.com" : "rr-android.googlevideo.com";
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"streamingData\":{\"formats\":[" + Format($"https://{host}/videoplayback?id=test") + "]}}")
                };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent("\"VISITOR_DATA\":\"anonymous\"") };
        });

        var result = await ApplicationFlowProbe.ProbeYouTubeVideoAsync(client, new YouTubeMediaSession(), CancellationToken.None);
        Assert.True(result.Passed);
        Assert.Equal(ApplicationFlowProbe.VideoSampleBytes, result.Bytes);
        Assert.Equal(2, players);
        Assert.Equal(2, downloads);
    }

    [Fact]
    public async Task Repeated403AcrossBothContextsRemainsUnknownWithoutFloodingPlayer()
    {
        int players = 0, downloads = 0;
        using var client = Client(request =>
        {
            if (request.RequestUri!.Host.EndsWith(".googlevideo.com")) { downloads++; return new(HttpStatusCode.Forbidden); }
            if (request.Method == HttpMethod.Post)
            {
                players++;
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"streamingData\":{\"formats\":[" + Format($"https://rr{players}.googlevideo.com/videoplayback?id={players}") + "]}}")
                };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent("\"VISITOR_DATA\":\"anonymous\"") };
        });

        var result = await ApplicationFlowProbe.ProbeYouTubeVideoAsync(client, new YouTubeMediaSession(), CancellationToken.None);
        Assert.Null(result.Passed);
        Assert.Equal(2, players);
        Assert.Equal(2, downloads);
    }
}
