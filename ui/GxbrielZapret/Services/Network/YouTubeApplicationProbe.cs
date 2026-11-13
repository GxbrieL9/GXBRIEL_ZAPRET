using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace GxbrielZapret.Services.Network;

public sealed record YouTubeApplicationResult(bool Ok, string Stage, string Details);

public static class YouTubeApplicationProbe
{
    internal static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        UseProxy = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        // Every probe must establish transport under the current candidate, not reuse an older TLS connection.
        PooledConnectionLifetime = TimeSpan.Zero,
        ConnectCallback = ConnectIpv4FirstAsync
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// Prefer IPv4 addresses first so broken/blackholed IPv6 routes do not stall probes behind Happy Eyeballs
    /// and match the IPv4 capture path of winws2.
    /// </summary>
    internal static async ValueTask<System.IO.Stream> ConnectIpv4FirstAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        System.Net.IPAddress[] addresses;
        try
        {
            addresses = await System.Net.Dns.GetHostAddressesAsync(
                context.DnsEndPoint.Host, System.Net.Sockets.AddressFamily.InterNetwork, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            addresses = Array.Empty<System.Net.IPAddress>();
        }
        if (addresses.Length == 0)
        {
            try
            {
                addresses = await System.Net.Dns.GetHostAddressesAsync(
                    context.DnsEndPoint.Host, System.Net.Sockets.AddressFamily.Unspecified, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                addresses = Array.Empty<System.Net.IPAddress>();
            }
        }
        if (addresses.Length == 0)
            throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound);

        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new System.Net.Sockets.Socket(address.AddressFamily, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp)
            {
                NoDelay = true
            };
            try
            {
                await socket.ConnectAsync(new System.Net.IPEndPoint(address, context.DnsEndPoint.Port), ct).ConfigureAwait(false);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                socket.Dispose();
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw last ?? new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostUnreachable);
    }

    public static Task<YouTubeApplicationResult> CheckAsync(CancellationToken ct) => CheckAsync(Client, ct);

    internal static async Task<YouTubeApplicationResult> CheckAsync(HttpClient client, CancellationToken ct)
    {
        string stage = "HTML";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(18));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.youtube.com/?hl=en");
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
            using var page = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!page.IsSuccessStatusCode) return new(false, stage, $"YouTube HTML: HTTP {(int)page.StatusCode}. Видео не проверено.");
            string html = await ReadBoundedAsync(page.Content, 4 * 1024 * 1024, requireEnd: true, deadline.Token).ConfigureAwait(false);
            if (!html.Contains("ytInitialData", StringComparison.Ordinal) || !html.Contains("ytd-app", StringComparison.Ordinal))
                return new(false, stage, "YouTube: вместо приложения получена другая страница (согласие, ограничение или ошибка). Видео не проверено.");

            var script = FindApplicationScript(html);
            if (script is null) return new(false, stage, "YouTube: в HTML не найден JavaScript приложения. Видео не проверено.");
            stage = "JavaScript";
            using var scriptRequest = new HttpRequestMessage(HttpMethod.Get, script);
            scriptRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 65535);
            using var js = await client.SendAsync(scriptRequest, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!js.IsSuccessStatusCode) return new(false, stage, $"YouTube JavaScript: HTTP {(int)js.StatusCode}. Видео не проверено.");
            string sample = await ReadBoundedAsync(js.Content, 65536, requireEnd: false, deadline.Token).ConfigureAwait(false);
            if (sample.Length < 1024 || sample.TrimStart().StartsWith('<') ||
                !(sample.Contains("function", StringComparison.Ordinal) || sample.Contains("=>", StringComparison.Ordinal)))
                return new(false, stage, "YouTube: JavaScript приложения пуст или подменён страницей ошибки. Видео не проверено.");
            return new(true, stage, "HTML YouTube загружен целиком, получен фрагмент JavaScript приложения. Видео, HTTP/3 и настройки браузера не проверены.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new(false, stage, $"YouTube {stage}: тайм-аут. Видео не проверено."); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
        { return new(false, stage, $"YouTube {stage}: {ex.Message}. Видео не проверено."); }
    }

    internal static Uri? FindApplicationScript(string html)
    {
        foreach (Match match in Regex.Matches(html, "<script\\b[^>]*\\bsrc=[\"'](?<url>[^\"']+)[\"']", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
        {
            string value = WebUtility.HtmlDecode(match.Groups["url"].Value);
            if (Uri.TryCreate(new Uri("https://www.youtube.com/"), value, out var uri) &&
                uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals("www.youtube.com", StringComparison.OrdinalIgnoreCase) &&
                uri.AbsolutePath.StartsWith("/s/desktop/", StringComparison.Ordinal) && uri.AbsolutePath.EndsWith(".js", StringComparison.Ordinal)) return uri;
        }
        return null;
    }

    internal static async Task<string> ReadBoundedAsync(HttpContent content, int limit, bool requireEnd, CancellationToken ct)
    {
        using var input = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192];
        while (output.Length <= limit)
        {
            int remaining = limit - (int)output.Length;
            if (remaining == 0 && !requireEnd) break;
            int read = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, requireEnd ? remaining + 1 : remaining)), ct).ConfigureAwait(false);
            if (read == 0) break;
            output.Write(buffer, 0, read);
            if (output.Length > limit) throw new InvalidDataException("Ответ превысил безопасный размер проверки");
        }
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
    }
}
