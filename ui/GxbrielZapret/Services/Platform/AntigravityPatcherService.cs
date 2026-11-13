using System.Diagnostics;
using System.IO;
using GxbrielZapret.Localization;

namespace GxbrielZapret.Services.Platform;

public sealed class AntigravityComponentStatus
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Path { get; set; }
    public string State { get; set; } = "NotFound";
    public string? Version { get; set; }
    public bool HasBackup { get; set; }
    public string Details { get; set; } = "";
    public string PatchRule { get; set; } = "";
    public string OperationError { get; set; } = "";
    public bool RequiredForIde { get; set; }
    public bool IsFound => !string.IsNullOrWhiteSpace(Path);
    public bool IsStandaloneCli => Key == "Agy" && IsFound && AntigravityDiscovery.InstallRootFor(Path!) is null;
    public bool IsPatched => State == "Patched";
    public bool CanPatch => IsFound && State is "Unpatched" or "Partial";
    public bool CanRestore => IsFound && HasBackup && State is not ("Error" or "NotChecked" or "NotFound" or "NotRequired");
    public string PatchLabel => Loc.T(State == "NotChecked" ? "Не проверен" : State is "PackedCompanion" or "NotRequired" ? "Не требуется" : State == "PackedUnsupported" ? "Не поддерживается" : State is "ExternalPatch" or "ExternalPartial" ? "Другой патч" : IsPatched ? "Уже применён" : !IsFound ? "Недоступен" : "Применить патч");
    public string PatchReason => Loc.T(State switch
    {
        "NotChecked" => "Сначала проверьте файлы компонента.",
        "NotRequired" => "Для Antigravity IDE отдельный Language Server не требуется: его роль выполняет фоновый сервис agy --hub.",
        "NotFound" when Key == "Agy" && RequiredForIde => "Для этой IDE требуется CLI agy.exe. Первая загрузка выполняется установщиком Antigravity и требует доступа к серверу обновлений; патч не создаёт отсутствующий CLI.",
        "NotFound" when Key is "Agy" or "VsCode" => "Дополнительный компонент не найден. Для запуска IDE он не обязателен.",
        "NotFound" => "Компонент не установлен или не найден в выбранном каталоге.",
        "Patched" => "Патч уже обнаружен. Повторная запись не требуется.",
        "Unknown" => "Версия не поддерживается: запись запрещена до проверки сигнатуры.",
        "ExternalPatch" or "ExternalPartial" => "Обнаружен другой способ патча. Накладывать патч GX поверх него нельзя.",
        "PackedUnsupported" => "Интерфейс упакован в app.asar. Совместимый патч интерфейса для этой сборки отсутствует. Патч Language Server не подтверждает доступность аккаунта в регионе.",
        "PackedCompanion" => "Для распознанной схемы правило применяется к Language Server. Архив app.asar отдельно не изменяется. Вход и ответ модели не проверены.",
        "Error" => "Проверка завершилась ошибкой. Повторите её перед изменением файлов.",
        _ => "Применить совместимый патч с резервной копией исходного файла."
    });
    public string RestoreReason => Loc.T(State == "NotChecked" ? "Сначала проверьте файлы компонента." :
        !IsFound ? "Компонент не найден: восстанавливать нечего." :
        State == "Error" ? "Проверка завершилась ошибкой. Повторите её перед изменением файлов." :
        CanRestore ? "Восстановить проверенную копию текущей версии." :
        IsPatched ? "Нет исходной копии GX для этого патча. Для восстановления используйте официальный установщик Antigravity." :
        "Нет проверенной резервной копии текущей версии.");
    public string ActionExplanation => !string.IsNullOrWhiteSpace(OperationError) ? "Операция не выполнена: " + OperationError :
        !IsFound || State is "Unknown" or "Error" or "NotChecked" or "ExternalPatch" or "ExternalPartial" ? PatchReason :
        IsPatched && !HasBackup ? RestoreReason : IsPatched ? Loc.T("Патч уже применён. Проверенный откат доступен.") : PatchReason;
    public string StateDisplay => State switch
    {
        "NotRequired" => "Не требуется (agy --hub)",
        "PackedUnsupported" => "Упакованный интерфейс · патч не поддерживается",
        "PackedCompanion" => "Основной файл · правило применяется к Language Server",
        "Patched" => Loc.T("Патч обнаружен"), "Unpatched" => Loc.T("Патч доступен"),
        "Partial" => Loc.T("Частичный патч"), "Unknown" => Loc.T("Неизвестная версия"),
        "ExternalPatch" => "Альтернативный патч", "ExternalPartial" => "Альтернативный / смешанный патч",
        "Error" => Loc.T("Ошибка проверки"), "NotChecked" => Loc.T("Не проверен"), _ => Loc.T("Не найден")
    };
    public string StateColor => State switch { "Patched" => "#75BB9F", "NotRequired" => "#75BB9F", "Unpatched" => "#C5B76B", "Partial" => "#C5B76B", "Error" => "#E594A1", _ => "#A4A0AD" };
    public string StateBadgeBg => "#202024";
}

public sealed class AntigravityStatusResult
{
    public AntigravityComponentStatus LanguageServer { get; set; } = new() { Key = "LanguageServer", Name = "Antigravity · Language Server" };
    public AntigravityComponentStatus Ide { get; set; } = new() { Key = "Ide", Name = "Antigravity IDE · main.js" };
    public AntigravityComponentStatus VsCode { get; set; } = new() { Key = "VsCode", Name = "Расширение VS Code" };
    public AntigravityComponentStatus Agy { get; set; } = new() { Key = "Agy", Name = "Antigravity CLI · agy" };
    public List<string> Logs { get; set; } = [];
    public string Timestamp { get; set; } = "";
    public bool Succeeded { get; set; }
    public int ChangedCount { get; set; }
    public string Summary { get; set; } = "";
    public string ProxyDiagnostic { get; set; } = "";
    public bool SelectionRequired { get; set; }
    public string? InstallationRoot { get; set; }
    public IReadOnlyList<string> Installations { get; set; } = [];
    public IReadOnlyList<AntigravityComponentStatus> Components => [LanguageServer, Ide, VsCode, Agy];
}

public static class AntigravityPatcherService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static Task<AntigravityStatusResult> GetStatusAsync(CancellationToken ct = default, string? root = null) => ExecuteAsync("Status", "All", root, ct);
    public static Task<AntigravityStatusResult> PatchAsync(string target = "All", CancellationToken ct = default, string? root = null) => ExecuteAsync("Patch", target, root, ct);
    public static Task<AntigravityStatusResult> RestoreAsync(string target = "All", CancellationToken ct = default, string? root = null) => ExecuteAsync("Restore", target, root, ct);

    private static async Task<AntigravityStatusResult> ExecuteAsync(string action, string target, string? root, CancellationToken ct)
    {
        if (target is not ("All" or "LanguageServer" or "Ide" or "VsCode" or "Agy")) throw new ArgumentException("Неизвестный компонент.", nameof(target));
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var result = new AntigravityStatusResult { Timestamp = DateTime.UtcNow.ToString("O") };
                result.ProxyDiagnostic = AntigravityProxyDiagnostic.Read();
                result.Logs.Add(result.ProxyDiagnostic);
                var paths = AntigravityDiscovery.Find(root);
                result.Installations = AntigravityDiscovery.FindInstallations();
                result.SelectionRequired = string.IsNullOrWhiteSpace(root) && result.Installations.Count > 1;
                result.InstallationRoot = paths.Where(p => p.Key is "LanguageServer" or "Ide" or "VsCode")
                    .Select(p => AntigravityDiscovery.InstallRootFor(p.Value)).FirstOrDefault(p => p is not null);
                result.Agy.RequiredForIde = result.InstallationRoot is not null &&
                    File.Exists(Path.Combine(result.InstallationRoot, "resources/app/extensions/agy/vscode/extension.js"));
                bool isIdeInstallation = result.InstallationRoot is not null &&
                    (File.Exists(Path.Combine(result.InstallationRoot, "Antigravity IDE.exe")) ||
                     File.Exists(Path.Combine(result.InstallationRoot, "resources/app/extensions/agy/vscode/extension.js")));
                if (isIdeInstallation)
                {
                    result.Ide.Name = "Antigravity IDE · main.js";
                    result.VsCode.Name = "Antigravity IDE · расширение agy";
                    result.Agy.Name = "Antigravity IDE · CLI (agy)";
                    if (!paths.ContainsKey("LanguageServer"))
                    {
                        result.LanguageServer.Name = "Antigravity IDE · Language Server";
                        result.LanguageServer.State = "NotRequired";
                        result.LanguageServer.Details = result.LanguageServer.PatchReason;
                    }
                }
                else
                {
                    result.LanguageServer.Name = "Antigravity · Language Server";
                    result.Ide.Name = (paths.TryGetValue("Ide", out var ip) && Path.GetExtension(ip).Equals(".asar", StringComparison.OrdinalIgnoreCase))
                        ? "Antigravity · app.asar" : "Antigravity · main.js";
                    result.VsCode.Name = "Расширение VS Code";
                    result.Agy.Name = "Antigravity CLI · agy";
                }
                bool failed = false;
                int matched = 0;
                foreach (var component in result.Components)
                {
                    ct.ThrowIfCancellationRequested();
                    component.Path = paths.GetValueOrDefault(component.Key);
                    if (component.Key == "VsCode" && component.Path is not null && AntigravityDiscovery.InstallRootFor(component.Path) is not null)
                        component.Name = "Antigravity IDE · расширение agy";
                    bool selected = target == "All" || target == component.Key;
                    if (component.Path is null)
                    {
                        if (component.State != "NotRequired") component.Details = component.PatchReason;
                        if (action != "Status" && target == component.Key && component.State != "NotRequired") failed = true;
                        continue;
                    }
                    try
                    {
                        string? install = AntigravityDiscovery.InstallRootFor(component.Path);
                        if (install is not null)
                        {
                            string? exe = new[] { "Antigravity.exe", "Antigravity IDE.exe" }.Select(n => Path.Combine(install, n)).FirstOrDefault(File.Exists);
                            if (exe is not null)
                            {
                                try
                                {
                                    var vi = FileVersionInfo.GetVersionInfo(exe);
                                    string? ver = !string.IsNullOrWhiteSpace(vi.ProductVersion) ? vi.ProductVersion : vi.FileVersion;
                                    if (!string.IsNullOrWhiteSpace(ver)) component.Version = "приложение v" + ver.Trim();
                                }
                                catch { }
                            }
                        }
                        if (component.Key == "Ide" && Path.GetExtension(component.Path).Equals(".asar", StringComparison.OrdinalIgnoreCase))
                        {
                            component.State = "PackedUnsupported";
                            component.Details = component.PatchReason;
                            result.Logs.Add(component.Name + ": " + component.Details);
                            if (action != "Status" && target == component.Key) { failed = true; matched++; }
                            continue;
                        }
                        using (var file = File.OpenRead(component.Path)) SetPlan(component, AntigravityPatchEngine.Analyze(file, component.Key));
                        component.HasBackup = AntigravityPatchEngine.CanRestore(component.Path, component.Key);
                        if (component.IsPatched && !component.HasBackup) component.Details += " Нет проверенной копии GX для отката.";
                        if (action == "Status" || !selected || result.SelectionRequired && !(target == "Agy" && component.IsStandaloneCli)) continue;
                        if (action == "Restore" && target == "All" && !component.HasBackup) continue;
                        if (action == "Patch" && target == "All" && !component.CanPatch && !component.IsPatched)
                        {
                            result.Logs.Add(component.Name + ": пропущен — " + component.Details);
                            continue;
                        }
                        matched++;
                        bool needed = action == "Patch" ? !component.IsPatched : component.HasBackup;
                        if (!needed)
                        {
                            if (action == "Restore") { failed = true; result.Logs.Add(component.Name + ": нет проверенной копии для отката."); }
                            continue;
                        }
                        EnsureNotRunning(component.Path);
                        if (action == "Patch") AntigravityPatchEngine.Apply(component.Path, component.Key, ct);
                        else AntigravityPatchEngine.Restore(component.Path, component.Key, ct);
                        using (var check = File.OpenRead(component.Path)) SetPlan(component, AntigravityPatchEngine.Analyze(check, component.Key));
                        component.HasBackup = AntigravityPatchEngine.CanRestore(component.Path, component.Key);
                        if (action == "Patch" && !component.IsPatched) throw new IOException("Сигнатура после записи не подтверждена.");
                        if (component.Key == "Ide") AntigravityPatchEngine.ClearV8Caches(component.Path);
                        result.ChangedCount++;
                        result.Logs.Add(component.Name + (action == "Patch" ? ": патч записан и проверен." : ": исходный хэш восстановлен."));
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or BadImageFormatException or ArgumentException or TimeoutException)
                    {
                        if (action != "Status" && selected) RecordOperationFailure(component, ex.Message);
                        else
                        {
                            component.State = "Error";
                            component.HasBackup = false;
                            component.Details = ex.Message;
                        }
                        result.Logs.Add(component.Name + ": " + ex.Message);
                        failed |= selected || action == "Status";
                    }
                }
                if (result.LanguageServer.PatchRule == "authresult-schema-v1" && result.Ide.State == "PackedUnsupported")
                {
                    result.Ide.State = "PackedCompanion";
                    result.Ide.Details = result.Ide.PatchReason;
                }
                result.Succeeded = !failed && (action == "Status" || matched > 0);
                result.Summary = action == "Status" ? $"Найдено компонентов: {result.Components.Count(c => c.IsFound)}. Ответ модели не проверен." :
                    matched == 0 ? "Подходящих компонентов не найдено. Файлы не изменены." :
                    failed ? $"Операция выполнена не полностью. Изменено файлов: {result.ChangedCount}. Подробности в журнале." :
                    $"Проверено компонентов: {matched}. Изменено файлов: {result.ChangedCount}. Ответ модели не проверен.";
                result.Summary += " " + result.ProxyDiagnostic;
                if (result.InstallationRoot is not null) result.Summary += " Проверяется установка: " + result.InstallationRoot + ".";
                if (result.Agy.RequiredForIde && !result.Agy.IsFound) result.Summary += " " + result.Agy.PatchReason;
                if (result.SelectionRequired)
                    result.Summary = "Найдено несколько установок Antigravity. Для компонентов IDE выберите нужный каталог кнопкой папки. " +
                        "Отдельный CLI можно патчить независимо. Установки: " + string.Join("; ", result.Installations) + ". " + result.Summary;
                if (result.Ide.State == "PackedUnsupported")
                    result.Summary += " Интерфейс app.asar: патч не поддерживается. Региональная доступность аккаунта не подтверждена; патча Language Server может быть недостаточно.";
                return result;
            }, ct).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    private static void SetPlan(AntigravityComponentStatus component, AntigravityPatchPlan plan) { component.State = plan.State; component.Details = plan.Details; component.PatchRule = plan.RuleId; }

    internal static void RecordOperationFailure(AntigravityComponentStatus component, string message)
    {
        component.OperationError = message;
        // Reinspect after any failed mutation: preserve only facts verified against the current file.
        try
        {
            using var file = File.OpenRead(component.Path!);
            SetPlan(component, AntigravityPatchEngine.Analyze(file, component.Key));
            component.HasBackup = AntigravityPatchEngine.CanRestore(component.Path!, component.Key);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or BadImageFormatException or ArgumentException)
        {
            component.State = "Error";
            component.HasBackup = false;
            component.Details = "Не удалось повторно проверить файл: " + ex.Message;
        }
    }

    private static void EnsureNotRunning(string path)
    {
        string? install = AntigravityDiscovery.InstallRootFor(path);
        if (install is null && Path.GetExtension(path).Equals(".js", StringComparison.OrdinalIgnoreCase)) return;
        string[] names = install is null ? [Path.GetFileNameWithoutExtension(path)] : ["Antigravity", "Antigravity IDE", "language_server", "agy", "webm_encoder"];
        foreach (string name in names)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    string? executable;
                    try { executable = process.MainModule?.FileName; }
                    catch { throw new IOException("Закройте Antigravity перед изменением файлов. Процессы автоматически не завершаются."); }
                    if (executable is null) continue;
                    bool exact = string.Equals(executable, path, StringComparison.OrdinalIgnoreCase);
                    if (exact || install is not null && executable.StartsWith(install + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Закройте Antigravity перед изменением файлов. Несохранённая работа не прерывалась.");
                }
            }
        }
    }

    public static IReadOnlyList<string> GetRunningProcessNames(string? root = null)
        => AntigravityProcesses.Describe(root);

    public static Task<IReadOnlyList<string>> RequestCloseProcessesAsync(string? root = null, CancellationToken ct = default)
        => AntigravityProcesses.RequestCloseAsync(root, ct);
}
