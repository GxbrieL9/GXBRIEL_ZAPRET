using System.Diagnostics;
using System.IO;
using System.Windows;
using GxbrielZapret.Services.Platform;

namespace GxbrielZapret.ViewModels;

public sealed partial class MainViewModel
{
    public string? PatcherLauncherPath { get; private set; }
    public string PatcherLauncherDisplay => _patcherSelectionRequired ? "Выберите установку Antigravity для запуска и применения патча." :
        PatcherLauncherPath ?? "Приложение для запуска не найдено";
    public string PatcherLaunchReason => !CanRunPatcher ? "Дождитесь завершения текущей проверки или операции." :
        _patcherSelectionRequired ? "Найдено несколько установок Antigravity. Выберите нужный каталог кнопкой папки." :
        PatcherLauncherPath is null ? "Исполняемый файл не найден. Проверьте файлы или выберите каталог установки." : PatcherLauncherPath;
    public string PatcherPatchAllReason => !CanRunPatcher ? "Дождитесь завершения текущей проверки или операции." :
        _patcherSelectionRequired ? "Найдено несколько установок Antigravity. Выберите нужный каталог кнопкой папки." :
        PatcherComponents.Any(c => c.CanPatch) ? "Применить только совместимые патчи. Исходные файлы будут сохранены для отката." :
        PatcherComponents.Any(c => c.State is "ExternalPatch" or "ExternalPartial") ? "Найден альтернативный патч. GX не накладывает свой патч поверх другого способа изменения файла." :
        PatcherComponents.Any(c => c.IsPatched) ? "Найденные совместимые компоненты уже пропатчены. Повторная запись не нужна." :
        "Нет совместимых компонентов для применения. Проверьте файлы и каталог установки.";
    public string PatcherRestoreAllReason => !CanRunPatcher ? "Дождитесь завершения текущей проверки или операции." :
        _patcherSelectionRequired ? "Найдено несколько установок Antigravity. Выберите нужный каталог кнопкой папки." :
        PatcherComponents.Any(c => c.CanRestore) ? "Восстановить только компоненты с проверенной исходной копией текущей версии." :
        "Нет проверенных исходных копий GX. Для восстановления ранее установленного патча используйте официальный установщик Antigravity.";
    public string PatcherActionSummary => PatcherComponents.All(c => c.State == "NotChecked") ? "Файлы ещё не проверены." :
        $"Найдено: {PatcherComponents.Count(c => c.IsFound)} · Патч обнаружен: {PatcherComponents.Count(c => c.IsPatched)} · " +
        $"Можно применить: {PatcherComponents.Count(c => c.CanPatch)} · Можно откатить: {PatcherComponents.Count(c => c.CanRestore)}" +
        (PatcherComponents.Any(c => c.State is "ExternalPatch" or "ExternalPartial")
            ? $" · Другой патч: {PatcherComponents.Count(c => c.State is "ExternalPatch" or "ExternalPartial")}" : "");
    public string PatcherCheckedAt { get; private set; } = "";

    internal static AntigravityStatusResult UncheckedPatcherStatus()
    {
        var result = new AntigravityStatusResult();
        foreach (var component in result.Components) component.State = "NotChecked";
        return result;
    }

    internal static string? ResolvePatcherLauncher(string? manualRoot, IEnumerable<AntigravityComponentStatus> components)
    {
        try
        {
            var roots = string.IsNullOrWhiteSpace(manualRoot)
                ? components.OrderBy(c => c.Key == "LanguageServer" ? 0 : c.Key == "Ide" ? 1 : 2).Where(c => c.IsFound).Select(c => AntigravityDiscovery.InstallRootFor(c.Path!))
                : new[] { AntigravityDiscovery.InstallRootFor(manualRoot) };
            return roots.Where(r => r is not null).SelectMany(r => new[] { "Antigravity IDE.exe", "Antigravity.exe" }.Select(n => Path.Combine(r!, n))).FirstOrDefault(File.Exists);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    private async Task ForceCloseAntigravityAsync()
    {
        if (!CanRunPatcher || PatcherLauncherPath is null) return;
        string root = Path.GetDirectoryName(PatcherLauncherPath)!;
        if (MessageBox.Show(root + "\n\nПринудительно завершить все процессы этой установки Antigravity, включая фоновые? Несохранённые изменения будут потеряны.",
            "Завершить все процессы Antigravity?", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        IsPatcherBusy = true;
        _patcherCts = new();
        try
        {
            PatcherStatusMessage = "Завершаю процессы Antigravity…";
            var remaining = await AntigravityProcesses.TerminateAsync(root, _patcherCts.Token);
            PatcherStatusMessage = remaining.Count == 0 ? "Все процессы выбранной установки завершены. Можно применить патч."
                : "Остались процессы: " + string.Join(", ", remaining) + ". Завершение не подтверждено.";
            AppendPatcherLog(PatcherStatusMessage);
        }
        catch (OperationCanceledException) { PatcherStatusMessage = "Операция остановлена. Часть процессов могла быть уже завершена."; }
        catch (Exception ex) { PatcherStatusMessage = "Не удалось завершить процессы: " + ex.Message; AppendPatcherLog(PatcherStatusMessage); }
        finally { _patcherCts.Dispose(); _patcherCts = null; IsPatcherBusy = false; }
    }

    private async Task RunPatcherApplicationAsync(bool restart, bool closeOnly = false)
    {
        if (!CanRunPatcher || PatcherLauncherPath is null) return;
        string executable = PatcherLauncherPath;
        string root = Path.GetDirectoryName(executable)!;
        if (!File.Exists(executable))
        {
            ApplyPatcherResult(UncheckedPatcherStatus());
            PatcherStatusMessage = "Исполняемый файл исчез или обновился. Повторите проверку файлов.";
            return;
        }
        IsPatcherBusy = true;
        _patcherCts = new();
        try
        {
            if ((restart || closeOnly) && MessageBox.Show(
                root + "\n\nСохраните изменения в Antigravity. Будет отправлен запрос обычного закрытия. Принудительное завершение не используется.",
                closeOnly ? "Закрыть Antigravity?" : "Перезапустить Antigravity?", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            if (restart || closeOnly)
            {
                PatcherStatusMessage = "Ожидаю закрытия Antigravity…";
                var remaining = await AntigravityPatcherService.RequestCloseProcessesAsync(root, _patcherCts.Token);
                if (remaining.Count > 0)
                {
                    PatcherStatusMessage = "Остались процессы: " + string.Join(", ", remaining) + ". Сохраните работу и закройте их вручную. Принудительное завершение не выполнялось.";
                    AppendPatcherLog(PatcherStatusMessage);
                    return;
                }
            }
            _patcherCts.Token.ThrowIfCancellationRequested();
            if (!closeOnly) await Task.Run(() => Process.Start(AntigravityLauncher.BuildStartInfo(executable, Settings.AntigravityDirectConnection)));
            PatcherStatusMessage = closeOnly ? "Процессы выбранной установки закрыты." : "Команда запуска Antigravity отправлена. Ответ модели не проверен.";
            if (!closeOnly && Settings.AntigravityDirectConnection) PatcherStatusMessage += " Режим без AG_LS_PROXY применён к новому процессу.";
            AppendPatcherLog(PatcherStatusMessage);
        }
        catch (OperationCanceledException) { PatcherStatusMessage = "Операция остановлена. Принудительное завершение процессов не выполнялось."; }
        catch (Exception ex) { PatcherStatusMessage = "Ошибка Antigravity: " + ex.Message; AppendPatcherLog(PatcherStatusMessage); }
        finally { _patcherCts.Dispose(); _patcherCts = null; IsPatcherBusy = false; }
    }
}
