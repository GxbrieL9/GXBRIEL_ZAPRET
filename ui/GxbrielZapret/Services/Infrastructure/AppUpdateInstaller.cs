using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace GxbrielZapret.Services.Infrastructure;

internal sealed record AppUpdateRequest(string Target, int ParentPid, long ParentStarted, string OriginalHash, string Nonce, string DataRoot);

internal static class AppUpdateInstaller
{
    internal static string UpdateRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GXBRIEL_ZAPRET", ".updates");
    internal static void RejectLinks(string path)
    {
        for (string? part = Path.GetFullPath(path); part is not null; part = Path.GetDirectoryName(part))
            if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Обновление через символические ссылки запрещено.");
    }
    private static string WorkDirectory(string requestPath)
    {
        string full = Path.GetFullPath(requestPath);
        string? directory = Path.GetDirectoryName(full);
        if (directory is null || Path.GetFileName(full) != "request.json" ||
            !string.Equals(Path.GetDirectoryName(directory), UpdateRoot, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) throw new IOException("Недопустимый каталог обновления.");
        RejectLinks(full);
        return directory;
    }
    internal static async Task BeginAsync(string directory)
    {
        string target = Environment.ProcessPath ?? throw new IOException("Не определён путь приложения.");
        if (!Path.GetFileName(target).Equals("GXBRIEL_ZAPRET.exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Обновление поддерживается для основного GXBRIEL_ZAPRET.exe.");
        RejectLinks(target);
        var manifest = AppUpdateService.ReadVerifiedManifest(directory);
        AppUpdateService.VerifyFile(Path.Combine(directory, "download.exe"), manifest);
        string helper = Path.Combine(directory, "updater.exe");
        File.Copy(Path.Combine(directory, "download.exe"), helper, false);
        AppUpdateService.VerifyFile(helper, manifest);
        using var parent = Process.GetCurrentProcess();
        var request = new AppUpdateRequest(target, parent.Id, parent.StartTime.ToUniversalTime().Ticks, AppUpdateTransaction.Hash(target), Guid.NewGuid().ToString("N"), AppPaths.AppDataRoot);
        string requestPath = Path.Combine(directory, "request.json");
        _ = WorkDirectory(requestPath);
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request));
        var info = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory };
        info.ArgumentList.Add("--apply-app-update"); info.ArgumentList.Add(requestPath);
        using var process = Process.Start(info) ?? throw new IOException("Не удалось запустить установку обновления.");
        for (int i = 0; i < 100; i++)
        {
            if (File.Exists(Path.Combine(directory, "ready")) && File.ReadAllText(Path.Combine(directory, "ready")) == request.Nonce) return;
            if (process.HasExited) throw new IOException("Установщик обновления завершился до проверки файлов.");
            await Task.Delay(100);
        }
        throw new IOException("Установщик не подтвердил готовность. Приложение осталось открытым.");
    }
    internal static async Task RunAsync(string requestPath)
    {
        string directory = WorkDirectory(requestPath);
        var request = JsonSerializer.Deserialize<AppUpdateRequest>(await File.ReadAllTextAsync(requestPath)) ?? throw new IOException("Пустой запрос обновления.");
        string target = Path.GetFullPath(request.Target);
        if (target != request.Target || !Path.GetFileName(target).Equals("GXBRIEL_ZAPRET.exe", StringComparison.OrdinalIgnoreCase)) throw new IOException("Некорректный путь приложения.");
        RejectLinks(target);
        var manifest = AppUpdateService.ReadVerifiedManifest(directory);
        AppUpdateService.VerifyFile(Environment.ProcessPath!, manifest);
        AppUpdateService.VerifyFile(Path.Combine(directory, "download.exe"), manifest);
        string oldVersion = FileVersionInfo.GetVersionInfo(target).FileVersion ?? "";
        if (AppUpdateService.ParseVersion(oldVersion) >= AppUpdateService.ParseVersion(manifest.Version)) throw new IOException("Установка более старой или той же версии запрещена.");
        using (var parent = Process.GetProcessById(request.ParentPid))
        {
            if (parent.StartTime.ToUniversalTime().Ticks != request.ParentStarted || !string.Equals(parent.MainModule?.FileName, target, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Процесс приложения изменился.");
            await File.WriteAllTextAsync(Path.Combine(directory, "ready"), request.Nonce);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await parent.WaitForExitAsync(timeout.Token);
        }
        Process? child = null;
        try
        {
            await AppUpdateTransaction.ApplyAsync(target, Path.Combine(directory, "download.exe"), request.OriginalHash, manifest.Sha256.ToUpperInvariant(), async () =>
            {
                var info = new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(target) };
                info.ArgumentList.Add("--window"); info.ArgumentList.Add("--update-health"); info.ArgumentList.Add(requestPath);
                info.ArgumentList.Add("--appdataroot"); info.ArgumentList.Add(request.DataRoot);
                child = Process.Start(info) ?? throw new IOException("Не удалось запустить новую версию.");
                for (int i = 0; i < 450; i++)
                {
                    if (File.Exists(Path.Combine(directory, "healthy")) && File.ReadAllText(Path.Combine(directory, "healthy")) == request.Nonce) return true;
                    if (child.HasExited) return false;
                    await Task.Delay(100);
                }
                // Only the process created above is stopped before restoring its executable.
                if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
                return false;
            });
            await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), "Обновление установлено. Запуск новой версии подтверждён.");
        }
        catch
        {
            // If rollback restored the exact previous file, relaunch it with the saved application state.
            if (AppUpdateTransaction.Hash(target) == request.OriginalHash)
            {
                var info = new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(target) };
                info.ArgumentList.Add("--window"); info.ArgumentList.Add("--appdataroot"); info.ArgumentList.Add(request.DataRoot); Process.Start(info)?.Dispose();
            }
            throw;
        }
        finally { child?.Dispose(); }
    }
    internal static void SignalHealthy(string requestPath)
    {
        string directory = WorkDirectory(requestPath);
        var request = JsonSerializer.Deserialize<AppUpdateRequest>(File.ReadAllText(requestPath))!;
        if (!string.Equals(request.Target, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) throw new IOException("Не совпадает путь запущенного обновления.");
        AppUpdateService.VerifyFile(request.Target, AppUpdateService.ReadVerifiedManifest(directory));
        File.WriteAllText(Path.Combine(directory, "healthy"), request.Nonce);
    }
}
