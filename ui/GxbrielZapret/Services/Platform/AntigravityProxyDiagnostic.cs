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
        string? value = AntigravityProxy.Resolve(process, user, machine);
        if (string.IsNullOrWhiteSpace(value)) return "AG_LS_PROXY: не задан. Маршрут Antigravity не проверен.";
        Uri uri;
        try { uri = AntigravityProxy.Parse(value); }
        catch (ArgumentException) { return "AG_LS_PROXY: некорректный или неподдерживаемый HTTP-прокси. Значение скрыто."; }
        // Never print credentials, paths, query strings or the raw environment value.
        string host = uri.HostNameType == UriHostNameType.IPv6 ? "[" + uri.Host.Trim('[', ']') + "]" : uri.IdnHost;
        string mismatch = !string.Equals(process ?? "", saved ?? "", StringComparison.Ordinal)
            ? " Настройки процесса и сохранённые настройки различаются. При запуске из GX используются сохранённые настройки пользователя/системы, затем окружение GX." : "";
        return $"AG_LS_PROXY: {uri.Scheme}://{host}:{uri.Port}. Доступность прокси и ответ модели не проверены." + mismatch;
    }
}
