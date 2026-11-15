using System.Net;
using System.Net.Http;

namespace GxbrielZapret.Services.Platform;

internal static class AntigravityProxy
{
    internal static string? Resolve(string? process, string? user, string? machine) =>
        new[] { user, machine, process }.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    internal static string? Read() => Resolve(Environment.GetEnvironmentVariable("AG_LS_PROXY"),
        Environment.GetEnvironmentVariable("AG_LS_PROXY", EnvironmentVariableTarget.User),
        Environment.GetEnvironmentVariable("AG_LS_PROXY", EnvironmentVariableTarget.Machine));

    internal static Uri Parse(string value)
    {
        if (value.Any(char.IsControl) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https" or "socks5") || string.IsNullOrEmpty(uri.Host) || uri.Port <= 0 ||
            uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Укажите HTTP/HTTPS или SOCKS5 прокси: http://host:port или socks5://host:port.");
        return uri;
    }

    internal static async Task<string> CheckAsync(string value, CancellationToken ct)
    {
        Uri uri = Parse(value);
        var proxy = new WebProxy(new UriBuilder(uri) { UserName = "", Password = "" }.Uri);
        if (uri.UserInfo.Length > 0)
        {
            string[] credentials = uri.UserInfo.Split(':', 2);
            proxy.Credentials = new NetworkCredential(Uri.UnescapeDataString(credentials[0]),
                credentials.Length > 1 ? Uri.UnescapeDataString(credentials[1]) : "");
        }
        using var handler = new HttpClientHandler { Proxy = proxy, UseProxy = true, AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
        try
        {
            using var response = await http.GetAsync("https://cloudaicompanion-pa.googleapis.com/", HttpCompletionOption.ResponseHeadersRead, ct);
            return $"HTTPS через прокси: получен HTTP {(int)response.StatusCode} от Google. Это проверка соединения без аккаунта; доступ к модели и страна выхода не подтверждены.";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return "Прокси не ответил за 12 секунд. Проверьте адрес и работу VPN/прокси."; }
        catch (HttpRequestException)
        { return "Не удалось установить HTTPS через прокси. Проверьте адрес, авторизацию и доступ к Google. Прямое подключение не использовалось."; }
    }
}
