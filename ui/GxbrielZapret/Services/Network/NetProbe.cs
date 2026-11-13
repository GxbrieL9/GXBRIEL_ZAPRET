using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using GxbrielZapret.Models;
using GxbrielZapret.Services.Strategies;

namespace GxbrielZapret.Services.Network;

/// <summary>Verdict of a DPI-signature probe: is the provider actively interfering by SNI, or not?</summary>
public enum DpiVerdict
{
    Clean,          // TLS ClientHello with the real SNI completed — no DPI drop on this host
    Reset,          // connection reset (RST) mid-handshake — classic DPI reset injection
    Freeze,         // ClientHello sent, no answer — the SNI-bearing packet is being dropped/frozen
    NoConnection,   // TCP never connected — a routing/IP issue, not name-based DPI
}

/// <summary>Verdict of the volume/packet-limit probe — the "TCP 16-20" censorship method. The
/// provider caps a connection by packet count (~16–20 packets each direction ≈ a few tens of KB):
/// it opens fine, then the byte stream freezes. Complements <see cref="DpiVerdict"/>, which never
/// sees this because a short TLS handshake stays under the cap.</summary>
public enum VolumeVerdict
{
    Ok,           // streamed far past the cap — no volume/packet limiting on the test channel
    Throttled,    // the transfer froze right after a few tens of KB — the classic TCP 16-20 cap
    Unreachable,  // couldn't run the transfer at all (route/TLS) — inconclusive for this method
}

/// <summary>
/// Low-level connectivity probes shared by the diagnostics matrix and the
/// auto-selector. Each probe is a single real attempt against a host: a browser-like
/// HTTP/2 reachability check on 443, a TLS handshake (forced version) on 443, or a latency
/// ping. They return <see cref="DiagStatus"/> so callers can both display and score.
/// </summary>
public static class NetProbe
{
    public static async Task<(bool ok, long ms)> PingAsync(string host, CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, TimeSpan.FromSeconds(2), cancellationToken: ct);
            if (reply.Status == IPStatus.Success) return (true, reply.RoundtripTime);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* ICMP often filtered for CDNs — fall back to TCP connect latency */ }

        var sw = Stopwatch.StartNew();
        try
        {
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            await tcp.ConnectAsync(host, 443, cts.Token);
            sw.Stop();
            return (true, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return (false, 0); }
    }

    /// <summary>
    /// Plain TCP-connect reachability to host:port. Used for protocols without TLS/SNI
    /// (e.g. a Telegram MTProto data-center IP) — it answers "is the endpoint reachable",
    /// NOT "is it throttled" (throttling still lets the connect succeed).
    /// </summary>
    public static async Task<DiagStatus> TcpAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            await tcp.ConnectAsync(host, port, cts.Token);
            return DiagStatus.Ok;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return DiagStatus.Timeout; }
        catch { return DiagStatus.Fail; }
    }

    /// <summary>
    /// RFC 5389 STUN Binding Request probe over UDP.
    /// Used for WebRTC / Discord Voice (RTC) UDP reachability (ports 50000-65535, 19294-19344, 3478).
    /// Returns DiagStatus.Ok when a remote server returns a STUN response (e.g. 0x0101 Binding Success)
    /// or any UDP packet back from the remote endpoint, proving the path is not dropped by DPI.
    /// </summary>
    public static async Task<DiagStatus> UdpStunProbeAsync(string host, int port, CancellationToken ct, int timeoutSec = 3)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct).ConfigureAwait(false);
            if (addresses.Length == 0) return DiagStatus.Fail;

            using var udp = new UdpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

            var txId = new byte[12];
            Random.Shared.NextBytes(txId);

            // RFC 5389 STUN Binding Request:
            // 2 bytes Type (0x0001: Binding Request)
            // 2 bytes Length (0x0000)
            // 4 bytes Magic Cookie (0x2112A442)
            // 12 bytes Transaction ID
            var req = new byte[20];
            req[0] = 0x00;
            req[1] = 0x01;
            req[2] = 0x00;
            req[3] = 0x00;
            req[4] = 0x21;
            req[5] = 0x12;
            req[6] = 0xA4;
            req[7] = 0x42;
            Buffer.BlockCopy(txId, 0, req, 8, 12);

            var endpoint = new IPEndPoint(addresses[0], port);
            await udp.SendAsync(req, req.Length, endpoint).WaitAsync(cts.Token).ConfigureAwait(false);

            var recvResult = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
            var buf = recvResult.Buffer;

            // If remote server answered with valid STUN or any voice UDP packet:
            if (buf.Length >= 20
                && buf[4] == 0x21 && buf[5] == 0x12 && buf[6] == 0xA4 && buf[7] == 0x42
                && buf.AsSpan(8, 12).SequenceEqual(txId))
            {
                return DiagStatus.Ok;
            }

            if (buf.Length > 0)
            {
                return DiagStatus.Ok;
            }

            return DiagStatus.Fail;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return DiagStatus.Timeout; }
        catch (SocketException se) when (se.SocketErrorCode == SocketError.TimedOut) { return DiagStatus.Timeout; }
        catch { return DiagStatus.Fail; }
    }

    private const string BrowserUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/126.0.0.0 Safari/537.36";

    internal static string HostToUrl(string host) => host.Trim().ToLowerInvariant() switch
    {
        "discord.com" => "https://discord.com/api/v10/gateway",
        "gateway.discord.gg" => "https://gateway.discord.gg/?v=10&encoding=json",
        "redirector.googlevideo.com" => "https://redirector.googlevideo.com/report_mapping",
        _ => host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || host.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? host
            : $"https://{host}/"
    };

    /// <summary>
    /// Runs an in-process HTTPS reachability probe. The shape intentionally mirrors the old curl-based
    /// API so diagnostics, watchdog and strategy tests keep the same scoring semantics: ok means the
    /// endpoint returned any HTTP response code instead of timing out or failing before HTTP.
    /// </summary>
    public static async Task<(bool ok, string httpCode, int exitCode)> RunCurlAsync(
        string url,
        IEnumerable<string>? additionalArgs,
        CancellationToken ct,
        int connectTimeoutSec = 5)
    {
        var tls = RequestedTlsProtocol(additionalArgs);
        var timeout = TimeSpan.FromSeconds(connectTimeoutSec + 2);
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(connectTimeoutSec),
            ConnectCallback = YouTubeApplicationProbe.ConnectIpv4FirstAsync
        };

        if (tls != SslProtocols.None)
        {
            handler.SslOptions.EnabledSslProtocols = tls;
        }

        try
        {
            using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", BrowserUa);

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            // Headers alone can succeed while the transfer stalls. Read a bounded sample
            // under the same deadline; a short response must reach EOF successfully.
            using var body = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            var buffer = new byte[8192];
            int remaining = 64 * 1024;
            while (remaining > 0)
            {
                int read = await body.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cts.Token)
                    .ConfigureAwait(false);
                if (read == 0) break;
                remaining -= read;
            }
            return (true, ((int)response.StatusCode).ToString("000"), 0);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (false, "000", 28);
        }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException)
        {
            return (false, "000", 35);
        }
        catch (AuthenticationException)
        {
            return (false, "000", 35);
        }
        catch
        {
            return (false, "000", -1);
        }
    }

    private static SslProtocols RequestedTlsProtocol(IEnumerable<string>? additionalArgs)
    {
        if (additionalArgs is null) return SslProtocols.None;

        foreach (var arg in additionalArgs)
        {
            if (string.Equals(arg, "--tlsv1.2", StringComparison.OrdinalIgnoreCase))
                return SslProtocols.Tls12;
            if (string.Equals(arg, "--tlsv1.3", StringComparison.OrdinalIgnoreCase))
                return SslProtocols.Tls13;
        }

        return SslProtocols.None;
    }

    /// <summary>
    /// Reachability check on 443 (or UDP STUN for discord.media). Evaluates to DiagStatus.Ok if the endpoint returns any HTTP status code or UDP STUN response.
    /// </summary>
    public static async Task<DiagStatus> ReachAsync(string host, CancellationToken ct)
    {
        string trimmed = host.Trim();
        if (trimmed.Equals("discord.media", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("udp://", StringComparison.OrdinalIgnoreCase))
        {
            string udpHost = trimmed.StartsWith("udp://", StringComparison.OrdinalIgnoreCase)
                ? trimmed.Substring("udp://".Length)
                : trimmed;
            int port = 50001;
            int colonIdx = udpHost.IndexOf(':');
            if (colonIdx > 0 && int.TryParse(udpHost.Substring(colonIdx + 1), out int parsedPort))
            {
                udpHost = udpHost.Substring(0, colonIdx);
                port = parsedPort;
            }
            return await UdpStunProbeAsync(udpHost, port, ct).ConfigureAwait(false);
        }

        string url = HostToUrl(host);
        var (ok, _, exitCode) = await RunCurlAsync(url, null, ct, 5).ConfigureAwait(false);
        if (ok) return DiagStatus.Ok;
        if (exitCode == 28 || exitCode == 124) return DiagStatus.Timeout;
        return DiagStatus.Fail;
    }

    /// <summary>Full per-host probe used by the auto-selector:
    /// TLS 1.2 + TLS 1.3 handshakes and a full reachability check.</summary>
    public static async Task<AutoHostResult> ProbeHostAsync(string host, CancellationToken ct)
    {
        string trimmed = host.Trim();
        if (trimmed.Equals("discord.media", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("udp://", StringComparison.OrdinalIgnoreCase))
        {
            var udpStatus = await ReachAsync(host, ct).ConfigureAwait(false);
            return new AutoHostResult(host, DiagStatus.Skip, DiagStatus.Skip, udpStatus, udpStatus);
        }

        var p12 = TlsAsync(host, SslProtocols.Tls12, ct);
        var p13 = TlsAsync(host, SslProtocols.Tls13, ct);
        var pr = ReachAsync(host, ct);
        await Task.WhenAll(p12, p13, pr).ConfigureAwait(false);
        return new AutoHostResult(host, p12.Result, p13.Result, pr.Result);
    }

    public static async Task<DiagStatus> TlsAsync(string host, SslProtocols proto, CancellationToken ct)
    {
        string trimmed = host.Trim();
        if (trimmed.Equals("discord.media", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("udp://", StringComparison.OrdinalIgnoreCase))
        {
            return DiagStatus.Skip;
        }

        string url = HostToUrl(host);
        var extra = proto switch
        {
            SslProtocols.Tls12 => new[] { "--tlsv1.2", "--tls-max", "1.2" },
            SslProtocols.Tls13 => new[] { "--tlsv1.3", "--tls-max", "1.3" },
            _ => Array.Empty<string>()
        };

        var (ok, _, exitCode) = await RunCurlAsync(url, extra, ct, 5).ConfigureAwait(false);
        if (ok) return DiagStatus.Ok;
        if (exitCode == 28 || exitCode == 124) return DiagStatus.Timeout;
        // A TLS failure can be a reset or certificate error; it does not prove lack of protocol support.
        if (exitCode == 35) return DiagStatus.Fail;
        return DiagStatus.Fail;
    }


    /// <summary>
    /// Builds a byte-exact RFC 8446 / RFC 5246 TLS 1.3/1.2 ClientHello matching a modern Chromium/Chrome browser.
    /// Includes modern ALPN (h2, http/1.1), supported groups (X25519), and the specified SNI host.
    /// This eliminates the discrepancy between .NET SslStream and actual browser ClientHello byte offsets,
    /// ensuring winws desync rules (e.g. split-pos, seqovl) are validated against authentic browser traffic.
    /// </summary>
    public static byte[] BuildChromeTlsClientHello(string sni)
    {
        var sniBytes = System.Text.Encoding.ASCII.GetBytes(sni.Trim());

        using var extMs = new MemoryStream();
        using var extW = new BinaryWriter(extMs);

        // 1. Extension: server_name (0x0000)
        extW.Write((byte)0x00); extW.Write((byte)0x00);
        int sniListLen = sniBytes.Length + 3;
        int sniExtLen = sniListLen + 2;
        extW.Write((byte)(sniExtLen >> 8)); extW.Write((byte)(sniExtLen & 0xFF));
        extW.Write((byte)(sniListLen >> 8)); extW.Write((byte)(sniListLen & 0xFF));
        extW.Write((byte)0x00); // host_name type
        extW.Write((byte)(sniBytes.Length >> 8)); extW.Write((byte)(sniBytes.Length & 0xFF));
        extW.Write(sniBytes);

        // 2. Extension: extended_master_secret (0x0017)
        extW.Write(new byte[] { 0x00, 0x17, 0x00, 0x00 });

        // 3. Extension: renegotiation_info (0xFF01)
        extW.Write(new byte[] { 0xFF, 0x01, 0x00, 0x01, 0x00 });

        // 4. Extension: supported_groups (0x000A) - X25519 (0x001D), secp256r1 (0x0017), secp384r1 (0x0018)
        extW.Write(new byte[] { 0x00, 0x0A, 0x00, 0x08, 0x00, 0x06, 0x00, 0x1D, 0x00, 0x17, 0x00, 0x18 });

        // 5. Extension: ec_point_formats (0x000B)
        extW.Write(new byte[] { 0x00, 0x0B, 0x00, 0x02, 0x01, 0x00 });

        // 6. Extension: session_ticket (0x0023)
        extW.Write(new byte[] { 0x00, 0x23, 0x00, 0x00 });

        // 7. Extension: alpn (0x0010) - h2, http/1.1
        extW.Write(new byte[] {
            0x00, 0x10, 0x00, 0x0E, 0x00, 0x0C,
            0x02, 0x68, 0x32,
            0x08, 0x68, 0x74, 0x74, 0x70, 0x2F, 0x31, 0x2E, 0x31
        });

        // 8. Extension: status_request (0x0005)
        extW.Write(new byte[] { 0x00, 0x05, 0x00, 0x05, 0x01, 0x00, 0x00, 0x00, 0x00 });

        // 9. Extension: signature_algorithms (0x000D)
        extW.Write(new byte[] {
            0x00, 0x0D, 0x00, 0x12, 0x00, 0x10,
            0x04, 0x03, 0x08, 0x04, 0x04, 0x01, 0x05, 0x03, 0x08, 0x05, 0x05, 0x01, 0x08, 0x06, 0x06, 0x01
        });

        // 10. Extension: supported_versions (0x002B) - TLS 1.3 (0x0304), TLS 1.2 (0x0303)
        extW.Write(new byte[] { 0x00, 0x2B, 0x00, 0x05, 0x04, 0x03, 0x04, 0x03, 0x03 });

        // 11. Extension: psk_key_exchange_modes (0x002D)
        extW.Write(new byte[] { 0x00, 0x2D, 0x00, 0x02, 0x01, 0x01 });

        // 12. Extension: key_share (0x0033) - X25519 (0x001D)
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        extW.Write(new byte[] { 0x00, 0x33, 0x00, 0x26, 0x00, 0x24, 0x00, 0x1D, 0x00, 0x20 });
        extW.Write(key);

        var extensions = extMs.ToArray();

        // Assemble Handshake Body
        using var hsMs = new MemoryStream();
        using var hsW = new BinaryWriter(hsMs);

        hsW.Write((byte)0x03); hsW.Write((byte)0x03); // client_version (TLS 1.2 legacy)
        var random = new byte[32];
        Random.Shared.NextBytes(random);
        hsW.Write(random); // 32 random bytes

        // session_id (32 random bytes)
        var sessionId = new byte[32];
        Random.Shared.NextBytes(sessionId);
        hsW.Write((byte)32);
        hsW.Write(sessionId);

        // cipher_suites (modern Chrome cipher suite, 15 suites = 30 bytes)
        var ciphers = new byte[] {
            0x13, 0x01, 0x13, 0x02, 0x13, 0x03,
            0xC0, 0x2B, 0xC0, 0x2F, 0xC0, 0x2C, 0xC0, 0x30,
            0xCC, 0xA9, 0xCC, 0xA8,
            0xC0, 0x13, 0xC0, 0x14,
            0x00, 0x9C, 0x00, 0x9D, 0x00, 0x2F, 0x00, 0x35
        };
        hsW.Write((byte)(ciphers.Length >> 8)); hsW.Write((byte)(ciphers.Length & 0xFF));
        hsW.Write(ciphers);

        // compression_methods (null)
        hsW.Write((byte)1); hsW.Write((byte)0);

        // extensions
        hsW.Write((byte)(extensions.Length >> 8)); hsW.Write((byte)(extensions.Length & 0xFF));
        hsW.Write(extensions);

        var hsBody = hsMs.ToArray();

        // Wrap into TLS Record (Type 0x16 Handshake, Version 0x0301)
        using var recMs = new MemoryStream();
        using var recW = new BinaryWriter(recMs);

        recW.Write((byte)0x16);
        recW.Write((byte)0x03);
        recW.Write((byte)0x01);

        int hsLen = hsBody.Length;
        int recLen = hsLen + 4;
        recW.Write((byte)(recLen >> 8));
        recW.Write((byte)(recLen & 0xFF));

        // Handshake Header (Type 0x01, 3 bytes length)
        recW.Write((byte)0x01);
        recW.Write((byte)((hsLen >> 16) & 0xFF));
        recW.Write((byte)((hsLen >> 8) & 0xFF));
        recW.Write((byte)(hsLen & 0xFF));
        recW.Write(hsBody);

        return recMs.ToArray();
    }

    /// <summary>DPI-signature probe: does the provider actively block this host by SNI? First a plain
    /// TCP-connect on 443 (if that already fails it's routing/IP, not name-DPI); then a TLS ClientHello
    /// with the REAL SNI. Because the server already completed the TCP handshake, a reset (RST) or a
    /// silent drop on the ClientHello is a middlebox injecting — the classic Russian DPI signature.</summary>
    public static async Task<DpiVerdict> DpiProbeAsync(string host, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await tcp.ConnectAsync(host, 443, cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return DpiVerdict.NoConnection; } // TCP never connected → routing/IP, not SNI-DPI

        // 1. Try real Chrome-style TLS ClientHello over raw socket first (exact packet layout parity with browsers)
        try
        {
            var stream = tcp.GetStream();
            var chBytes = BuildChromeTlsClientHello(host);
            using var hs = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hs.CancelAfter(TimeSpan.FromSeconds(4));

            await stream.WriteAsync(chBytes.AsMemory(), hs.Token).ConfigureAwait(false);

            var respBuf = new byte[5];
            int read = await stream.ReadAsync(respBuf.AsMemory(0, 5), hs.Token).ConfigureAwait(false);
            if (read >= 5 && respBuf[0] == 0x16 && respBuf[1] == 0x03)
            {
                return DpiVerdict.Clean; // Server answered with TLS ServerHello!
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return DpiVerdict.Freeze; }
        catch (IOException ioe) when (IsConnectionReset(ioe)) { return DpiVerdict.Reset; }
        catch (SocketException se) when (se.SocketErrorCode == SocketError.ConnectionReset) { return DpiVerdict.Reset; }
        catch
        {
            // Fall back to SslStream if raw socket probe was inconclusive
        }

        // 2. Fallback to SslStream probe if raw probe did not complete
        try
        {
            using var tcpFallback = new TcpClient();
            using var ctsFallback = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ctsFallback.CancelAfter(TimeSpan.FromSeconds(3));
            await tcpFallback.ConnectAsync(host, 443, ctsFallback.Token);

            using var ssl = new SslStream(tcpFallback.GetStream(), false, (_, _, _, _) => true);
            var opts = new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ApplicationProtocols = new List<SslApplicationProtocol>
                {
                    SslApplicationProtocol.Http2,
                    SslApplicationProtocol.Http11,
                },
            };
            using var hs = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hs.CancelAfter(TimeSpan.FromSeconds(4));
            await ssl.AuthenticateAsClientAsync(opts, hs.Token);
            return DpiVerdict.Clean;                                     // handshake completed → no DPI drop
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return DpiVerdict.Freeze; }  // ClientHello sent, no answer → dropped
        catch (AuthenticationException) { return DpiVerdict.Clean; }      // server answered at TLS level → reached
        catch (IOException ioe) when (IsConnectionReset(ioe)) { return DpiVerdict.Reset; }
        catch (SocketException se) when (se.SocketErrorCode == SocketError.ConnectionReset) { return DpiVerdict.Reset; }
        catch { return DpiVerdict.Reset; }                               // abrupt failure after a good TCP connect → DPI
    }

    private static bool IsConnectionReset(IOException ioe) =>
        ioe.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset }
        || ioe.Message.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase)
        || ioe.Message.Contains("reset", StringComparison.OrdinalIgnoreCase);

    // Purpose-built large-download sink: Cloudflare's speedtest streams exactly ?bytes=N of payload,
    // accepts arbitrary volume without rate-limiting, and rides a commonly-throttled CDN path.
    private const string VolumeDownUrl = "https://speed.cloudflare.com/__down?bytes=";

    /// <summary>
    /// "TCP 16-20" volume/packet-limit probe. The censor caps a connection by packet count (~16–20
    /// packets each direction ≈ a few tens of KB): the connection establishes, then the byte stream
    /// freezes. We pull a payload far larger than that cap and watch whether it races through or
    /// stalls just past the threshold. Download mirrors the user-visible symptom (video buffering,
    /// media not loading); outbound is limited the same way. No admin needed; independent of the SNI
    /// probe. Adapted from the method in hyperion-cs/dpi-checkers (Apache-2.0) — but streamed, so we
    /// can pinpoint the stall instead of relying on a browser's all-or-nothing POST timeout.
    /// </summary>
    public static async Task<VolumeVerdict> VolumeProbeAsync(CancellationToken ct)
    {
        const long wantBytes  = 1_500_000;   // ask for ~1.5 MB — far above any packet cap
        const int  passBytes  = 256 * 1024;  // streamed this much cleanly → past the cap → Ok
        const int  stallFloor =  80 * 1024;  // froze below this → the cap bit → Throttled

        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,   // count real wire bytes
            ConnectTimeout = TimeSpan.FromSeconds(6),
        };
        try
        {
            using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));            // whole-probe ceiling

            using var req = new HttpRequestMessage(HttpMethod.Get, VolumeDownUrl + wantBytes);
            req.Headers.TryAddWithoutValidation("User-Agent", BrowserUa);

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            resp.EnsureSuccessStatusCode();

            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            var buf = new byte[32 * 1024];
            long total = 0;
            while (total < passBytes)
            {
                // Per-read stall timeout: bytes flowing then stopping dead for 5 s is the throttle
                // fingerprint — a merely slow line keeps trickling; the cap freezes it outright.
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                readCts.CancelAfter(TimeSpan.FromSeconds(5));
                int n;
                try { n = await stream.ReadAsync(buf, readCts.Token); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (OperationCanceledException) // the read stalled (or the whole-probe ceiling hit)
                {
                    return total >= stallFloor ? VolumeVerdict.Ok : VolumeVerdict.Throttled;
                }
                if (n == 0) break;               // clean EOF
                total += n;
            }
            return total >= stallFloor ? VolumeVerdict.Ok : VolumeVerdict.Throttled;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return VolumeVerdict.Unreachable; }   // couldn't even start the transfer → inconclusive
    }
}
