using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace GxbrielZapret.Services.Platform;

// A loopback-only CONNECT relay. It transports encrypted bytes; it never handles Google credentials.
internal sealed class AntigravityRelayService : IDisposable
{
    private readonly AntigravitySmartDns _routes = new();
    private readonly Func<string, CancellationToken, Task<Stream>> _connect;
    private readonly ConcurrentDictionary<long, Task> _clients = new();
    private readonly SemaphoreSlim _slots = new(64);
    private CancellationTokenSource? _stop;
    private TcpListener? _listener;
    private Task? _accept;
    private long _clientId;
    private readonly object _routeLock = new();
    private CancellationTokenSource _routeChanged = new();
    internal string? ProxyUrl { get; private set; }
    internal bool IsRunning => _listener is not null;
    internal string Status => _routes.LastRoute;
    internal int ActiveConnections => _clients.Count;

    internal AntigravityRelayService(Func<string, CancellationToken, Task<Stream>>? connect = null) =>
        _connect = connect ?? _routes.ConnectAsync;

    internal string Start()
    {
        if (ProxyUrl is not null) return ProxyUrl;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(64);
        _stop = new();
        _listener = listener;
        ProxyUrl = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        _accept = AcceptAsync(listener, _stop.Token);
        return ProxyUrl;
    }

    private async Task AcceptAsync(TcpListener listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) { client.Dispose(); break; }
                if (!_slots.Wait(0)) { client.Dispose(); continue; }
                long id = Interlocked.Increment(ref _clientId);
                // Start after registration so even an immediately failed request is tracked correctly.
                var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Task task = ServeTrackedAsync(client, gate.Task, ct);
                _clients[id] = task;
                _ = task.ContinueWith(completed => _clients.TryRemove(id, out _), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                gate.SetResult();
            }
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task ServeTrackedAsync(TcpClient client, Task gate, CancellationToken ct)
    {
        await gate.ConfigureAwait(false);
        using (client)
        {
            client.NoDelay = true;
            using var stream = client.GetStream();
            bool established = false;
            try
            {
                using var setup = CancellationTokenSource.CreateLinkedTokenSource(ct);
                setup.CancelAfter(TimeSpan.FromSeconds(25));
                byte[] buffer = new byte[8192];
                int count = 0, end = -1;
                using (var headers = CancellationTokenSource.CreateLinkedTokenSource(setup.Token))
                {
                    headers.CancelAfter(TimeSpan.FromSeconds(5));
                    while (end < 0 && count < buffer.Length)
                    {
                        int read = await stream.ReadAsync(buffer.AsMemory(count), headers.Token).ConfigureAwait(false);
                        if (read == 0) return;
                        count += read;
                        end = HeaderEnd(buffer, count);
                    }
                }
                string? host = end < 0 ? null : ParseTarget(Encoding.ASCII.GetString(buffer, 0, end));
                if (host is null)
                {
                    await ReplyAsync(stream, "403 Forbidden", setup.Token).ConfigureAwait(false);
                    return;
                }
                CancellationTokenSource connection;
                lock (_routeLock) connection = CancellationTokenSource.CreateLinkedTokenSource(ct,
                    AntigravitySmartDns.IsRouted(host) ? _routeChanged.Token : CancellationToken.None);
                using var lifetime = connection;
                using var open = CancellationTokenSource.CreateLinkedTokenSource(setup.Token, connection.Token);
                using var upstream = await _connect(host, open.Token).ConfigureAwait(false);
                await ReplyAsync(stream, "200 Connection Established", setup.Token).ConfigureAwait(false);
                established = true;
                if (count > end) await upstream.WriteAsync(buffer.AsMemory(end, count - end), connection.Token).ConfigureAwait(false);
                using var pump = CancellationTokenSource.CreateLinkedTokenSource(connection.Token);
                Task upload = stream.CopyToAsync(upstream, pump.Token);
                Task download = upstream.CopyToAsync(stream, pump.Token);
                Task first = await Task.WhenAny(upload, download).ConfigureAwait(false);
                if (first == upload && upload.IsCompletedSuccessfully && upstream is NetworkStream network)
                {
                    try { network.Socket.Shutdown(SocketShutdown.Send); } catch (SocketException) { }
                    await download.ConfigureAwait(false);
                }
                else pump.Cancel();
                try { await Task.WhenAll(upload, download).ConfigureAwait(false); }
                catch (OperationCanceledException) when (pump.IsCancellationRequested) { }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                if (!established && !ct.IsCancellationRequested)
                {
                    try
                    {
                        using var reply = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                        await ReplyAsync(stream, "502 Bad Gateway", reply.Token).ConfigureAwait(false);
                    }
                    catch { }
                }
            }
            finally { _slots.Release(); }
        }
    }

    private static int HeaderEnd(byte[] buffer, int count)
    {
        for (int i = 3; i < count; i++)
            if (buffer[i - 3] == 13 && buffer[i - 2] == 10 && buffer[i - 1] == 13 && buffer[i] == 10) return i + 1;
        return -1;
    }

    internal static string? ParseTarget(string headers)
    {
        string[] parts = headers.Split("\r\n", 2)[0].Split(' ');
        if (parts.Length != 3 || parts[0] != "CONNECT" || parts[2] is not ("HTTP/1.1" or "HTTP/1.0")) return null;
        string target = parts[1];
        if (!target.EndsWith(":443", StringComparison.Ordinal)) return null;
        string host = target[..^4].ToLowerInvariant();
        if (host.Length is < 3 or > 253 || !host.Contains('.') || IPAddress.TryParse(host, out _) ||
            host.Split('.').Any(label => label.Length is < 1 or > 63 || label[0] == '-' || label[^1] == '-' ||
                label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))) return null;
        return host;
    }

    private static Task ReplyAsync(Stream stream, string status, CancellationToken ct) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\n\r\n"), ct).AsTask();

    internal void NextRoute()
    {
        _routes.NextRoute();
        lock (_routeLock)
        {
            var previous = _routeChanged;
            _routeChanged = new();
            previous.Cancel(); previous.Dispose();
        }
    }

    internal async Task<string> CheckAsync(CancellationToken ct)
    {
        string url = Start();
        using var handler = new HttpClientHandler { Proxy = new WebProxy(url), UseProxy = true, AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(28) };
        var results = await Task.WhenAll(new[] { "cloudcode-pa.googleapis.com", "daily-cloudcode-pa.googleapis.com", "cloudaicompanion-pa.googleapis.com" }
            .Select(async host =>
            {
                try
                {
                    using var response = await http.GetAsync("https://" + host + "/", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    return $"{host}: HTTPS, HTTP {(int)response.StatusCode}";
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
                { ct.ThrowIfCancellationRequested(); return host + ": маршрут недоступен"; }
            })).ConfigureAwait(false);
        return string.Join("\n", results) + "\nПроверка выполнена без аккаунта. Доступ к модели подтверждается её ответом в Antigravity.";
    }

    internal async Task StopAsync()
    {
        _stop?.Cancel();
        _listener?.Stop();
        _listener = null;
        ProxyUrl = null;
        if (_accept is not null) await _accept.ConfigureAwait(false);
        await Task.WhenAll(_clients.Values).ConfigureAwait(false);
        _stop?.Dispose(); _stop = null; _accept = null;
    }

    public void Dispose()
    {
        _stop?.Cancel();
        _listener?.Stop();
        _listener = null; ProxyUrl = null;
        _routes.Dispose();
        // Active pumps own their cancellation-aware sockets and release them on cancellation.
    }
}
