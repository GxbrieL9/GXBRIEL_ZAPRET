using System.IO;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GxbrielZapret.Services.Infrastructure;

namespace GxbrielZapret.Services.Platform;

internal sealed record AntigravityByteChange(long Offset, byte[] Bytes);
internal sealed record AntigravityPatchPlan(string State, string Details, IReadOnlyList<AntigravityByteChange> Changes, byte[]? Text = null, string RuleId = "");
internal sealed record AntigravityBackup(int Version, string Target, string OriginalHash, string PatchedHash, string Component);

internal static class AntigravityPatchEngine
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const string ExtensionMarker = "/* GXBRIEL_AG_EXISTING_BINARY */";
    private const string ExtensionGuard = ";" + ExtensionMarker + "{const gxAgPath=options.targetPathOverride||getInstalledTargetPath();if(gxAgPath&&await pathExists(gxAgPath)){return gxAgPath;}}";
    private static Regex Pattern(string value) => new(value, RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromSeconds(1));
    private static readonly Regex IdeOriginal = Pattern(@"(resetIsTierGCPTos\(\),)this\.[A-Za-z_$0-9]+\.isGoogleInternal");
    private static readonly Regex IdePatched = Pattern(@"resetIsTierGCPTos\(\),true\b");
    private static readonly Regex ExtensionAnchor = Pattern("outputChannel\\s*\\.\\s*appendLine\\s*\\(\\s*(['\"])\\[INSTALL\\]\\s*Checking Antigravity releases[^'\"]*\\1\\s*\\)\\s*;?");
    private static readonly Regex ChannelOriginal = Pattern(@"const\s+isChannelChanged\s*=\s*manifestFetched\s*&&\s*lastInstalledUrl\s*!==\s*releaseBaseUrl\s*;");
    private static readonly Regex ChannelPatched = Pattern(@"const\s+isChannelChanged\s*=\s*false\s*;");
    private static readonly Regex LegacyGuard = Pattern(@"\{const __primary=options\.targetPathOverride\|\|getInstalledTargetPath\(\);const __candidates=\[__primary,.{0,256}\];for\(const __p of __candidates\)\{if\(__p&&await pathExists\(__p\)\)\{outputChannel\.appendLine\('\[INSTALL\] Existing binary found at '\+__p\+'\. Skipping version check and re-download\.'\);return __p;\}\}\}");

    public static AntigravityPatchPlan Analyze(Stream stream, string component)
    {
        stream.Position = 0;
        if (component is "Ide" or "VsCode")
        {
            if (stream.Length > 32 * 1024 * 1024) return Unknown("JavaScript превышает безопасный размер 32 МБ.");
            using var output = new MemoryStream();
            stream.CopyTo(output);
            string text = Utf8.GetString(output.ToArray());
            return AnalyzeText(text, component);
        }
        if (component is not ("LanguageServer" or "Agy")) return Unknown("Неизвестный компонент.");
        using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
        if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64) return Unknown("Поддерживаются только проверенные сигнатуры Windows x64.");
        if (InspectAlternativePatch(stream) is { } alternative) return alternative;
        bool cli = component == "Agy";
        var original = Pattern(cli ? @"\x48\x85\xc0\x0f\x84....\x80\x78\x08\x00\x0f\x85...." : @"\x80\x78\x08\x00\x74.\x48\x8b.\x24.\x48\x89.\x60");
        var patched = Pattern(cli ? @"\x48\x85\xc0\x0f\x84....\x48\x85\xc0\x90\x0f\x85...." : @"\xc6\x40\x08\x01\x90\x90\x48\x8b.\x24.\x48\x89.\x60");
        var before = new HashSet<long>();
        var after = new HashSet<long>();
        byte[] buffer = new byte[65536 + 32];
        foreach (var section in pe.PEHeaders.SectionHeaders.Where(s => (s.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0))
        {
            long start = section.PointerToRawData, end = start + section.SizeOfRawData;
            if (start < 0 || end > stream.Length) return Unknown("Повреждённая таблица секций PE.");
            for (long offset = start; offset < end; offset += 65536)
            {
                stream.Position = offset;
                int count = (int)Math.Min(buffer.Length, end - offset);
                stream.ReadExactly(buffer.AsSpan(0, count));
                string data = Encoding.Latin1.GetString(buffer, 0, count);
                foreach (Match match in original.Matches(data)) before.Add(offset + match.Index);
                foreach (Match match in patched.Matches(data)) after.Add(offset + match.Index);
                if (before.Count + after.Count > 1) return Unknown("Несколько совпадений сигнатуры: автоматический патч запрещён.");
            }
        }
        var region = AntigravityRegionSchema.Analyze(stream, pe.PEHeaders);
        var codeAssist = cli ? AntigravityCodeAssistSchema.Analyze(stream, pe.PEHeaders) : null;
        if (region is not null || codeAssist is not null)
        {
            if (region?.State == "Unknown") return region;
            if (codeAssist?.State == "Unknown") return codeAssist;
            if (region is null || cli && codeAssist is null)
                return Unknown("Для CLI нужны согласованные схемы AuthResult и LoadCodeAssistResponse. Старый машинный патч недостаточен; запись запрещена.");
            var changes = region.Changes.ToList();
            if (codeAssist is not null) changes.AddRange(codeAssist.Changes);
            if (before.Count == 1) changes.Add(new(before.Single() + (cli ? 9 : 0),
                cli ? [0x48, 0x85, 0xc0, 0x90] : [0xc6, 0x40, 0x08, 0x01, 0x90, 0x90]));
            bool anyApplied = region.State is "Patched" or "Partial" || codeAssist?.State is "Patched" or "Partial" || after.Count == 1;
            return new(changes.Count == 0 ? "Patched" : anyApplied ? "Partial" : "Unpatched",
                region.Details + (codeAssist is null ? "" : " " + codeAssist.Details) +
                (before.Count + after.Count == 1 ? " Старый auth gate также проверен." : " Старый auth gate отсутствует; машинный код не изменяется."), changes,
                RuleId: cli ? "cli-eligibility-schema-v2" : "authresult-schema-v1");
        }
        if (after.Count == 1 && before.Count == 0) return new("Patched", "Обнаружена полная x64-сигнатура патча. Ответ модели не проверен.", []);
        if (before.Count != 1) return Unknown("Сигнатура этой версии не распознана; файл не изменён.");
        return new("Unpatched", "Найдена одна точная x64-сигнатура.",
            [new(before.Single() + (cli ? 9 : 0), cli ? [0x48, 0x85, 0xc0, 0x90] : [0xc6, 0x40, 0x08, 0x01, 0x90, 0x90])]);
    }

    internal static AntigravityPatchPlan? InspectAlternativePatch(Stream stream)
    {
        long saved = stream.Position;
        bool region = false, proxy = false, stockRegion = false, stockProxy = false;
        try
        {
            stream.Position = 0;
            byte[] buffer = new byte[65536 + 32];
            for (long offset = 0; offset < stream.Length; offset += 65536)
            {
                stream.Position = offset;
                int count = (int)Math.Min(buffer.Length, stream.Length - offset);
                stream.ReadExactly(buffer.AsSpan(0, count));
                string text = Encoding.Latin1.GetString(buffer, 0, count);
                region |= text.Contains("inexigible", StringComparison.Ordinal);
                proxy |= text.Contains("AG_LS_PROXY", StringComparison.Ordinal);
                stockRegion |= text.Contains("ineligible", StringComparison.Ordinal);
                stockProxy |= text.Contains("https_proxy", StringComparison.Ordinal);
            }
            if (!region && !proxy) return null;
            bool complete = region && proxy && !stockRegion && !stockProxy;
            return new(complete ? "ExternalPatch" : "ExternalPartial",
                $"Найдены маркеры альтернативного патча: регион={(region ? "изменён" : "не подтверждён")}, прокси={(proxy ? "изменён" : "не подтверждён")}. " +
                "Это отдельный способ изменения файла. Совместное применение патча GX запрещено; работоспособность и ответ модели не проверены.", []);
        }
        finally { stream.Position = saved; }
    }

    internal static AntigravityPatchPlan AnalyzeText(string text, string component)
    {
        if (component == "Ide")
        {
            int before = IdeOriginal.Matches(text).Count, after = IdePatched.Matches(text).Count;
            if (before == 0 && after > 0) return new("Patched", "Патч IDE найден. Вход и ответ модели не проверены.", []);
            if (before == 0 || after > 0 || before > 20) return Unknown("Неизвестная или смешанная сигнатура main.js.");
            return new("Unpatched", $"Найдено проверок IDE: {before}.", [], Utf8.GetBytes(IdeOriginal.Replace(text, "${1}true")));
        }
        if (AntigravityHubInstaller.Analyze(text) is { } hub) return hub;
        bool guard = text.Contains(ExtensionGuard, StringComparison.Ordinal) || LegacyGuard.IsMatch(text);
        bool channel = ChannelPatched.IsMatch(text);
        if (guard && channel) return new("Patched", "Обе части патча расширения найдены.", []);
        if ((!guard && ExtensionAnchor.Matches(text).Count != 1) || (!channel && ChannelOriginal.Matches(text).Count != 1))
            return Unknown("Не хватает точных сигнатур расширения; частичная запись запрещена.");
        string updated = text;
        if (!guard)
        {
            // Use only APIs present at the observed installer entry point; do not disable arbitrary downloads.
            updated = ExtensionAnchor.Replace(updated, m => m.Value + ExtensionGuard, 1);
        }
        if (!channel) updated = ChannelOriginal.Replace(updated, "const isChannelChanged = false;", 1);
        return new(guard || channel ? "Partial" : "Unpatched", "Проверены обе точки изменения расширения.", [], Utf8.GetBytes(updated));
    }

    private static AntigravityPatchPlan Unknown(string reason) => new("Unknown", reason, []);
    internal static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    private static string Store(string path) => Path.Combine(AppPaths.AppDataRoot, "antigravity", "backups",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()))));

    internal static bool CanRestore(string path, string component)
    {
        try { return ReadBackup(path, component) is not null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return false; }
    }

    private static AntigravityBackup? ReadBackup(string path, string component)
    {
        string dir = Store(path), manifest = Path.Combine(dir, "current.json");
        if (!File.Exists(manifest) || new FileInfo(manifest).Length > 8192) return null;
        var saved = JsonSerializer.Deserialize<AntigravityBackup>(File.ReadAllText(manifest));
        if (saved is not { Version: 1 } || saved.Component != component || !string.Equals(saved.Target, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(saved.OriginalHash ?? "", "^[A-F0-9]{64}$") || saved.PatchedHash != Hash(path)) return null;
        string backup = Path.Combine(dir, saved.OriginalHash + ".bin");
        return File.Exists(backup) && Hash(backup) == saved.OriginalHash ? saved : null;
    }

    internal static void Apply(string path, string component, CancellationToken ct)
    {
        path = Path.GetFullPath(path);
        RejectLink(path);
        string initialHash = Hash(path);
        // Upgrading a verified partial patch must retain the true original, not the intermediate file.
        var previousBackup = ReadBackup(path, component);
        AntigravityPatchPlan plan;
        using (var source = File.OpenRead(path)) plan = Analyze(source, component);
        if (plan.State == "Patched") return;
        if (plan.State is not ("Unpatched" or "Partial")) throw new InvalidOperationException(plan.Details);
        string temporary = path + ".gx-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            ct.ThrowIfCancellationRequested();
            if (plan.Text is not null) File.WriteAllBytes(temporary, plan.Text);
            else
            {
                File.Copy(path, temporary);
                using var output = new FileStream(temporary, FileMode.Open, FileAccess.Write, FileShare.None);
                foreach (var change in plan.Changes) { output.Position = change.Offset; output.Write(change.Bytes); }
                output.Flush(true);
            }
            using (var check = File.OpenRead(temporary))
                if (Analyze(check, component).State != "Patched") throw new IOException("Проверка подготовленного файла не пройдена.");
            if (Hash(path) != initialHash) throw new IOException("Компонент обновился во время операции. Патч отменён.");
            string dir = Store(path);
            Directory.CreateDirectory(dir);
            string backup = Path.Combine(dir, initialHash + ".bin");
            if (!File.Exists(backup)) File.Copy(path, backup);
            if (Hash(backup) != initialHash) throw new IOException("Контрольная сумма резервной копии не совпала.");
            var saved = new AntigravityBackup(1, path, previousBackup?.OriginalHash ?? initialHash, Hash(temporary), component);
            string manifest = Path.Combine(dir, "current.json"), manifestTemp = manifest + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string? previousManifest = File.Exists(manifest) ? File.ReadAllText(manifest) : null;
            bool committedManifest = false;
            try
            {
                File.WriteAllText(manifestTemp, JsonSerializer.Serialize(saved));
                ct.ThrowIfCancellationRequested();
                if (Hash(path) != initialHash) throw new IOException("Файл изменился до записи. Операция отменена.");
                File.Move(manifestTemp, manifest, true);
                committedManifest = true;
                File.Replace(temporary, path, null);
            }
            catch
            {
                // A locked target must not invalidate the manifest for an earlier working patch.
                if (committedManifest)
                {
                    if (previousManifest is null) File.Delete(manifest);
                    else
                    {
                        File.WriteAllText(manifestTemp, previousManifest);
                        File.Move(manifestTemp, manifest, true);
                    }
                }
                throw;
            }
            finally { if (File.Exists(manifestTemp)) File.Delete(manifestTemp); }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static void Restore(string path, string component, CancellationToken ct)
    {
        RejectLink(path);
        var saved = ReadBackup(path, component) ?? throw new InvalidOperationException("Нет проверенной резервной копии для текущей версии. Откат старой версии запрещён.");
        string temporary = path + ".gx-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(Path.Combine(Store(path), saved.OriginalHash + ".bin"), temporary);
            if (Hash(temporary) != saved.OriginalHash) throw new IOException("Резервная копия повреждена.");
            ct.ThrowIfCancellationRequested();
            if (Hash(path) != saved.PatchedHash) throw new IOException("Приложение обновилось. Откат отменён.");
            File.Replace(temporary, path, null);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static void ClearV8Caches(string componentPath, string? applicationDataRoot = null)
    {
        string? install = AntigravityDiscovery.InstallRootFor(componentPath);
        if (install is null) return;
        string profile = File.Exists(Path.Combine(install, "Antigravity IDE.exe")) ? "Antigravity IDE" : "Antigravity";
        string appData = applicationDataRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData)) return;
        string profileRoot = Path.GetFullPath(Path.Combine(appData, profile));
        string[] cacheDirs = [Path.Combine(profileRoot, "CachedData"), Path.Combine(profileRoot, "Code Cache")];
        foreach (var dir in cacheDirs)
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                string target = Path.GetFullPath(dir);
                if (!target.StartsWith(profileRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                RejectLink(target);
                if (ContainsLink(target)) continue;
                Directory.Delete(target, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool ContainsLink(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) return true;
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
            }
        return false;
    }

    private static void RejectLink(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Пути через символические ссылки и junction не поддерживаются.");
    }
}
