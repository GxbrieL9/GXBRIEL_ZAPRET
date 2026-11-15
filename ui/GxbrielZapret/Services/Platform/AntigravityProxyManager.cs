using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace GxbrielZapret.Services.Platform;

public sealed record LocalProxyCandidate(string Name, string Url);

public static class AntigravityProxyManager
{
    private static readonly (string Name, string Host, int Port, string Scheme)[] KnownProxies =
    [
        ("Xray / v2rayN (HTTP)", "127.0.0.1", 10809, "http"),
        ("Xray / v2rayN (SOCKS5)", "127.0.0.1", 10808, "socks5"),
        ("Clash / Mihomo (HTTP)", "127.0.0.1", 7890, "http"),
        ("Clash Verge Rev (HTTP)", "127.0.0.1", 7897, "http"),
        ("NekoBox / NekoRay (HTTP)", "127.0.0.1", 2080, "http"),
        ("Shadowsocks (SOCKS5)", "127.0.0.1", 1080, "socks5"),
        ("Локальный HTTP прокси", "127.0.0.1", 8080, "http")
    ];

    public static IReadOnlyList<LocalProxyCandidate> DetectLocalProxies()
    {
        var found = new List<LocalProxyCandidate>();
        foreach (var c in KnownProxies)
        {
            try
            {
                using var client = new TcpClient();
                var asyncResult = client.BeginConnect(c.Host, c.Port, null, null);
                bool connected = asyncResult.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(70));
                if (connected && client.Connected)
                {
                    found.Add(new(c.Name, $"{c.Scheme}://{c.Host}:{c.Port}"));
                }
            }
            catch { }
        }
        return found;
    }

    public static async Task<(bool Success, string Message)> TestProxyAsync(string proxyUrl, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(proxyUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "socks5"))
            return (false, "Некорректный протокол прокси. Поддерживаются http://, https:// и socks5://");
        try
        {
            string message = await AntigravityProxy.CheckAsync(proxyUrl, ct);
            return (message.StartsWith("HTTPS через прокси:", StringComparison.Ordinal), message);
        }
        catch (ArgumentException ex) { return (false, ex.Message); }
    }

    public static void SetUserEnvironmentProxy(string? proxyUrl)
    {
        Environment.SetEnvironmentVariable("AG_LS_PROXY", string.IsNullOrWhiteSpace(proxyUrl) ? null : proxyUrl.Trim(), EnvironmentVariableTarget.User);
    }

    public static string? GetUserEnvironmentProxy()
    {
        return Environment.GetEnvironmentVariable("AG_LS_PROXY", EnvironmentVariableTarget.User);
    }
}
