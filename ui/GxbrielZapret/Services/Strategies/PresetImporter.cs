using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using GxbrielZapret.Models;
using GxbrielZapret.Services.Infrastructure;

namespace GxbrielZapret.Services.Strategies;

public sealed record ImportResult(
    int TotalFilesScanned,
    int SuccessfullyParsed,
    int NewImported,
    int SkippedDuplicates,
    int ErrorsCount,
    int NativeZapret2,
    int LegacyMaybe,
    int UnsupportedTokens,
    string SummaryMessage
);

internal sealed record ImportCompatibility(string Status, string Notes)
{
    public bool IsNativeZapret2 => string.Equals(Status, "native_zapret2", StringComparison.OrdinalIgnoreCase);
    public bool IsUnsupported => string.Equals(Status, "unsupported_tokens", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Imports external zapret/winws configurations (from DiscordFix, Flowseal, custom .bat/.cmd scripts,
/// or canonical .gxprofile.json files), extracts winws arguments, normalizes hostlist and fake payload
/// tokens, deduplicates by SHA-256 fingerprint, and stores them cleanly in profiles/broken as unverified candidates.
/// </summary>
public static class PresetImporter
{
    public static string DefaultExternalArchiveDir => "";

    public static ImportResult ImportFromDirectory(string directoryPath, PresetService presetService, Action<string>? progressCallback = null)
    {
        if (!Directory.Exists(directoryPath))
        {
            return new ImportResult(0, 0, 0, 0, 1, 0, 0, 0, $"Папка не найдена: {directoryPath}");
        }

        var files = Directory.EnumerateFiles(directoryPath, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".gxprofile.json", StringComparison.OrdinalIgnoreCase))
            .ToList();

        int parsed = 0;
        int imported = 0;
        int duplicates = 0;
        int errors = 0;
        int native = 0;
        int legacy = 0;
        int unsupported = 0;

        foreach (var file in files)
        {
            try
            {
                progressCallback?.Invoke($"Обработка {Path.GetFileName(file)}...");
                var candidate = ParseFile(file);
                if (candidate is null || candidate.Args.Count == 0)
                {
                    errors++;
                    continue;
                }

                parsed++;
                string fp = CanonicalProfile.ComputeFingerprint(candidate.Args);
                candidate.Fingerprint = fp;
                if (string.Equals(candidate.Compatibility, "native_zapret2", StringComparison.OrdinalIgnoreCase)) native++;
                else if (string.Equals(candidate.Compatibility, "unsupported_tokens", StringComparison.OrdinalIgnoreCase)) unsupported++;
                else legacy++;

                // Check for duplicate in existing preset service
                if (presetService.FindByFingerprint(fp) is not null)
                {
                    duplicates++;
                    continue;
                }

                // Save as BROKEN / imported_unverified according to AGENTS.md
                candidate.IsBuiltIn = false;
                candidate.Status = "BROKEN";
                candidate.Reason = "imported_unverified";
                presetService.AddUser(candidate);
                imported++;
            }
            catch
            {
                errors++;
            }
        }

        string summary = $"Импорт завершён: проверено {files.Count} файлов, распознано {parsed}, добавлено новых стратегий: {imported}, пропущено дубликатов: {duplicates}. " +
                         $"Совместимость: zapret2-native {native}, legacy-maybe {legacy}, unsupported {unsupported}.";
        return new ImportResult(files.Count, parsed, imported, duplicates, errors, native, legacy, unsupported, summary);
    }

    public static Preset? ParseFile(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext == ".json")
        {
            var profile = CanonicalProfile.LoadFromFile(filePath);
            return profile?.ToPreset();
        }

        string content = File.ReadAllText(filePath, Encoding.UTF8);
        return ParseBatchContent(Path.GetFileNameWithoutExtension(filePath), content);
    }

    public static Preset? ParseBatchContent(string rawName, string content)
    {
        // 1. Join line continuations with ^
        string[] lines = content.Split('\n');
        var joinedSb = new StringBuilder();
        foreach (var line in lines)
        {
            string trim = line.Trim();
            if (trim.StartsWith("rem", StringComparison.OrdinalIgnoreCase) ||
                trim.StartsWith("::", StringComparison.OrdinalIgnoreCase) ||
                trim.StartsWith("@echo", StringComparison.OrdinalIgnoreCase) ||
                trim.StartsWith("chcp", StringComparison.OrdinalIgnoreCase) ||
                trim.StartsWith("call", StringComparison.OrdinalIgnoreCase) ||
                trim.StartsWith("echo", StringComparison.OrdinalIgnoreCase) ||
                trim.StartsWith("pause", StringComparison.OrdinalIgnoreCase) ||
                trim.StartsWith("exit", StringComparison.OrdinalIgnoreCase) ||
                trim.StartsWith("cd ", StringComparison.OrdinalIgnoreCase) ||
                trim.StartsWith("set \"", StringComparison.OrdinalIgnoreCase) ||
                trim.StartsWith("set ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (trim.EndsWith('^'))
            {
                joinedSb.Append(trim[..^1].Trim()).Append(' ');
            }
            else
            {
                joinedSb.Append(trim).Append(' ');
            }
        }

        string joined = joinedSb.ToString();
        if (string.IsNullOrWhiteSpace(joined)) return null;

        // 2. Find start of winws command. Use regex instead of fixed offsets: winws.exe and
        // winws2.exe have different lengths, quoted paths may include folders, and old BATs often
        // launch through "start /min".
        var winwsMatch = Regex.Match(joined,
            @"(?:""[^""]*\\)?winws2?\.exe""?|\bwinws2?\.exe\b|\bwinws\b",
            RegexOptions.IgnoreCase);
        string cmdArgs = winwsMatch.Success ? joined[(winwsMatch.Index + winwsMatch.Length)..] : joined;

        var rawTokens = SplitCommandLine(cmdArgs);
        if (rawTokens.Count == 0) return null;

        // 3. Normalize tokens and replace environment vars and paths with canonical tokens
        var normalizedTokens = new List<string>();
        var compatibilitySignals = new List<string>();
        foreach (var tok in rawTokens)
        {
            string t = tok.Trim();
            if (string.IsNullOrEmpty(t) || t == "^" || t == "start" || t.StartsWith("/min", StringComparison.OrdinalIgnoreCase) || t.StartsWith("\"zapret:", StringComparison.OrdinalIgnoreCase))
                continue;

            // Remove surrounding quotes if any
            if (t.StartsWith('"') && t.EndsWith('"') && t.Length >= 2)
                t = t[1..^1];

            // Normalize variables and paths
            t = Regex.Replace(t, @"%BIN%quic_initial_([a-zA-Z0-9_\-\.]+)\.bin", "{FILES}\\fake\\quic_initial_$1.bin", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"%BIN%tls_clienthello_([a-zA-Z0-9_\-\.]+)\.bin", "{FILES}\\fake\\tls_clienthello_$1.bin", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"%BIN%([a-zA-Z0-9_\-\.]+\.bin)", "{FILES}\\fake\\$1", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"%BIN%", "{FILES}\\", RegexOptions.IgnoreCase);

            // Normalize list tokens
            t = Regex.Replace(t, @"--hostlist=""?%LISTS%list-ultimate\.txt""?", "{HOSTLIST:ultimate}", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"--hostlist=""?%LISTS%list-google\.txt""?", "{HOSTLIST:youtube}", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"--hostlist=""?%LISTS%list-discord\.txt""?", "{HOSTLIST:discord}", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"--hostlist=""?%LISTS%list-general\.txt""?", "{HOSTLIST:general}", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"--hostlist=""?%LISTS%list-([a-zA-Z0-9_\-]+)\.txt""?", "{HOSTLIST:$1}", RegexOptions.IgnoreCase);

            // Exclude tokens
            t = Regex.Replace(t, @"--hostlist-exclude=""?%LISTS%list-exclude\.txt""?", "{EXCLUDE:exclude}", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"--hostlist-exclude=""?%LISTS%([a-zA-Z0-9_\-]+)\.txt""?", "{EXCLUDE:$1}", RegexOptions.IgnoreCase);

            // Ipset tokens
            t = Regex.Replace(t, @"--ipset=""?%LISTS%ipset-all\.txt""?", "{IPSET:all}", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"--ipset=""?%LISTS%ipset-discord\.txt""?", "{IPSET}", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"--ipset=""?%LISTS%ipset-([a-zA-Z0-9_\-]+)\.txt""?", "{IPSET:$1}", RegexOptions.IgnoreCase);

            // Filter tokens
            if (t.StartsWith("--wf-tcp=", StringComparison.OrdinalIgnoreCase) && t.Contains("%GameFilter%", StringComparison.OrdinalIgnoreCase))
                t = "{WF_TCP}";
            else if (t.StartsWith("--wf-udp=", StringComparison.OrdinalIgnoreCase) && t.Contains("%GameFilter%", StringComparison.OrdinalIgnoreCase))
                t = "{WF_UDP}";
            else
                t = t.Replace("%GameFilter%", "443-65535", StringComparison.OrdinalIgnoreCase);

            AddCompatibilitySignals(t, compatibilitySignals);
            normalizedTokens.Add(t);
        }

        // Coalesce unquoted "--lua-init=var = expr" that got split on spaces around '='
        for (int i = 0; i + 2 < normalizedTokens.Count; i++)
        {
            if (normalizedTokens[i].StartsWith("--lua-init=", StringComparison.OrdinalIgnoreCase) &&
                normalizedTokens[i + 1] == "=")
            {
                normalizedTokens[i] = $"{normalizedTokens[i]} = {normalizedTokens[i + 2]}";
                normalizedTokens.RemoveRange(i + 1, 2);
            }
        }

        if (normalizedTokens.Count == 0) return null;

        string cleanName = rawName.Trim();
        if (cleanName.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) || cleanName.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
            cleanName = Path.GetFileNameWithoutExtension(cleanName);

        // Human-friendly title
        string title = $"Импорт: {cleanName}";
        var compatibility = AnalyzeCompatibility(normalizedTokens, compatibilitySignals);
        string reason = compatibility.IsNativeZapret2 ? "imported_native_zapret2_unverified"
            : compatibility.IsUnsupported ? "imported_unsupported_tokens"
            : "imported_legacy_maybe";

        return new Preset
        {
            Name = title,
            Tagline = compatibility.IsNativeZapret2
                ? "Импортировано: zapret2-compatible"
                : compatibility.IsUnsupported
                    ? "Импортировано: нужна ручная конвертация"
                    : "Импортировано: legacy, нужна проверка",
            Description = $"Конфигурация «{cleanName}», импортированная из внешнего архива пресетов. " +
                          $"Совместимость: {compatibility.Status}. {compatibility.Notes}",
            IsBuiltIn = false,
            IsRecommended = false,
            Status = "BROKEN",
            Reason = reason,
            Compatibility = compatibility.Status,
            CompatibilityNotes = compatibility.Notes,
            Args = normalizedTokens
        };
    }

    private static void AddCompatibilitySignals(string token, List<string> signals)
    {
        if (token.Contains('%') || token.Contains("%~", StringComparison.OrdinalIgnoreCase))
            signals.Add("unresolved_env_var");
        if (token.StartsWith("--dpi-desync", StringComparison.OrdinalIgnoreCase) ||
            token.StartsWith("--dpi-desync-", StringComparison.OrdinalIgnoreCase))
            signals.Add("legacy_dpi_desync_option");
        if (token.Contains("nfqws", StringComparison.OrdinalIgnoreCase) ||
            token.Contains("tpws", StringComparison.OrdinalIgnoreCase))
            signals.Add("other_zapret_binary");
        if (token.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) ||
            token.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
            token.StartsWith("if", StringComparison.OrdinalIgnoreCase) ||
            token.StartsWith("goto", StringComparison.OrdinalIgnoreCase))
            signals.Add("batch_control_flow");
        if ((token.Contains('\\') || token.Contains('/')) &&
            !token.Contains("{FILES}", StringComparison.OrdinalIgnoreCase) &&
            !token.Contains("{WF}", StringComparison.OrdinalIgnoreCase) &&
            !token.Contains("{HOSTLIST", StringComparison.OrdinalIgnoreCase) &&
            !token.Contains("{EXCLUDE", StringComparison.OrdinalIgnoreCase) &&
            !token.Contains("{IPSET", StringComparison.OrdinalIgnoreCase) &&
            (token.Contains(".bin", StringComparison.OrdinalIgnoreCase) ||
             token.Contains(".txt", StringComparison.OrdinalIgnoreCase)))
            signals.Add("absolute_or_external_file_path");
    }

    private static ImportCompatibility AnalyzeCompatibility(IReadOnlyList<string> args, IReadOnlyList<string> signals)
    {
        var distinct = signals.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        bool hasLuaDesync = args.Any(a => a.StartsWith("--lua-desync=", StringComparison.OrdinalIgnoreCase));
        bool hasWinws2Shape = hasLuaDesync ||
            args.Any(a => a.StartsWith("--wf-tcp", StringComparison.OrdinalIgnoreCase) ||
                          a.StartsWith("--wf-udp", StringComparison.OrdinalIgnoreCase) ||
                          a.StartsWith("--filter-", StringComparison.OrdinalIgnoreCase));

        if (distinct.Any(s => s is "unresolved_env_var" or "other_zapret_binary" or "batch_control_flow"))
        {
            return new ImportCompatibility(
                "unsupported_tokens",
                "В батнике остались переменные/управляющая логика или ссылки на другой zapret-бинарник: " +
                string.Join(", ", distinct) + ". Импорт сохранён как BROKEN-кандидат, но нужна ручная конвертация.");
        }

        if (distinct.Count > 0 || !hasWinws2Shape)
        {
            return new ImportCompatibility(
                "legacy_maybe",
                distinct.Count > 0
                    ? "Похоже на legacy-запуск: " + string.Join(", ", distinct) + ". Нужно проверить/конвертировать под winws2."
                    : "Не найден явный zapret2/nfqws2-профиль. Можно тестировать как кандидата, но WORK без проверки не ставим.");
        }

        return new ImportCompatibility(
            "native_zapret2",
            "Аргументы похожи на прямой профиль winws2/zapret2. Всё равно сохранено как BROKEN до проверки.");
    }

    private static List<string> SplitCommandLine(string commandLine)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine)) return result;

        var current = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < commandLine.Length; i++)
        {
            char c = commandLine[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
                current.Append(c);
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
            result.Add(current.ToString());

        return result;
    }
}
