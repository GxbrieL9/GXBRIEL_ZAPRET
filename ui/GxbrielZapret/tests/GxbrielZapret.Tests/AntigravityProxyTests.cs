using System.Net;
using System.Net.Sockets;
using System.Text;
using GxbrielZapret.Services.Platform;
using Xunit;

namespace GxbrielZapret.Tests;

public sealed class AntigravityProxyTests
{
    [Fact]
    public void SavedEnvironmentOverridesStaleProcessAndFallsBackWhenAbsent()
    {
        Assert.Equal("http://user:80", AntigravityProxy.Resolve("http://stale:80", "http://user:80", "http://machine:80"));
        Assert.Equal("http://machine:80", AntigravityProxy.Resolve("http://stale:80", " ", "http://machine:80"));
        Assert.Equal("http://process:80", AntigravityProxy.Resolve(" http://process:80 ", null, null));
        Assert.Null(AntigravityProxy.Resolve(null, "", " "));
    }

    [Theory]
    [InlineData("socks4://localhost:1080")]
    [InlineData("http://localhost:0")]
    [InlineData("http://localhost:80/path")]
    [InlineData("http://localhost:80?token=secret")]
    [InlineData("http://localhost:80/#fragment")]
    [InlineData("http://localhost:80\n")]
    public void InvalidProxyFailsWithoutDisclosingInput(string value)
    {
        var error = Assert.Throws<ArgumentException>(() => AntigravityProxy.Parse(value));
        Assert.DoesNotContain(value, error.Message);
    }

    [Theory]
    [InlineData("http://127.0.0.1:7890")]
    [InlineData("https://user:p%40ss@proxy.example:8443")]
    [InlineData("http://[::1]:8080")]
    [InlineData("socks5://localhost:1080")]
    public void HttpProxyAndEncodedCredentialsAreAccepted(string value) => Assert.NotNull(AntigravityProxy.Parse(value));

    [Fact]
    public async Task CheckUsesConnectProxyAndReportsAuthenticationFailureWithoutSecrets()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = client.GetStream();
            byte[] buffer = new byte[8192];
            string request = "";
            while (!request.Contains("\r\n\r\n"))
            {
                int read = await stream.ReadAsync(buffer, timeout.Token);
                if (read == 0) break;
                request += Encoding.ASCII.GetString(buffer, 0, read);
            }
            Assert.StartsWith("CONNECT cloudaicompanion-pa.googleapis.com:443 HTTP/1.1", request);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 407 Proxy Authentication Required\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), timeout.Token);
        });
        string result = await AntigravityProxy.CheckAsync($"http://alice:private-password@127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}", timeout.Token);
        await server;
        Assert.Contains("Не удалось", result);
        Assert.DoesNotContain("private-password", result);
        Assert.DoesNotContain("alice", result);
    }
}
