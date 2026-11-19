namespace GxbrielZapret.Services.Platform;

internal static class AntigravityProxyDiagnostic
{
    internal static string Read(string? scoped = null)
    {
        try
        {
            return Describe(
                Environment.GetEnvironmentVariable("AG_LS_PROXY"),
                Environment.GetEnvironmentVariable("AG_LS_PROXY", EnvironmentVariableTarget.User),
                Environment.GetEnvironmentVariable("AG_LS_PROXY", EnvironmentVariableTarget.Machine),
                scoped);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        { return "AG_LS_PROXY: настройки недоступны для чтения; соединение не проверено."; }
    }

    internal static string Describe(string? process, string? user, string? machine, string? scoped = null)
    {
        var resolution = AntigravityProxy.ResolveDetailed(scoped, user, machine, process);
        if (string.IsNullOrWhiteSpace(resolution.Value)) return "AG_LS_PROXY: не задан. Маршрут Antigravity не проверен.";
        Uri uri;
        try { uri = AntigravityProxy.Parse(resolution.Value); }
        catch (ArgumentException) { return "AG_LS_PROXY: некорректный или неподдерживаемый HTTP-прокси. Значение скрыто."; }
        // Never print credentials, paths, query strings or the raw environment value.
        string host = uri.HostNameType == UriHostNameType.IPv6 ? "[" + uri.Host.Trim('[', ']') + "]" : uri.IdnHost;
        string? saved = string.IsNullOrWhiteSpace(user) ? machine : user;
        string mismatch = "";
        if (!string.IsNullOrWhiteSpace(scoped) && !string.IsNullOrWhiteSpace(saved) &&
            !string.Equals(AntigravityProxy.RedactSafe(scoped), AntigravityProxy.RedactSafe(saved), StringComparison.OrdinalIgnoreCase))
        {
            mismatch = " Применён явный локальный приоритет запуска GX (Scoped override); сохранённые настройки пользователя/системы отличаются.";
        }
        else if (string.IsNullOrWhiteSpace(scoped) && !string.Equals(process ?? "", saved ?? "", StringComparison.Ordinal))
        {
            mismatch = " Настройки процесса и сохранённые настройки различаются. При запуске из GX используются сохранённые настройки пользователя/системы, затем окружение GX.";
        }
        return $"AG_LS_PROXY: {uri.Scheme}://{host}:{uri.Port}. Доступность прокси и ответ модели не проверены." + mismatch;
    }
}
