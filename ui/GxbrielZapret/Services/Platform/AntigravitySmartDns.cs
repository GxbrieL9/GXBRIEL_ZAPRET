using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace GxbrielZapret.Services.Platform;

// Original implementation. Public provider templates documented at dns-ai.ru and magicdns.ru.
// No copied unlocker code, private group keys, certificate interception or system DNS changes.
internal sealed class AntigravitySmartDns : IDisposable
{
    internal static readonly string[] RoutedHosts =
    [
        "cloudcode-pa.googleapis.com", "daily-cloudcode-pa.googleapis.com",
        "cloudaicompanion-pa.googleapis.com", "cloudaicompanion.googleapis.com", "aicode.googleapis.com"
    ];
    internal static readonly (string Name, string Url)[] Providers =
    [
        ("DNS-AI", "https://dns.dns-ai.ru/dns-query"),
        ("MagicDNS", "https://api.magicinfo.art/rpc"),
        ("DNS-AI · запасной вход", "https://cf.dns-ai.ru/dns-query")
    ];
    private readonly HttpClient _http;
    internal AntigravitySmartDns(HttpClient? http = null) => _http = http ?? new HttpClient(new SocketsHttpHandler
    {
        UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(3), MaxConnectionsPerServer = 4
    }) { Timeout = TimeSpan.FromSeconds(5) };
    private readonly ConcurrentDictionary<string, (IPAddress[] Addresses, long Expires)> _cache = new();
    private readonly ConcurrentDictionary<string, long> _verified = new();
    private int _firstProvider;
    internal string LastRoute { get; private set; } = "Ожидание запроса Antigravity. Ответ модели не проверен.";
    internal static bool IsRouted(string host) => RoutedHosts.Contains(host, StringComparer.OrdinalIgnoreCase);
    internal void NextRoute()
    {
        Interlocked.Increment(ref _firstProvider);
        LastRoute = "Выбран следующий маршрут. Проверьте новое сообщение в Antigravity.";
    }

    internal async Task<Stream> ConnectAsync(string host, CancellationToken ct)
    {
        if (!IsRouted(host))
            return await ConnectPublicAsync(await Dns.GetHostAddressesAsync(host, ct), ct).ConfigureAwait(false);

        int first = (int)((uint)Volatile.Read(ref _firstProvider) % Providers.Length);
        for (int i = 0; i < Providers.Length; i++)
        {
            var provider = Providers[(first + i) % Providers.Length];
            try
            {
                string key = provider.Url + "|" + host;
                if (!_cache.TryGetValue(key, out var cached) || cached.Expires <= Environment.TickCount64)
                {
                    byte[] query = BuildQuery(host);
                    string dns = Convert.ToBase64String(query).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                    using var request = new HttpRequestMessage(HttpMethod.Get, provider.Url + "?dns=" + dns)
                    { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionOrLower };
                    request.Headers.Accept.ParseAdd("application/dns-message");
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentType?.MediaType != "application/dns-message") throw new IOException("Неверный тип ответа DoH.");
                    await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    using var bytes = new MemoryStream();
                    byte[] buffer = new byte[4096];
                    int count;
                    while ((count = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        if (bytes.Length + count > 65535) throw new IOException("Слишком большой ответ DoH.");
                        bytes.Write(buffer, 0, count);
                    }
                    var parsed = ParseAnswer(bytes.ToArray(), host);
                    cached = (parsed.Addresses, Environment.TickCount64 + parsed.Ttl * 1000L);
                    if (cached.Addresses.Length == 0) throw new IOException("DoH не вернул публичный адрес.");
                    _cache[key] = cached;
                }
                foreach (var address in cached.Addresses.Take(4))
                {
                    try
                    {
                        string verifiedKey = host + "|" + address;
                        if (!_verified.TryGetValue(verifiedKey, out long until) || until <= Environment.TickCount64)
                        {
                            using var probe = await ConnectPublicAsync([address], ct).ConfigureAwait(false);
                            using var tls = new SslStream(probe, leaveInnerStreamOpen: false);
                            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            budget.CancelAfter(TimeSpan.FromSeconds(4));
                            // Default OS certificate validation, against the ORIGINAL Google name.
                            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, budget.Token).ConfigureAwait(false);
                            _verified[verifiedKey] = cached.Expires;
                            if (_verified.Count > 128)
                                foreach (var old in _verified.Where(v => v.Value <= Environment.TickCount64)) _verified.TryRemove(old.Key, out _);
                        }
                        Stream stream = await ConnectPublicAsync([address], ct).ConfigureAwait(false);
                        LastRoute = $"Маршрут: {provider.Name}. TLS Google проверен. Ответ модели не проверен.";
                        return stream;
                    }
                    catch (Exception ex) when (ex is IOException or SocketException or System.Security.Authentication.AuthenticationException or OperationCanceledException)
                    { ct.ThrowIfCancellationRequested(); }
                }
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or ArgumentException)
            { ct.ThrowIfCancellationRequested(); }
        }
        LastRoute = "Встроенные маршруты сейчас недоступны. Прямого резервного подключения к API не было. Попробуйте следующий маршрут или свой прокси.";
        throw new IOException("Встроенные маршруты Antigravity недоступны.");
    }

    private static async Task<Stream> ConnectPublicAsync(IEnumerable<IPAddress> addresses, CancellationToken ct)
    {
        foreach (var address in addresses.Where(IsPublic).Distinct().Take(4))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                budget.CancelAfter(TimeSpan.FromSeconds(3));
                await socket.ConnectAsync(address, 443, budget.Token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); ct.ThrowIfCancellationRequested(); }
        }
        throw new IOException("Нет доступного публичного адреса.");
    }

    internal static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return IsPublic(address.MapToIPv4());
        byte[] b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return b[0] is not (0 or 10 or 127) && b[0] < 224 &&
                !(b[0] == 100 && b[1] is >= 64 and <= 127) && !(b[0] == 169 && b[1] == 254) &&
                !(b[0] == 172 && b[1] is >= 16 and <= 31) && !(b[0] == 192 && b[1] == 168) &&
                !(b[0] == 192 && b[1] == 0 && b[2] is 0 or 2) &&
                !(b[0] == 198 && b[1] is 18 or 19) && !(b[0] == 198 && b[1] == 51 && b[2] == 100) &&
                !(b[0] == 203 && b[1] == 0 && b[2] == 113);
        // Only global-unicast IPv6; exclude documentation space as well.
        return b.Length == 16 && (b[0] & 0xe0) == 0x20 && !(b[0] == 0x20 && b[1] == 1 && b[2] == 0x0d && b[3] == 0xb8);
    }

    internal static byte[] BuildQuery(string host)
    {
        using var data = new MemoryStream();
        data.Write(new byte[] { 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
        foreach (string label in host.Split('.'))
        {
            if (label.Length is < 1 or > 63 || label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new ArgumentException("Некорректное имя DNS.");
            data.WriteByte((byte)label.Length);
            data.Write(Encoding.ASCII.GetBytes(label));
        }
        data.Write(new byte[] { 0, 0, 1, 0, 1 });
        if (data.Length > 271) throw new ArgumentException("Слишком длинное имя DNS.");
        return data.ToArray();
    }

    internal static (IPAddress[] Addresses, int Ttl) ParseAnswer(byte[] data, string host)
    {
        static IOException Invalid() => new("Некорректный ответ DNS.");
        int Read16(int offset) => offset + 2 <= data.Length ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2)) : throw Invalid();
        string Name(ref int offset)
        {
            int cursor = offset, hops = 0, size = 0;
            bool jumped = false;
            var labels = new List<string>();
            while (true)
            {
                if (cursor >= data.Length || ++hops > 128) throw Invalid();
                int length = data[cursor++];
                if ((length & 0xc0) == 0xc0)
                {
                    if (cursor >= data.Length) throw Invalid();
                    int target = ((length & 0x3f) << 8) | data[cursor++];
                    if (!jumped) offset = cursor;
                    jumped = true; cursor = target; continue;
                }
                if (length == 0) { if (!jumped) offset = cursor; return string.Join('.', labels); }
                if (length > 63 || cursor + length > data.Length || (size += length + 1) > 254) throw Invalid();
                labels.Add(Encoding.ASCII.GetString(data, cursor, length)); cursor += length;
            }
        }
        if (data.Length < 12 || Read16(0) != 0 || (Read16(2) & 0xfa0f) != 0x8000 || Read16(4) != 1) throw Invalid();
        int offset = 12;
        if (!Name(ref offset).Equals(host, StringComparison.OrdinalIgnoreCase) || Read16(offset) != 1 || Read16(offset + 2) != 1) throw Invalid();
        offset += 4;
        var aliases = new List<(string Owner, string Target)>();
        var records = new List<(string Owner, IPAddress Address, uint Ttl)>();
        for (int i = 0; i < Read16(6); i++)
        {
            string owner = Name(ref offset);
            int type = Read16(offset), klass = Read16(offset + 2), length = Read16(offset + 8);
            if (offset + 10 + length > data.Length) throw Invalid();
            uint ttl = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 4, 4));
            offset += 10;
            if (klass == 1 && type == 1 && length == 4) records.Add((owner, new IPAddress(data.AsSpan(offset, 4)), ttl));
            if (klass == 1 && type == 5)
            {
                int nameOffset = offset;
                string target = Name(ref nameOffset);
                if (nameOffset != offset + length) throw Invalid();
                aliases.Add((owner, target));
            }
            offset += length;
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { host };
        for (int i = 0; i < 8; i++) foreach (var alias in aliases) if (names.Contains(alias.Owner)) names.Add(alias.Target);
        var accepted = records.Where(r => names.Contains(r.Owner) && IsPublic(r.Address)).ToArray();
        uint ttlMin = accepted.Select(r => r.Ttl).Concat(aliases.Count > 0 ? new uint[] { 15 } : []).DefaultIfEmpty(15u).Min();
        return (accepted.Select(r => r.Address).Distinct().ToArray(), (int)Math.Clamp(ttlMin, 1u, 120u));
    }

    public void Dispose() => _http.Dispose();
}
