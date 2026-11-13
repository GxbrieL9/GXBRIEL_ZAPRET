using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GxbrielZapret.Models;

namespace GxbrielZapret.Services.Network;

public sealed record FlowEvidence(bool? Passed, string Detail, long Bytes = 0, double? MegabitsPerSecond = null, long? FirstByteMs = null, long? TransferMs = null);
public sealed record ApplicationFlowResult(FlowEvidence YouTubeVideo, FlowEvidence DiscordGateway)
{
    public bool HasFailure(AutoScope scope) =>
        (scope != AutoScope.Discord && YouTubeVideo?.Passed == false) ||
        (scope != AutoScope.YouTube && DiscordGateway?.Passed == false);
    public bool IsConclusive(AutoScope scope) => Passes(scope) || HasFailure(scope);
    public bool Passes(AutoScope scope) =>
        Enum.IsDefined(scope) && YouTubeVideo is not null && DiscordGateway is not null &&
        (scope == AutoScope.Discord || YouTubeVideo is { Passed: true, Bytes: >= ApplicationFlowProbe.VideoSampleBytes }) &&
        (scope == AutoScope.YouTube || DiscordGateway?.Passed == true);
    public string Summary => $"YouTube: {YouTubeVideo.Detail} · Discord: {DiscordGateway.Detail} · Голос не проверен.";
}

public static class ApplicationFlowProbe
{
    public const int VideoSampleBytes = 2 * 1024 * 1024;
    public static Task<ApplicationFlowResult> CheckAsync(AutoScope scope, CancellationToken ct) => CheckAsync(scope, new YouTubeMediaSession(), ct);

    internal static async Task<ApplicationFlowResult> CheckAsync(AutoScope scope, YouTubeMediaSession session, CancellationToken ct, Action<string>? onProgress = null)
    {
        var youtube = scope == AutoScope.Discord ? Task.FromResult(new FlowEvidence(null, "не выбран")) : ProbeYouTubeVideoAsync(YouTubeApplicationProbe.Client, session, ct, onProgress);
        var discord = scope == AutoScope.YouTube ? Task.FromResult(new FlowEvidence(null, "не выбран")) : ProbeDiscordAsync(ct);
        await Task.WhenAll(youtube, discord).ConfigureAwait(false);
        return new(youtube.Result, discord.Result);
    }

    public static Task<FlowEvidence> ProbeYouTubeVideoAsync(CancellationToken ct) => ProbeYouTubeVideoAsync(YouTubeApplicationProbe.Client, new YouTubeMediaSession(), ct);

    internal static Task<FlowEvidence> ProbeYouTubeVideoAsync(HttpClient client, CancellationToken ct) => ProbeYouTubeVideoAsync(client, new YouTubeMediaSession(), ct);

    internal static async Task<FlowEvidence> ProbeYouTubeVideoAsync(HttpClient client, YouTubeMediaSession session, CancellationToken ct, Action<string>? onProgress = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        bool mediaAttempted = false;
        try
        {
            if (session.CachedStream is { } cached)
            {
                mediaAttempted = true;
                var sample = await ReadVideoSampleAsync(client, cached, deadline.Token, onProgress).ConfigureAwait(false);
                if (sample.Passed.HasValue) return sample;
                session.Clear();
                mediaAttempted = false;
            }
            onProgress?.Invoke("YouTube: получение страницы и ссылки на тестовое видео");
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.youtube.com/watch?v=M7lc1UVf-VE&hl=en");
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return new(null, $"тестовый ролик недоступен: HTTP {(int)response.StatusCode}; работа стратегии не определена");
            string page = await YouTubeApplicationProbe.ReadBoundedAsync(response.Content, 4 * 1024 * 1024, true, deadline.Token).ConfigureAwait(false);
            Uri? stream = FindDirectVideoUrl(page);
            bool usedPlayer = false;
            if (stream is null)
            {
                usedPlayer = true;
                stream = await session.ResolveAsync(client, page, deadline.Token).ConfigureAwait(false);
            }
            if (stream is null) return new(null, "YouTube не предоставил пригодный поток тестового ролика; это не доказательство блокировки");
            mediaAttempted = true;
            var result = await ReadVideoSampleAsync(client, stream, deadline.Token, onProgress).ConfigureAwait(false);
            if (result.Passed == true)
            {
                session.Remember(stream);
                return result;
            }
            if (result.Passed is null)
            {
                // A 403/404 from one player client (e.g. VISIONOS) does not mean the network blocks GGC;
                // try the alternate player context once before reporting inconclusive.
                session.Reject(stream);
                Uri? alt = await session.ResolveAsync(client, page, deadline.Token, startContextIndex: usedPlayer ? 1 : 0).ConfigureAwait(false);
                if (alt is not null)
                {
                    var retry = await ReadVideoSampleAsync(client, alt, deadline.Token, onProgress).ConfigureAwait(false);
                    if (retry.Passed == true) session.Remember(alt);
                    return retry;
                }
            }
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new(mediaAttempted ? false : null, mediaAttempted ? "передача видео: тайм-аут" : "получение тестовой ссылки: тайм-аут; передача видео не проверена"); }
        catch (JsonException) { return new(null, "ответ проигрывателя изменился; проверить видео не удалось"); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        { return new(mediaAttempted ? false : null, mediaAttempted ? "соединение с видеосервером не удалось" : "не удалось получить тестовую ссылку; передача видео не проверена"); }
    }

    internal static Uri? FindDirectVideoUrl(string page)
    {
        var marker = Regex.Match(page, "(?:\\bytInitialPlayerResponse\\s*|window\\s*\\[\\s*[\"']ytInitialPlayerResponse[\"']\\s*\\]\\s*)=\\s*", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!marker.Success) return null;
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(page[(marker.Index + marker.Length)..]));
        using var data = JsonDocument.ParseValue(ref reader);
        return FindVideoUrl(data.RootElement);
    }

    internal static Uri? FindVideoUrl(JsonElement player)
    {
        if (player.ValueKind != JsonValueKind.Object ||
            !player.TryGetProperty("streamingData", out var streams) || streams.ValueKind != JsonValueKind.Object) return null;
        foreach (string property in new[] { "formats", "adaptiveFormats" })
        {
            if (!streams.TryGetProperty(property, out var formats) || formats.ValueKind != JsonValueKind.Array) continue;
            foreach (var format in formats.EnumerateArray())
            {
                if (format.ValueKind != JsonValueKind.Object || !format.TryGetProperty("mimeType", out var mime) ||
                    mime.ValueKind != JsonValueKind.String || mime.GetString()?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) != true) continue;
                string? url = format.TryGetProperty("url", out var direct) && direct.ValueKind == JsonValueKind.String ? direct.GetString() : null;
                if (url is null && format.TryGetProperty("signatureCipher", out var cipher) && cipher.ValueKind == JsonValueKind.String)
                {
                    var fields = ParseQuery(cipher.GetString() ?? "");
                    // Encrypted signatures require a real decipherer, never guess them or claim success.
                    if (!fields.ContainsKey("s") && fields.TryGetValue("url", out var signed))
                    {
                        url = signed;
                        if (fields.TryGetValue("sig", out var signature) || fields.TryGetValue("signature", out signature))
                            url += (url.Contains('?') ? "&" : "?") + Uri.EscapeDataString(fields.GetValueOrDefault("sp", "signature")) + "=" + Uri.EscapeDataString(signature);
                    }
                }
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsVideoHost(uri)) return uri;
            }
        }
        return null;
    }

    internal static Dictionary<string, string> ParseQuery(string query) => query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(p => p.Split('=', 2)).GroupBy(p => Uri.UnescapeDataString(p[0]), StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => Uri.UnescapeDataString(g.Last().Length == 2 ? g.Last()[1] : ""), StringComparer.Ordinal);

    internal static bool IsVideoHost(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
        uri.Host.EndsWith(".googlevideo.com", StringComparison.OrdinalIgnoreCase);

    internal static async Task<FlowEvidence> ReadVideoSampleAsync(HttpClient client, Uri stream, CancellationToken ct, Action<string>? onProgress = null)
    {
        if (!IsVideoHost(stream)) return new(false, "неподдерживаемый адрес видеосервера");
        var builder = new UriBuilder(stream);
        builder.Query = builder.Query.TrimStart('?') + "&range=0-" + (VideoSampleBytes - 1);
        using var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
        request.Headers.Range = new(0, VideoSampleBytes - 1);
        onProgress?.Invoke("YouTube: подключение к видеосерверу, ожидание первых данных · 0 / 2048 КиБ");
        var timer = Stopwatch.StartNew();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            return new(null, $"тестовая ссылка/сервис отклонили запрос: HTTP {(int)response.StatusCode}; требуется другая ссылка");
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent) ||
            response.RequestMessage?.RequestUri is { } finalUri && !IsVideoHost(finalUri) ||
            response.Content.Headers.ContentType?.MediaType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) != true)
            return new(false, $"видеосервер: HTTP {(int)response.StatusCode}, ответ не является видео");
        using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        byte[] buffer = new byte[8192];
        byte[] header = new byte[32];
        int headerLength = 0;
        int received = 0;
        long? firstByteMs = null;
        long lastReport = -250;
        while (received < VideoSampleBytes)
        {
            int read = await body.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, VideoSampleBytes - received)), ct).ConfigureAwait(false);
            if (read == 0) return new(false, "видеопоток оборвался до получения фрагмента", received);
            firstByteMs ??= timer.ElapsedMilliseconds;
            int copy = Math.Min(read, header.Length - headerLength);
            if (copy > 0) { buffer.AsSpan(0, copy).CopyTo(header.AsSpan(headerLength)); headerLength += copy; }
            received += read;
            if (timer.ElapsedMilliseconds - lastReport >= 250 || received == VideoSampleBytes)
            {
                lastReport = timer.ElapsedMilliseconds;
                double speed = received * 8.0 / (Math.Max(1, lastReport) * 1000.0);
                onProgress?.Invoke($"YouTube: {received / 1024} / {VideoSampleBytes / 1024} КиБ · {speed:F2} Мбит/с · первые данные {firstByteMs} мс");
            }
        }
        bool mp4 = header.AsSpan(4, 4).SequenceEqual("ftyp"u8) || header.AsSpan(4, 4).SequenceEqual("styp"u8);
        bool webm = header.AsSpan(0, 4).SequenceEqual(new byte[] { 0x1a, 0x45, 0xdf, 0xa3 });
        if (!mp4 && !webm) return new(false, "ответ не содержит заголовка MP4/WebM", received);
        long elapsed = Math.Max(1, timer.ElapsedMilliseconds);
        double mbps = received * 8.0 / (elapsed * 1000.0);
        return new(true, $"получено {received / 1024} КиБ видео; первые данные {firstByteMs} мс; {mbps:F2} Мбит/с за {elapsed} мс; короткий замер, длительное воспроизведение не проверено",
            received, mbps, firstByteMs, elapsed);
    }

    public static async Task<FlowEvidence> ProbeDiscordAsync(CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        using var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        try
        {
            using var response = await YouTubeApplicationProbe.Client.GetAsync("https://discord.com/api/v10/gateway", HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return new(false, $"API: HTTP {(int)response.StatusCode}");
            string json = await YouTubeApplicationProbe.ReadBoundedAsync(response.Content, 65536, true, deadline.Token).ConfigureAwait(false);
            if (!IsDiscordGatewayResponse(json)) return new(false, "API не вернул корректный Gateway");
            await socket.ConnectAsync(new Uri("wss://gateway.discord.gg/?v=10&encoding=json"), deadline.Token).ConfigureAwait(false);
            using var packet = new MemoryStream();
            byte[] buffer = new byte[4096];
            while (packet.Length < 65536)
            {
                var message = await socket.ReceiveAsync(buffer.AsMemory(), deadline.Token).ConfigureAwait(false);
                if (message.MessageType != WebSocketMessageType.Text) return new(false, "Gateway закрыл соединение или вернул не JSON");
                if (packet.Length + message.Count > 65536) return new(false, "Gateway прислал слишком большой ответ");
                packet.Write(buffer, 0, message.Count);
                if (!message.EndOfMessage) continue;
                return IsGatewayHello(Encoding.UTF8.GetString(packet.ToArray()))
                    ? new(true, "API + WebSocket Hello получены; вход в аккаунт и голос не проверены")
                    : new(false, "Gateway не прислал корректный Hello");
            }
            return new(false, "Gateway прислал слишком большой ответ");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new(false, "API/Gateway: тайм-аут"); }
        catch (Exception ex) when (ex is HttpRequestException or WebSocketException or IOException or JsonException or InvalidOperationException)
        { return new(false, "API/Gateway: " + ex.Message); }
        finally { socket.Abort(); }
    }

    internal static bool IsDiscordGatewayResponse(string json)
    {
        using var data = JsonDocument.Parse(json);
        return data.RootElement.ValueKind == JsonValueKind.Object && data.RootElement.TryGetProperty("url", out var url) &&
            url.ValueKind == JsonValueKind.String && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri) &&
            uri.Scheme == "wss" && uri.Host == "gateway.discord.gg" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo);
    }
    internal static bool IsGatewayHello(string json)
    {
        using var data = JsonDocument.Parse(json);
        return data.RootElement.ValueKind == JsonValueKind.Object && data.RootElement.TryGetProperty("op", out var op) &&
            op.ValueKind == JsonValueKind.Number && op.TryGetInt32(out int code) && code == 10 &&
            data.RootElement.TryGetProperty("d", out var payload) && payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("heartbeat_interval", out var interval) && interval.ValueKind == JsonValueKind.Number &&
            interval.TryGetInt32(out int ms) && ms > 0;
    }
}
