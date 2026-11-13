using System;
using System.Diagnostics;
using System.Threading.Tasks;
using GxbrielZapret.Localization;

namespace GxbrielZapret.Services.Platform;

public static class NetworkFlushService
{
    public static async Task<(bool success, string message)> FlushDnsAsync()
    {
        return await Task.Run(() =>
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "ipconfig.exe",
                    Arguments = "/flushdns",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });

                if (proc is null)
                {
                    return (false, Loc.T("Не удалось запустить ipconfig.exe"));
                }

                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit(5000);

                if (proc.ExitCode == 0)
                {
                    return (true, Loc.T("Кэш сопоставителя DNS Windows успешно очищен."));
                }

                string err = !string.IsNullOrWhiteSpace(stderr) ? stderr : stdout;
                return (false, Loc.T("ipconfig завершился с кодом {0}: {1}", proc.ExitCode, err.Trim()));
            }
            catch (Exception ex)
            {
                return (false, Loc.T("Ошибка при очистке DNS: {0}", ex.Message));
            }
        }).ConfigureAwait(false);
    }
}
