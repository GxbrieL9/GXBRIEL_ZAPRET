using System.IO;
using System.Text;

namespace GxbrielZapret.Services.Platform;

internal static class AntigravityServerDiagnostic
{
    internal static string Read()
    {
        string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Antigravity", "logs", "language_server.log");
        try
        {
            if (!File.Exists(file)) return "Ответ модели не проверен: журнал Antigravity App не найден.";
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Position = Math.Max(0, stream.Length - 262144);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return Describe(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return "Ответ модели не проверен: журнал Antigravity App недоступен."; }
    }

    internal static string Describe(string text)
    {
        // Only fixed messages leave this method: never disclose prompts, tokens or raw logs.
        var region = text.LastIndexOf("User location is not supported", StringComparison.OrdinalIgnoreCase);
        region = Math.Max(region, text.LastIndexOf("LOCATION_NOT_ELIGIBLE", StringComparison.OrdinalIgnoreCase));
        region = Math.Max(region, text.LastIndexOf("Your location is not eligible", StringComparison.OrdinalIgnoreCase));
        if (region >= 0)
            return "В журнале Antigravity App найден серверный отказ по местоположению (HTTP 400/403). Локальный патч не подтверждает доступ к модели и не отменяет проверку на сервере. Повторное применение патча не устранит этот отказ. Проверьте разрешённый для сервиса маршрут подключения; после изменения маршрута перезапустите App и проверьте ответ модели. Это запись журнала, текущий ответ модели не проверен.";
        return "Ответ модели не проверен. Серверный отказ по местоположению в просмотренной части журнала не найден.";
    }
}
