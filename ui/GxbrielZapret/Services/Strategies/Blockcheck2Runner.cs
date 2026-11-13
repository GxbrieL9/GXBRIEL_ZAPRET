using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using GxbrielZapret.Models;
using GxbrielZapret.Services.Infrastructure;
using GxbrielZapret.Services.Platform;

namespace GxbrielZapret.Services.Strategies;

/// <summary>Runs the upstream zapret2 blockcheck2 script as a real diagnostic.</summary>
public sealed class Blockcheck2Runner : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    public void Dispose() => _lifetime.Cancel();
    public sealed record Result(int ExitCode, string Output, string? Error, string ScriptPath, string LogPath = "");

    /// <summary>Extracts the first strategy reported in the COMMON/SUMMARY section.</summary>
    public static Preset? ExtractPreset(string output, IReadOnlyList<string> domains)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var lines = output.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
        bool inCommon = false, inSummary = false;
        IEnumerable<string> candidates = lines.Where(line =>
        {
            if (line.Equals("* SUMMARY", StringComparison.OrdinalIgnoreCase)) { inSummary = true; return false; }
            if (line.Equals("* COMMON", StringComparison.OrdinalIgnoreCase)) { inCommon = true; inSummary = false; return false; }
            if (inCommon && line.StartsWith("* ", StringComparison.Ordinal)) { inCommon = false; }
            return domains.Count > 1 ? inCommon : inCommon || inSummary;
        });
        foreach (string line in candidates)
        {
            if (line.Contains("not working", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("working without bypass", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("test aborted", StringComparison.OrdinalIgnoreCase)) continue;
            Match match = Regex.Match(line, @"(?i)(?:^|\s)(?:winws2(?:\.exe)?|nfqws2(?:\.exe)?)\s+(.+)$");
            if (!match.Success) continue;
            List<string> args = SplitCommandLine(match.Groups[1].Value);
            args.RemoveAll(a => a.Equals("\\", StringComparison.Ordinal));
            if (args.Count == 0 || !args.Any(a => a.StartsWith("--lua-desync=", StringComparison.Ordinal)) ||
                args.Any(a => a is ";" or "&&" or "||" or "|" or ">" or "<")) continue;
            string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            return new Preset
            {
                Name = $"blockcheck2 — {stamp}",
                Description = $"Стратегия найдена upstream blockcheck2 для {domains.Count} целей.",
                Tagline = "Найдена blockcheck2 — проверьте в приложении",
                Args = args,
                IsGenerated = false,
                Status = "UNTESTED",
                Reason = "blockcheck2_found_requires_gui_verify",
                Provenance = "UPSTREAM_BLOCKCHECK2",
                TargetServices = string.Join(", ", domains)
            };
        }
        return null;
    }

    private static List<string> SplitCommandLine(string value)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        char quote = '\0';
        foreach (char c in value.Trim())
        {
            if (quote != '\0') { if (c == quote) quote = '\0'; else current.Append(c); continue; }
            if (c is '\'' or '"') { quote = c; continue; }
            if (char.IsWhiteSpace(c)) { if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); } }
            else current.Append(c);
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    public static string? FindScript()
    {
        string script = Path.Combine(AppPaths.EngineDir, "blockcheck2", "blockcheck2.sh");
        return File.Exists(script) ? script : null;
    }

    public static string? FindBash()
    {
        string bash = Path.Combine(AppPaths.EngineDir, "blockcheck2", "cygwin", "bin", "bash.exe");
        return File.Exists(bash) ? bash : null;
    }

    public async Task<Result> RunAsync(IReadOnlyList<string> domains, CancellationToken ct, IProgress<string>? progress = null)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var runtime = await Task.Run(Blockcheck2Runtime.EnsureInstalled, linked.Token).ConfigureAwait(false);
        return await RunProcessAsync(CreateStartInfo(runtime, domains), runtime.Script, linked.Token, progress).ConfigureAwait(false);
    }

    internal static string ToCygwinPath(string path)
    {
        string normalized = Path.GetFullPath(path).Replace('\\', '/');
        return normalized.Length > 2 && normalized[1] == ':'
            ? "/cygdrive/" + char.ToLowerInvariant(normalized[0]) + normalized[2..] : normalized;
    }

    internal static string[] NormalizeDomains(IReadOnlyList<string> domains)
    {
        var result = domains.Select(d => new IdnMapping().GetAscii(d.Trim().TrimEnd('.')).ToLowerInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (result.Length == 0 || result.Any(d => Uri.CheckHostName(d) != UriHostNameType.Dns))
            throw new ArgumentException("Укажите домены без URL, кавычек и параметров.", nameof(domains));
        return result;
    }

    internal static ProcessStartInfo CreateStartInfo(Blockcheck2Environment runtime, IReadOnlyList<string> domains)
    {
        var normalized = NormalizeDomains(domains);
        var psi = new ProcessStartInfo
        {
            FileName = runtime.Bash,
            WorkingDirectory = runtime.Root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("--noprofile");
        psi.ArgumentList.Add("--norc");
        psi.ArgumentList.Add(ToCygwinPath(runtime.Script));
        var env = psi.Environment;
        env["PATH"] = Path.GetDirectoryName(runtime.Bash) + ";" + Path.GetDirectoryName(runtime.Curl) + ";" + Environment.SystemDirectory + ";" + Environment.GetEnvironmentVariable("PATH");
        env["HOME"] = "/home/gxbriel";
        env["BATCH"] = "1";
        env["TEST"] = "standard";
        env["DOMAINS"] = string.Join(' ', normalized);
        env["IPVS"] = "4";
        env["ENABLE_HTTP"] = env["ENABLE_HTTP3"] = "0";
        env["ENABLE_HTTPS_TLS12"] = env["ENABLE_HTTPS_TLS13"] = "1";
        env["REPEATS"] = "1";
        env["SCANLEVEL"] = "quick";
        env["SKIP_DNSCHECK"] = env["SKIP_IPBLOCK"] = "1";
        env["CURL_MAX_TIME"] = "8";
        env["CURL_HTTPS_GET"] = "1";
        env["WINWS2"] = ToCygwinPath(runtime.Winws);
        env["MDIG"] = ToCygwinPath(runtime.Mdig);
        env["CURL"] = ToCygwinPath(runtime.Curl);
        env["CURL_CA_BUNDLE"] = ToCygwinPath(Path.Combine(runtime.Root, "cygwin", "etc", "pki", "tls", "certs", "ca-bundle.crt"));
        env["ZAPRET_BASE"] = ToCygwinPath(runtime.Root);
        string session = Path.Combine(AppPaths.TempDir, "blockcheck2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(session);
        env["ZAPRET_RW"] = ToCygwinPath(session);
        foreach (string proxy in new[] { "ALL_PROXY", "all_proxy", "HTTP_PROXY", "http_proxy", "HTTPS_PROXY", "https_proxy" }) env.Remove(proxy);
        return psi;
    }

    public async Task<Result> CheckEnvironmentAsync(CancellationToken ct, IProgress<string>? progress = null)
    {
        var runtime = await Task.Run(Blockcheck2Runtime.EnsureInstalled, ct).ConfigureAwait(false);
        var psi = CreateStartInfo(runtime, new[] { "example.org" });
        psi.ArgumentList.Clear();
        psi.ArgumentList.Add("--noprofile");
        psi.ArgumentList.Add("--norc");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("set -e; uname; for tool in bash sh dirname basename grep sed awk sort uniq tr head tail xargs curl nslookup kill md5sum; do command -v $tool; done; \"$CURL\" --version; \"$MDIG\" --help >/dev/null 2>&1 || test $? -eq 1; bash -n \"$ZAPRET_BASE/blockcheck2.sh\"; test -f \"$ZAPRET_BASE/lua/zapret-lib.lua\"; test -f \"$ZAPRET_BASE/lua/zapret-antidpi.lua\"; echo BLOCKCHECK2_ENVIRONMENT_OK");
        return await RunProcessAsync(psi, runtime.Script, ct, progress).ConfigureAwait(false);
    }

    internal static async Task<Result> RunProcessAsync(ProcessStartInfo psi, string script, CancellationToken ct, IProgress<string>? progress = null)
    {
        Directory.CreateDirectory(AppPaths.LogsDir);
        string logPath = Path.Combine(AppPaths.LogsDir, $"blockcheck2-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
        using var log = new StreamWriter(logPath, false, new UTF8Encoding(false)) { AutoFlush = true };
        var output = new StringBuilder();
        var errors = new StringBuilder();
        var sync = new object();
        ct.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Start();
        ChildProcessJob? job = null;
        async Task PumpAsync(StreamReader reader, StringBuilder destination, bool isError)
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (sync)
                {
                    destination.AppendLine(line);
                    if (destination.Length > 2_000_000) destination.Remove(0, destination.Length - 1_500_000);
                    log.WriteLine((isError ? "[stderr] " : "") + line);
                }
                try { progress?.Report((isError ? "[stderr] " : "") + line); }
                catch (Exception ex) { Trace.WriteLine($"[blockcheck2] Progress subscriber: {ex.Message}"); }
            }
        }
        Task stdout = PumpAsync(process.StandardOutput, output, false);
        Task stderr = PumpAsync(process.StandardError, errors, true);
        try
        {
            job = new ChildProcessJob(process);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            job.Dispose();
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            return new(process.ExitCode, output.ToString(), errors.Length == 0 ? null : errors.ToString(), script, logPath);
        }
        finally
        {
            job?.Dispose();
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

}
