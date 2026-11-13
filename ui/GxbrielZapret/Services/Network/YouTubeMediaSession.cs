using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GxbrielZapret.Services.Network;

/// <summary>Bounded, anonymous player fallback. Only a signed URL is cached, in memory, within a scan.
/// Every candidate must still download fresh media bytes. No cookies, account data or JS execution.
/// Player contexts follow the public YoutubeExplode 6.6.2 protocol documentation/source.</summary>
internal sealed class YouTubeMediaSession
{
    private readonly HashSet<string> _rejected = new(StringComparer.Ordinal);
    private Uri? _stream;
    private DateTime _until;
    public Uri? CachedStream => DateTime.UtcNow < _until && _stream is not null && !_rejected.Contains(_stream.AbsoluteUri) ? _stream : null;
    public void Clear() { _stream = null; _until = default; }
    public void Reject(Uri stream)
    {
        if (_rejected.Count < 16) _rejected.Add(stream.AbsoluteUri);
        if (_stream == stream || string.Equals(_stream?.AbsoluteUri, stream.AbsoluteUri, StringComparison.Ordinal))
            Clear();
    }
    public void Remember(Uri stream)
    {
        if (!ApplicationFlowProbe.IsVideoHost(stream)) return;
        _stream = stream;
        _until = DateTime.UtcNow.AddMinutes(10);
        if (ApplicationFlowProbe.ParseQuery(stream.Query).TryGetValue("expire", out string? expiry) && long.TryParse(expiry, out long seconds))
        {
            try { _until = new[] { _until, DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.AddSeconds(-30) }.Min(); }
            catch (ArgumentOutOfRangeException) { Clear(); }
        }
    }

    internal async Task<Uri?> ResolveAsync(HttpClient client, string page, CancellationToken ct, int startContextIndex = 0)
    {
        var match = Regex.Match(page, "[\"']VISITOR_DATA[\"']\\s*:\\s*\"(?<v>[^\"]{1,2048})\"", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        string? visitor = match.Success ? JsonSerializer.Deserialize<string>("\"" + match.Groups["v"].Value + "\"") : null;
        if (visitor is null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.youtube.com/sw.js_data");
            request.Headers.TryAddWithoutValidation("User-Agent", "com.google.android.youtube/21.26.364 (Linux; U; Android 11) gzip");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                string json = await YouTubeApplicationProbe.ReadBoundedAsync(response.Content, 1024 * 1024, true, ct).ConfigureAwait(false);
                if (json.StartsWith(")]}'", StringComparison.Ordinal)) json = json[4..];
                using var document = JsonDocument.Parse(json);
                try { visitor = document.RootElement[0][2][0][0][13].GetString(); }
                catch (Exception ex) when (ex is InvalidOperationException or IndexOutOfRangeException or ArgumentOutOfRangeException) { }
            }
        }
        if (string.IsNullOrWhiteSpace(visitor) || visitor.Length > 2048) return null;
        // Two public player contexts at most; no request flood when the service changes its API.
        bool[] contexts = { false, true };
        for (int idx = Math.Clamp(startContextIndex, 0, contexts.Length); idx < contexts.Length; idx++)
        {
            bool android = contexts[idx];
            ct.ThrowIfCancellationRequested();
            var context = new Dictionary<string, object>
            {
                ["clientName"] = android ? "ANDROID" : "VISIONOS", ["clientVersion"] = android ? "21.26.364" : "1.02",
                ["visitorData"] = visitor, ["hl"] = "en", ["gl"] = "US", ["utcOffsetMinutes"] = 0,
                ["osName"] = android ? "Android" : "visionOS", ["osVersion"] = android ? "11" : "26.5.23O471"
            };
            if (android) context["androidSdkVersion"] = 30;
            else { context["deviceMake"] = "Apple"; context["deviceModel"] = "RealityDevice17,1"; }
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://www.youtube.com/youtubei/v1/player")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { videoId = "M7lc1UVf-VE", context = new { client = context } }), Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("User-Agent", android
                ? "com.google.android.youtube/21.26.364 (Linux; U; Android 11) gzip"
                : "Mozilla/5.0 (Macintosh; Intel Mac OS X 15_7_3) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.0 Safari/605.1.15");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) continue;
            string json = await YouTubeApplicationProbe.ReadBoundedAsync(response.Content, 4 * 1024 * 1024, true, ct).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var stream = ApplicationFlowProbe.FindVideoUrl(document.RootElement);
            if (stream is not null && !_rejected.Contains(stream.AbsoluteUri))
                return stream;
        }
        return null;
    }
}
