using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using GxbrielZapret.Services.Infrastructure;

namespace GxbrielZapret.Services.Platform;

internal sealed record AntigravityMonitorResult(AntigravityStatusResult? Status, string Message, bool Notify, bool Pending = false);

// Metadata polling stays cheap while idle; signature scans run only after a stable change.
internal sealed class AntigravityUpdateMonitor
{
    internal sealed record Stamp(string Key, string Path, long Length, long Written, long Created);
    internal sealed record Snapshot(int Version, string Scope, Stamp[] Files, Dictionary<string, string> States, string Message);
    private readonly string _directory;
    private readonly Func<string?, CancellationToken, Task<AntigravityStatusResult>> _inspect;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<string?, CancellationToken, Task<AntigravityStatusResult>>? _apply;
    private readonly Func<string?, bool> _running;
    private string? _scope;
    private Snapshot? _snapshot;
    private Stamp[]? _candidate;
    private bool _delivered;

    internal AntigravityUpdateMonitor(string? directory = null,
        Func<string?, CancellationToken, Task<AntigravityStatusResult>>? inspect = null,
        Func<string?, CancellationToken, Task<AntigravityStatusResult>>? apply = null,
        Func<string?, bool>? running = null)
    {
        _directory = directory ?? Path.Combine(AppPaths.AppDataRoot, "antigravity", "monitor");
        _inspect = inspect ?? ((root, ct) => AntigravityPatcherService.GetStatusAsync(ct, root));
        _apply = apply;
        _running = running ?? (root => AntigravityPatcherService.GetRunningProcessNames(root).Count > 0);
    }

    internal async Task<AntigravityMonitorResult?> CheckAsync(string? root, CancellationToken ct = default,
        bool force = false, bool acknowledge = false, bool autoPatch = false)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string scope = string.IsNullOrWhiteSpace(root) ? "auto" : Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
            string file = Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope))) + ".json");
            if (_scope != scope)
            {
                _scope = scope; _candidate = null; _delivered = false;
                _snapshot = Load(file, scope);
            }
            Stamp[] before = await Task.Run(() => Capture(root), ct).ConfigureAwait(false);
            bool changed = _snapshot is null || !before.SequenceEqual(_snapshot.Files);
            if (!force && !changed && _delivered) return null;
            if (!force && changed && _snapshot is not null && (_candidate is null || !before.SequenceEqual(_candidate)))
            {
                _delivered = false;
                _candidate = before;
                return new(null, "Файлы Antigravity изменились. Ожидаю завершения обновления…", false, true);
            }
            var status = !string.IsNullOrWhiteSpace(root) && !Directory.Exists(root)
                ? new AntigravityStatusResult { Succeeded = true, Summary = "Каталог Antigravity не найден. Патчи не подтверждены." }
                : await _inspect(root, ct).ConfigureAwait(false);
            Stamp[] after = await Task.Run(() => Capture(root), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!before.SequenceEqual(after))
            {
                _delivered = false;
                _candidate = after;
                return new(null, "Файлы меняются во время проверки. Повторю после завершения обновления…", false, true);
            }
            if (!status.Succeeded || status.Components.Any(c => c.State == "Error"))
            {
                _delivered = false;
                return new(status, "Не удалось проверить обновление Antigravity. Проверка будет повторена; файлы не изменены.", false);
            }

            bool automaticApplied = false;
            var autoComponent = AutoComponent(status);
            if (autoPatch && autoComponent is not null)
            {
                _delivered = false;
                string? operationRoot = autoComponent.IsStandaloneCli ? Path.GetDirectoryName(autoComponent.Path!) : root;
                if (_running(operationRoot))
                    return new(status, "Автопатч ожидает закрытия Antigravity. Работающие процессы автоматически не завершаются.", false, true);
                var applied = await (_apply is not null ? _apply(operationRoot, ct) : AntigravityPatcherService.PatchAsync(autoComponent.Key, ct, operationRoot)).ConfigureAwait(false);
                if (!applied.Succeeded || !applied.Components.Any(c => c.Key == autoComponent.Key && c.IsPatched))
                    return new(applied, "Автопатч не подтверждён. Проверка будет повторена: " + applied.Summary, false, true);
                status = applied;
                after = await Task.Run(() => Capture(root), ct).ConfigureAwait(false);
                status = await _inspect(root, ct).ConfigureAwait(false);
                var verified = await Task.Run(() => Capture(root), ct).ConfigureAwait(false);
                if (!after.SequenceEqual(verified) || !status.Succeeded || !status.Components.Any(c => c.Key == autoComponent.Key && c.IsPatched))
                    return new(null, "Файлы изменились после автопатча. Ожидаю стабильную версию для повторной проверки.", false, true);
                automaticApplied = true;
            }

            string message = acknowledge ? "" : _snapshot?.Message ?? "";
            bool notify = false;
            if (automaticApplied)
            {
                string name = autoComponent?.Key switch { "VsCode" => "установщика Hub", "Agy" => "CLI agy", _ => "Language Server" };
                message = $"Совместимый патч {name} автоматически записан и проверен. Резервная копия сохранена. Вход и ответ модели не проверены.";
                notify = true;
            }
            else if (changed && _snapshot is not null && !acknowledge)
            {
                string[] lost = status.Components.Where(c => _snapshot.States.GetValueOrDefault(c.Key) == "Patched" && !c.IsPatched)
                    .Select(c => c.Name).ToArray();
                bool unknown = status.Components.Any(c => c.IsFound && c.State == "Unknown");
                message = lost.Length > 0
                    ? "После изменения файлов патч больше не подтверждён: " + string.Join(", ", lost) + ". " +
                        (unknown ? "Есть неизвестная версия: автоматическое изменение запрещено." :
                         status.Components.Any(c => c.CanPatch) ? "Совместимые компоненты можно пропатчить повторно." : "Проверьте каталог установки.")
                    : unknown ? "Файлы Antigravity изменились. Есть неизвестная версия: автоматическое изменение запрещено."
                    : "Файлы Antigravity изменились; совместимость перепроверена. Ответ модели не проверен.";
                notify = true;
            }
            if (status.SelectionRequired)
            {
                message = (automaticApplied ? message + " " : "") + "Найдено несколько установок: выберите каталог для компонентов IDE. Отдельный CLI проверяется независимо.";
                if (!automaticApplied) notify = false;
            }
            var next = new Snapshot(1, scope, after, status.Components.ToDictionary(c => c.Key, c => c.State), message);
            // Commit the checkpoint only after a successful inspection and atomic persistence.
            Save(file, next);
            _snapshot = next; _candidate = null; _delivered = !(autoPatch && AutoComponent(status) is not null);
            return new(status, message, notify);
        }
        catch { _delivered = false; throw; }
        finally { _gate.Release(); }
    }

    private static AntigravityComponentStatus? AutoComponent(AntigravityStatusResult status) =>
        !status.SelectionRequired && status.LanguageServer.CanPatch ? status.LanguageServer :
        status.Agy.CanPatch && status.Agy.PatchRule == "cli-eligibility-schema-v2" && (!status.SelectionRequired || status.Agy.IsStandaloneCli) ? status.Agy :
        !status.SelectionRequired && status.VsCode.CanPatch && status.VsCode.PatchRule == "hub-installer-1.11-v1" ? status.VsCode : null;

    private static Stamp[] Capture(string? root) => (!string.IsNullOrWhiteSpace(root) && !Directory.Exists(root)
        ? new Dictionary<string, string>() : AntigravityDiscovery.Find(root))
        .Concat(string.IsNullOrWhiteSpace(root) ? AntigravityDiscovery.FindInstallations().SelectMany(dir =>
            new[] { "Antigravity.exe", "Antigravity IDE.exe" }.Select(name => Path.Combine(dir, name)).Where(File.Exists))
            .Select(path => new KeyValuePair<string, string>("Installation:" + path, path)) : [])
        .OrderBy(p => p.Key, StringComparer.Ordinal)
        .Select(p =>
        {
            var info = new FileInfo(p.Value);
            return new Stamp(p.Key, info.FullName, info.Length, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks);
        }).ToArray();

    private static Snapshot? Load(string file, string scope)
    {
        try
        {
            if (!File.Exists(file) || new FileInfo(file).Length > 65536) return null;
            var value = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(file));
            return value is { Version: 1, Files: not null, States: not null, Message: not null } && value.Scope == scope &&
                value.Files.Length <= 16 && value.Files.All(s => s is not null && !string.IsNullOrWhiteSpace(s.Path)) ? value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static void Save(string file, Snapshot value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value));
            File.Move(temp, file, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
