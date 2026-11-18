using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using GxbrielZapret.Services.Platform;
using Xunit;

namespace GxbrielZapret.Tests;

public sealed class AntigravityBuiltinRoutingTests
{
    private const string Host = "daily-cloudcode-pa.googleapis.com";

    [Fact]
    public async Task ApiAliasDiscoversPublishedGatewayInsteadOfGenuineGoogleDns()
    {
        string gateway = "cloudcode-pa.googleapis.com";
        Assert.Equal(gateway, AntigravitySmartDns.GatewayName("cloudaicompanion-pa.googleapis.com"));
        using var http = new HttpClient(new Handler(request =>
        {
            string encoded = request.RequestUri!.Query[5..].Replace('-', '+').Replace('_', '/');
            encoded += new string('=', (4 - encoded.Length % 4) % 4);
            Assert.Equal(AntigravitySmartDns.BuildQuery(gateway), Convert.FromBase64String(encoded));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Answer(gateway, [142, 250, 1, 1])) };
            response.Content.Headers.ContentType = new("application/dns-message");
            return response;
        }));
        using var routes = new AntigravitySmartDns(http);
        await Assert.ThrowsAsync<IOException>(() => routes.ConnectAsync("cloudaicompanion-pa.googleapis.com", CancellationToken.None));
    }

    [Fact]
    public async Task GenuineGoogleDnsAnswerDoesNotMasqueradeAsExternalRoute()
    {
        Assert.True(AntigravityGoogleNetworks.Contains(IPAddress.Parse("142.250.1.1")));
        Assert.True(AntigravityGoogleNetworks.Contains(IPAddress.Parse("172.217.1.1")));
        Assert.True(AntigravityGoogleNetworks.Contains(IPAddress.Parse("2001:4860::8888")));
        Assert.False(AntigravityGoogleNetworks.Contains(IPAddress.Parse("217.144.190.216")));
        int requests = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            requests++;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Answer(Host, [142, 250, 1, 1])) };
            response.Content.Headers.ContentType = new("application/dns-message");
            return response;
        }));
        using var routes = new AntigravitySmartDns(http);
        await Assert.ThrowsAsync<IOException>(() => routes.ConnectAsync(Host, CancellationToken.None));
        Assert.Equal(AntigravitySmartDns.Providers.Length, requests);
    }

    [Fact]
    public async Task SwitchingRouteClosesApiTunnelAndKeepsUnrelatedTunnel()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        using var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        var peers = new System.Collections.Concurrent.ConcurrentBag<TcpClient>();
        using var relay = new AntigravityRelayService(async (host, ct) =>
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync((IPEndPoint)upstream.LocalEndpoint, ct);
            peers.Add(await upstream.AcceptTcpClientAsync(ct));
            return new NetworkStream(socket, ownsSocket: true);
        });
        try
        {
            int port = new Uri(relay.Start()).Port;
            using var api = new TcpClient();
            using var other = new TcpClient();
            await api.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            using var apiStream = api.GetStream();
            await apiStream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {Host}:443 HTTP/1.1\r\n\r\n"), timeout.Token);
            Assert.Contains("200", await ReadHeaders(apiStream, timeout.Token));
            await other.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            using var otherStream = other.GetStream();
            await otherStream.WriteAsync(Encoding.ASCII.GetBytes("CONNECT github.com:443 HTTP/1.1\r\n\r\n"), timeout.Token);
            Assert.Contains("200", await ReadHeaders(otherStream, timeout.Token));
            relay.NextRoute();
            Assert.Equal(0, await apiStream.ReadAsync(new byte[1], timeout.Token));
            using var shortWait = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { _ = await otherStream.ReadAsync(new byte[1], shortWait.Token); });
            await relay.StopAsync().WaitAsync(timeout.Token);
        }
        finally { foreach (var peer in peers) peer.Dispose(); }
    }

    [Fact]
    public void ChildLaunchCoversStockGoAndAlternativePatchesWithoutPersistingProxy()
    {
        string path = Path.Combine(Path.GetTempPath(), "gx-launch-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(path, "fixture");
        try
        {
            string? parent = Environment.GetEnvironmentVariable("AG_LS_PROXY");
            var info = AntigravityLauncher.BuildStartInfo(path, false, "http://127.0.0.1:12345", selectiveHttps: true);
            Assert.Equal("http://127.0.0.1:12345", info.Environment["AG_LS_PROXY"]);
            Assert.Equal("http://127.0.0.1:12345", info.Environment["HTTPS_PROXY"]);
            info.Environment.TryGetValue("HTTP_PROXY", out string? childHttp);
            Assert.Equal(Environment.GetEnvironmentVariable("HTTP_PROXY"), childHttp);
            Assert.False(info.Environment.ContainsKey("NO_PROXY"));
            Assert.Equal(parent, Environment.GetEnvironmentVariable("AG_LS_PROXY"));
            var direct = AntigravityLauncher.BuildStartInfo(path, true, "http://127.0.0.1:12345");
            Assert.False(direct.Environment.ContainsKey("AG_LS_PROXY"));
            Assert.False(direct.Environment.ContainsKey("HTTPS_PROXY"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RegionFeedbackIgnoresOldLogsThrottlesAndLimitsSwitches()
    {
        string file = Path.GetTempFileName();
        const string failure = "HTTP 400 FAILED_PRECONDITION: User location is not supported for the API use\n";
        long now = 1000;
        try
        {
            File.WriteAllText(file, failure);
            var feedback = new AntigravityRouteFeedback(file, () => now);
            Assert.Equal(AntigravityRouteFeedbackResult.None, feedback.Poll());
            File.AppendAllText(file, failure);
            Assert.Equal(AntigravityRouteFeedbackResult.SwitchRoute, feedback.Poll());
            File.AppendAllText(file, failure);
            Assert.Equal(AntigravityRouteFeedbackResult.None, feedback.Poll());
            now += 31000; File.AppendAllText(file, failure);
            Assert.Equal(AntigravityRouteFeedbackResult.SwitchRoute, feedback.Poll());
            now += 31000; File.AppendAllText(file, failure);
            Assert.Equal(AntigravityRouteFeedbackResult.Exhausted, feedback.Poll());
            now += 121000; File.AppendAllText(file, failure);
            Assert.Equal(AntigravityRouteFeedbackResult.SwitchRoute, feedback.Poll());
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void RegionFeedbackWaitsForCompletedNewErrorAndHandlesLogTruncation()
    {
        string file = Path.Combine(Path.GetTempPath(), "gx-feedback-" + Guid.NewGuid().ToString("N"));
        try
        {
            var feedback = new AntigravityRouteFeedback(file);
            Assert.Equal(AntigravityRouteFeedbackResult.None, feedback.Poll());
            File.WriteAllText(file, "HTTP 400 User location is not ");
            Assert.Equal(AntigravityRouteFeedbackResult.None, feedback.Poll());
            File.AppendAllText(file, "supported\n");
            Assert.Equal(AntigravityRouteFeedbackResult.SwitchRoute, feedback.Poll());
            feedback.Reset();
            File.WriteAllText(file, "403 LOCATION_NOT_ELIGIBLE\n");
            Assert.Equal(AntigravityRouteFeedbackResult.SwitchRoute, feedback.Poll());
            Assert.False(AntigravityRouteFeedback.HasRegionFailure("A prompt mentioning User location is not supported\n"));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void DnsAnswerValidatesQuestionCompressionTtlAndPublicAddresses()
    {
        byte[] packet = Answer(Host, [109, 120, 131, 200], 30);
        var result = AntigravitySmartDns.ParseAnswer(packet, Host);
        Assert.Equal(IPAddress.Parse("109.120.131.200"), Assert.Single(result.Addresses));
        Assert.Equal(30, result.Ttl);
        Assert.Empty(AntigravitySmartDns.ParseAnswer(Answer(Host, [127, 0, 0, 1]), Host).Addresses);
        Assert.Throws<IOException>(() => AntigravitySmartDns.ParseAnswer(packet, "other.googleapis.com"));
        packet[0] = 1;
        Assert.Throws<IOException>(() => AntigravitySmartDns.ParseAnswer(packet, Host));
    }

    [Fact]
    public void TruncatedAndCyclicDnsPacketsFailWithinBounds()
    {
        byte[] packet = Answer(Host, [109, 120, 131, 200]);
        for (int length = 0; length < packet.Length; length++)
            Assert.Throws<IOException>(() => AntigravitySmartDns.ParseAnswer(packet[..length], Host));
        packet[12] = 0xc0; packet[13] = 12;
        Assert.Throws<IOException>(() => AntigravitySmartDns.ParseAnswer(packet, Host));
        packet = Answer(Host, [109, 120, 131, 200]);
        packet[2] |= 2;
        Assert.Throws<IOException>(() => AntigravitySmartDns.ParseAnswer(packet, Host));
    }

    [Fact]
    public void UnrelatedAnswerIsNotUsedAsRoute()
    {
        byte[] question = AntigravitySmartDns.BuildQuery(Host);
        byte[] otherName = AntigravitySmartDns.BuildQuery("other.example")[12..^4];
        byte[] packet = [.. question, .. otherName, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4, 109, 120, 131, 200];
        packet[2] = 0x81; packet[3] = 0x80; packet[7] = 1;
        Assert.Empty(AntigravitySmartDns.ParseAnswer(packet, Host).Addresses);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("192.168.1.1")]
    [InlineData("172.16.1.1")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("2001:db8::1")]
    public void ProviderCannotRouteToLocalOrReservedAddress(string address) =>
        Assert.False(AntigravitySmartDns.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public async Task ProviderFailureTriesEveryProviderWithoutSystemDnsFallback()
    {
        var called = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            called.Add(request.RequestUri!.GetLeftPart(UriPartial.Path));
            Assert.Equal(HttpVersion.Version20, request.Version);
            Assert.Contains(request.Headers.Accept, h => h.MediaType == "application/dns-message");
            return new(HttpStatusCode.ServiceUnavailable);
        }));
        using var routes = new AntigravitySmartDns(http);
        await Assert.ThrowsAsync<IOException>(() => routes.ConnectAsync(Host, CancellationToken.None));
        Assert.Equal(AntigravitySmartDns.Providers.Select(p => p.Url), called);
        called.Clear(); routes.NextRoute();
        await Assert.ThrowsAsync<IOException>(() => routes.ConnectAsync(Host, CancellationToken.None));
        Assert.Equal(AntigravitySmartDns.Providers[1].Url, called[0]);
    }

    [Theory]
    [InlineData("GET https://example.com/ HTTP/1.1\r\n\r\n")]
    [InlineData("CONNECT 127.0.0.1:443 HTTP/1.1\r\n\r\n")]
    [InlineData("CONNECT example.com:80 HTTP/1.1\r\n\r\n")]
    [InlineData("CONNECT user@example.com:443 HTTP/1.1\r\n\r\n")]
    [InlineData("CONNECT example.com/path:443 HTTP/1.1\r\n\r\n")]
    [InlineData("CONNECT a..example:443 HTTP/1.1\r\n\r\n")]
    public void RelayRejectsMalformedRequestsAndNonHttpsPorts(string request) => Assert.Null(AntigravityRelayService.ParseTarget(request));

    [Fact]
    public async Task RelayPreservesPipelinedBytesAndHalfCloseResponse()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        Task server = Task.Run(async () =>
        {
            using var peer = await upstream.AcceptTcpClientAsync(timeout.Token);
            using var peerStream = peer.GetStream();
            using var input = new MemoryStream();
            await peerStream.CopyToAsync(input, timeout.Token);
            Assert.Equal(new byte[] { 0, 1, 2, 255 }, input.ToArray());
            await peerStream.WriteAsync(new byte[] { 7, 8, 9 }, timeout.Token);
        });
        using var relay = new AntigravityRelayService(async (host, ct) =>
        {
            Assert.Equal(Host, host);
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync((IPEndPoint)upstream.LocalEndpoint, ct);
            return new NetworkStream(socket, ownsSocket: true);
        });
        string url = relay.Start();
        Assert.Equal(url, relay.Start());
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, new Uri(url).Port, timeout.Token);
        using var clientStream = client.GetStream();
        byte[] headers = Encoding.ASCII.GetBytes($"CONNECT {Host}:443 HTTP/1.1\r\nHost: {Host}:443\r\n\r\n");
        await clientStream.WriteAsync(headers.Concat(new byte[] { 0, 1, 2, 255 }).ToArray(), timeout.Token);
        Assert.Contains("200 Connection Established", await ReadHeaders(clientStream, timeout.Token));
        client.Client.Shutdown(SocketShutdown.Send);
        using var response = new MemoryStream();
        await clientStream.CopyToAsync(response, timeout.Token);
        Assert.Equal(new byte[] { 7, 8, 9 }, response.ToArray());
        await server;
        await relay.StopAsync();
        Assert.False(relay.IsRunning);
        Assert.Equal(0, relay.ActiveConnections);
        Assert.Null(relay.ProxyUrl);
    }

    [Fact]
    public async Task RelayReturns502RatherThanForwardingDirectWhenRouteFails()
    {
        int attempts = 0;
        using var relay = new AntigravityRelayService((host, ct) =>
        { attempts++; throw new IOException("private-password"); });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, new Uri(relay.Start()).Port, timeout.Token);
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {Host}:443 HTTP/1.1\r\n\r\n"), timeout.Token);
        string response = await ReadHeaders(client.GetStream(), timeout.Token);
        Assert.Contains("502", response);
        Assert.DoesNotContain("private-password", response);
        Assert.Equal(1, attempts);
        await relay.StopAsync();
    }

    [Fact]
    public async Task StoppingRelayCancelsClientsWaitingForHeadersAndReleasesListener()
    {
        using var relay = new AntigravityRelayService((host, ct) => throw new Exception("Must not connect"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        int port = new Uri(relay.Start()).Port;
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("CONNECT "), timeout.Token);
        while (relay.ActiveConnections == 0) await Task.Delay(10, timeout.Token);
        await relay.StopAsync().WaitAsync(timeout.Token);
        Assert.Equal(0, relay.ActiveConnections);
        Assert.Equal(0, await client.GetStream().ReadAsync(new byte[1], timeout.Token));
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start(); listener.Stop();
    }

    private static byte[] Answer(string host, byte[] ip, uint ttl = 60)
    {
        byte[] data = [.. AntigravitySmartDns.BuildQuery(host), 0xc0, 0x0c, 0, 1, 0, 1, 0, 0, 0, 0, 0, 4, .. ip];
        data[2] = 0x81; data[3] = 0x80; data[7] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(data.Length - 10, 4), ttl);
        return data;
    }

    private static async Task<string> ReadHeaders(Stream stream, CancellationToken ct)
    {
        var text = new StringBuilder(); byte[] b = new byte[1];
        while (text.Length < 8192 && !text.ToString().EndsWith("\r\n\r\n"))
        {
            if (await stream.ReadAsync(b, ct) == 0) break;
            text.Append((char)b[0]);
        }
        return text.ToString();
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(callback(request));
    }
}
