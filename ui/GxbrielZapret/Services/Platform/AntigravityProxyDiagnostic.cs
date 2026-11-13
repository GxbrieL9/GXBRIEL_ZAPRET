namespace GxbrielZapret.Services.Platform;

internal static class AntigravityProxyDiagnostic
{
    internal static string Read()
    {
        try
        {
            return Describe(Environment.GetEnvironmentVariable("AG_LS_PROXY"),
                Environment.GetEnvironmentVariable("AG_LS_PROXY", EnvironmentVariableTarget.User),
                Environment.GetEnvironmentVariable("AG_LS_PROXY", EnvironmentVariableTarget.Machine));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        { return "AG_LS_PROXY: настройки недоступны для чтения; соединение не проверено."; }
    }

    internal static string Describe(string? process, string? user, string? machine)
    {
        string? saved = string.IsNullOrWhiteSpace(user) ? machine : user;
        string? value = string.IsNullOrWhiteSpace(process) ? saved : process;
        if (string.IsNullOrWhiteSpace(value)) return "AG_LS_PROXY: не задан. Маршрут Antigravity не проверен.";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host))
            return "AG_LS_PROXY: некорректный или неподдерживаемый HTTP-прокси. Значение скрыто.";
        // Never print credentials, paths, query strings or the raw environment value.
        string host = uri.HostNameType == UriHostNameType.IPv6 ? "[" + uri.Host.Trim('[', ']') + "]" : uri.IdnHost;
        string mismatch = !string.Equals(process ?? "", saved ?? "", StringComparison.Ordinal)
            ? " Настройки процесса GX и сохранённые настройки различаются; Antigravity может использовать другое значение после перезапуска." : "";
        return $"AG_LS_PROXY: {uri.Scheme}://{host}:{uri.Port}. Доступность прокси и ответ модели не проверены." + mismatch;
    }
}
