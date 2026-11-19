using System.Net;
using System.Net.Http;

namespace GxbrielZapret.Services.Platform;

public enum AntigravityProxySource
{
    None,
    Scoped,
    User,
    Machine,
    Process
}

public sealed record AntigravityProxyResolution(
    string? Value,
    AntigravityProxySource Source,
    string? RedactedValue = null);

internal static class AntigravityProxy
{
    internal static AntigravityProxyResolution ResolveDetailed(string? scoped, string? user, string? machine, string? process = null)
    {
        if (!string.IsNullOrWhiteSpace(scoped))
            return new(scoped.Trim(), AntigravityProxySource.Scoped, RedactSafe(scoped.Trim()));
        if (!string.IsNullOrWhiteSpace(user))
            return new(user.Trim(), AntigravityProxySource.User, RedactSafe(user.Trim()));
        if (!string.IsNullOrWhiteSpace(machine))
            return new(machine.Trim(), AntigravityProxySource.Machine, RedactSafe(machine.Trim()));
        if (!string.IsNullOrWhiteSpace(process))
            return new(process.Trim(), AntigravityProxySource.Process, RedactSafe(process.Trim()));
        return new(null, AntigravityProxySource.None, null);
    }

    internal static string? Resolve(string? scoped, string? user, string? machine) =>
        ResolveDetailed(scoped, user, machine).Value;

    internal static string? Resolve(string? scoped, string? user, string? machine, string? process) =>
        ResolveDetailed(scoped, user, machine, process).Value;

    internal static string? Read() => Resolve(
        null,
        Environment.GetEnvironmentVariable("AG_LS_PROXY", EnvironmentVariableTarget.User),
        Environment.GetEnvironmentVariable("AG_LS_PROXY", EnvironmentVariableTarget.Machine),
        Environment.GetEnvironmentVariable("AG_LS_PROXY"));

    internal static string Redact(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        string trimmed = value.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            string host = uri.HostNameType == UriHostNameType.IPv6 ? "[" + uri.Host.Trim('[', ']') + "]" : uri.IdnHost;
            return $"{uri.Scheme}://{host}:{uri.Port}";
        }
        int slashIdx = trimmed.IndexOf("://", StringComparison.Ordinal);
        int authorityStart = slashIdx >= 0 ? slashIdx + 3 : 0;
        int pathIdx = trimmed.IndexOfAny(['/', '?', '#'], authorityStart);
        int authorityEnd = pathIdx >= 0 ? pathIdx : trimmed.Length;
        int count = authorityEnd - authorityStart;
        if (count > 0)
        {
            int atIdx = trimmed.LastIndexOf('@', authorityEnd - 1, count);
            if (atIdx >= authorityStart)
            {
                string prefix = slashIdx >= 0 ? trimmed[..(slashIdx + 3)] : "";
                return prefix + trimmed[(atIdx + 1)..];
            }
        }
        return trimmed;
    }

    internal static string? RedactSafe(string? value) =>
        value is null ? null : Redact(value);

    internal static bool ContainsCredentials(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            if (Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) && uri.UserInfo.Length > 0)
                return true;
        }
        catch { }
        string trimmed = value.Trim();
        int slashIdx = trimmed.IndexOf("://", StringComparison.Ordinal);
        int authorityStart = slashIdx >= 0 ? slashIdx + 3 : 0;
        int pathIdx = trimmed.IndexOfAny(['/', '?', '#'], authorityStart);
        int authorityEnd = pathIdx >= 0 ? pathIdx : trimmed.Length;
        int count = authorityEnd - authorityStart;
        if (count > 0)
        {
            int atIdx = trimmed.LastIndexOf('@', authorityEnd - 1, count);
            return atIdx >= authorityStart;
        }
        return false;
    }

    internal static (string RedactedUrl, string? UserInfo) SplitCredentials(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return ("", null);
        Uri uri = Parse(value);
        string host = uri.HostNameType == UriHostNameType.IPv6 ? "[" + uri.Host.Trim('[', ']') + "]" : uri.IdnHost;
        string redacted = $"{uri.Scheme}://{host}:{uri.Port}";
        string? userInfo = uri.UserInfo.Length > 0 ? uri.UserInfo : null;
        return (redacted, userInfo);
    }

    internal static string Combine(string proxyUrl, string? userInfo)
    {
        if (string.IsNullOrWhiteSpace(proxyUrl)) return "";
        if (string.IsNullOrWhiteSpace(userInfo)) return proxyUrl.Trim();
        Uri uri = Parse(proxyUrl);
        string host = uri.HostNameType == UriHostNameType.IPv6 ? "[" + uri.Host.Trim('[', ']') + "]" : uri.IdnHost;
        return $"{uri.Scheme}://{userInfo}@{host}:{uri.Port}";
    }

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
