using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GxbrielZapret.Localization;
using GxbrielZapret.Models;
using GxbrielZapret.Services.Infrastructure;

namespace GxbrielZapret.Services.Engine;

/// <summary>
/// Performs live network endpoint capture by sampling TCP/UDP sockets
/// of active target processes (League of Legends, Riot Client, Discord, Blitz, Rockstar, custom targets, etc.)
/// using bundled Sysinternals tcpvcon, and outputs structured BlitzFix trace files.
/// </summary>
public sealed class LiveCaptureService
{
    public sealed record CapturedEndpoint(
        string processName,
        int pid,
        string protocol,
        string state,
        string localAddress,
        int localPort,
        string remoteAddress,
        int remotePort,
        string remoteKind,
        List<string> domainCandidates,
        List<string> sources,
        string? targetId = null,
        string? targetName = null
    );

    public sealed record LiveCaptureResult(
        int DurationSeconds,
        int TotalSamples,
        int DiscoveredEndpointsCount,
        int DiscoveredIpsCount,
        string OutputJsonPath,
        string SummaryMessage
    );

    public sealed record CaptureScope(
        bool Discord = true,
        bool Riot = true,
        bool Rockstar = true,
        bool Other = false,
        IReadOnlyList<DiscoveryTarget>? Targets = null)
    {
        public bool Includes(string processName, int pid = 0, DiscoveryService.ProcessTreeContext? treeCtx = null, DiscoveryService? discovery = null)
        {
            if (Targets is not null && Targets.Count > 0)
            {
                if (discovery is not null && treeCtx is not null)
                {
                    var matched = discovery.MatchTarget(processName, pid, treeCtx);
                    if (matched is not null && matched.Enabled) return true;
                }
                else
                {
                    foreach (var t in Targets.Where(x => x.Enabled))
                    {
                        if (DiscoveryService.MatchesTargetName(t, processName))
                            return true;
                    }
                }

                if (Other) return true;
                return false;
            }

            if (IsDiscordProcess(processName)) return Discord;
            if (IsRiotProcess(processName)) return Riot;
            if (IsRockstarProcess(processName)) return Rockstar;
            return Other;
        }

        public string Label
        {
            get
            {
                if (Targets is not null && Targets.Count > 0)
                {
                    var active = Targets.Where(t => t.Enabled).Select(t => t.DisplayName).ToList();
                    return active.Count == 0 ? "ничего" : string.Join(", ", active);
                }

                var names = new List<string>();
                if (Discord) names.Add("Discord");
                if (Riot) names.Add("Riot/LoL");
                if (Rockstar) names.Add("Rockstar");
                if (Other) names.Add("другие");
                return names.Count == 0 ? "ничего" : string.Join(", ", names);
            }
        }
    }

    public static string FindTcpvconExe()
    {
        string blitz = AppPaths.BlitzFixDir;
        string[] candidates =
        {
            Path.Combine(blitz, "TCPView", Environment.Is64BitOperatingSystem ? "tcpvcon64.exe" : "tcpvcon.exe"),
            Path.Combine(blitz, "TCPView", "tcpvcon.exe")
        };

        return candidates.FirstOrDefault(File.Exists) ?? "";
    }

    /// <summary>
    /// Runs a live capture for <paramref name="durationSeconds"/> (default 30-60s),
    /// capturing new sockets from running target processes and saving to BlitzFix/output.
    /// </summary>
    public async Task<LiveCaptureResult> RunLiveCaptureAsync(
        int durationSeconds = 30,
        Action<string>? progressCallback = null,
        CancellationToken ct = default,
        CaptureScope? scope = null,
        DiscoveryService? discovery = null)
    {
        scope ??= new CaptureScope();
        if (durationSeconds < 5) durationSeconds = 5;
        if (durationSeconds > 600) durationSeconds = 600;

        string outputDir = AppPaths.BlitzFixOutputDir;
        Directory.CreateDirectory(outputDir);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string jsonOut = Path.Combine(outputDir, $"apptrace-live-{stamp}-endpoints.json");
        string latestJson = Path.Combine(outputDir, "apptrace-live-latest-endpoints.json");
        string gamesJson = Path.Combine(outputDir, "apptrace-games-endpoints.json");
        string gamesCsv = Path.Combine(outputDir, "apptrace-games-endpoints.csv");
        string gamesSummary = Path.Combine(outputDir, "apptrace-games-summary.txt");

        var endpointsMap = new Dictionary<string, CapturedEndpoint>(StringComparer.OrdinalIgnoreCase);
        var uniqueIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string tcpvcon = FindTcpvconExe();
        progressCallback?.Invoke(Loc.T("Старт живой трассировки сети ({0} сек). Область: {1}.", durationSeconds, scope.Label));

        int samples = 0;
        var stopwatch = Stopwatch.StartNew();
        DiscoveryService.ProcessTreeContext? treeCtx = null;
        DateTime lastTreeRefresh = DateTime.MinValue;

        while (stopwatch.Elapsed.TotalSeconds < durationSeconds && !ct.IsCancellationRequested)
        {
            samples++;
            int remaining = Math.Max(0, durationSeconds - (int)stopwatch.Elapsed.TotalSeconds);
            int prevCount = uniqueIps.Count;

            // Refresh process tree mapping every 4 seconds to capture newly launched games/children
            if (discovery is not null && (DateTime.UtcNow - lastTreeRefresh).TotalSeconds >= 4)
            {
                try
                {
                    treeCtx = discovery.BuildProcessTreeContext();
                    lastTreeRefresh = DateTime.UtcNow;
                }
                catch { }
            }

            // 1. Try tcpvcon if available
            bool tcpvconSuccess = false;
            if (!string.IsNullOrEmpty(tcpvcon) && File.Exists(tcpvcon))
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = tcpvcon,
                        Arguments = "-accepteula -a -n -c",
                        WorkingDirectory = Path.GetDirectoryName(tcpvcon) ?? AppPaths.BlitzFixDir,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.ASCII
                    };

                    using var proc = Process.Start(psi);
                    if (proc is not null)
                    {
                        string stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
                        await proc.WaitForExitAsync(ct).ConfigureAwait(false);

                        int parsed = ParseTcpvconCsv(stdout, endpointsMap, uniqueIps, scope, discovery, treeCtx);
                        if (parsed > 0) tcpvconSuccess = true;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Fall back to native probe below
                }
            }

            if (!tcpvconSuccess)
            {
                try
                {
                    using var netstat = new Process { StartInfo = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netstat.exe"), "-ano -p tcp")
                        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
                    netstat.Start();
                    string output = await netstat.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
                    await netstat.WaitForExitAsync(ct).ConfigureAwait(false);
                    string csv = NativeNetstatCsv(output, pid => { try { using var p = Process.GetProcessById(pid); return p.ProcessName; } catch { return null; } });
                    ParseTcpvconCsv(csv, endpointsMap, uniqueIps, scope, discovery, treeCtx);
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
            // 2. Fallback / supplementary native .NET socket scan
            if (!tcpvconSuccess || uniqueIps.Count == prevCount)
            {
                if (scope.Other)
                    SampleNativeSockets(endpointsMap, uniqueIps);
            }

            progressCallback?.Invoke(Loc.T("Живой захват сети: осталось {0} сек | Найдено эндпоинтов: {1}, уникальных IP: {2}",
                remaining, endpointsMap.Count, uniqueIps.Count));

            try
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }

        // Save captured endpoints to standard JSON & CSV files
        var itemsList = endpointsMap.Values.ToList();
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        string serialized = JsonSerializer.Serialize(itemsList, jsonOptions);

        await File.WriteAllTextAsync(jsonOut, serialized, Encoding.UTF8, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(latestJson, serialized, Encoding.UTF8, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(gamesJson, serialized, Encoding.UTF8, ct).ConfigureAwait(false);

        // Build CSV
        var csvSb = new StringBuilder();
        csvSb.AppendLine("Process,PID,Protocol,State,LocalAddress,LocalPort,RemoteAddress,RemotePort,Target");
        foreach (var ep in itemsList)
        {
            csvSb.AppendLine($"\"{ep.processName}\",{ep.pid},{ep.protocol},{ep.state},{ep.localAddress},{ep.localPort},{ep.remoteAddress},{ep.remotePort},\"{ep.targetName ?? ""}\"");
        }
        await File.WriteAllTextAsync(gamesCsv, csvSb.ToString(), Encoding.UTF8, ct).ConfigureAwait(false);

        int newCandidates = 0;
        if (discovery is not null && itemsList.Count > 0)
        {
            try
            {
                newCandidates = discovery.IngestCapturedEndpoints(itemsList);
            }
            catch { }
        }

        // Build Summary text
        string summary = newCandidates > 0
            ? Loc.T("Живой захват сети завершён ({0} сек): перехвачено {1} эндпоинтов и {2} публичных IP-адресов. Новых кандидатов: {3}.",
                (int)stopwatch.Elapsed.TotalSeconds, itemsList.Count, uniqueIps.Count, newCandidates)
            : Loc.T("Живой захват сети завершён ({0} сек): перехвачено {1} эндпоинтов и {2} публичных IP-адресов.",
                (int)stopwatch.Elapsed.TotalSeconds, itemsList.Count, uniqueIps.Count);

        var sumSb = new StringBuilder();
        sumSb.AppendLine($"Capture time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sumSb.AppendLine($"Duration: {(int)stopwatch.Elapsed.TotalSeconds} seconds");
        sumSb.AppendLine($"Total endpoints: {itemsList.Count}");
        sumSb.AppendLine($"Unique public IPs: {uniqueIps.Count}");
        sumSb.AppendLine($"New candidates: {newCandidates}");
        sumSb.AppendLine("IPs:");
        foreach (var ip in uniqueIps.OrderBy(x => x)) sumSb.AppendLine($"  {ip}");
        await File.WriteAllTextAsync(gamesSummary, sumSb.ToString(), Encoding.UTF8, ct).ConfigureAwait(false);

        return new LiveCaptureResult(
            (int)stopwatch.Elapsed.TotalSeconds,
            samples,
            itemsList.Count,
            uniqueIps.Count,
            jsonOut,
            summary
        );
    }

    private static int ParseTcpvconCsv(
        string csvOutput,
        Dictionary<string, CapturedEndpoint> endpointsMap,
        HashSet<string> uniqueIps,
        CaptureScope scope,
        DiscoveryService? discovery = null,
        DiscoveryService.ProcessTreeContext? treeCtx = null)
    {
        if (string.IsNullOrWhiteSpace(csvOutput)) return 0;

        int added = 0;
        using var reader = new StringReader(csvOutput);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            string clean = line.Trim();
            if (clean.Length == 0 || clean.StartsWith('#') || clean.StartsWith("Process", StringComparison.OrdinalIgnoreCase)
                || clean.StartsWith("Tcpvcon", StringComparison.OrdinalIgnoreCase) || clean.StartsWith("Copyright", StringComparison.OrdinalIgnoreCase)
                || clean.StartsWith("Sysinternals", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = clean.Split(',');
            if (parts.Length < 6) continue;

            // tcpvcon has existed in both orders depending on build/version:
            //   old: Protocol, Process, PID, State, Local, Remote
            //   current TCPView CSV: Process Name, Process ID, Protocol, State, Local Address, Local Port, Remote Address, Remote Port
            string protocol;
            string process;
            string pidStr;
            string state;
            string local;
            string remote;

            string first = parts[0].Trim().Trim('"');
            string third = parts.Length > 2 ? parts[2].Trim().Trim('"') : "";
            if (first.Equals("TCP", StringComparison.OrdinalIgnoreCase) || first.Equals("UDP", StringComparison.OrdinalIgnoreCase))
            {
                protocol = first;
                process = parts[1].Trim().Trim('"');
                pidStr = third;
                state = parts[3].Trim().Trim('"');
                local = parts[4].Trim().Trim('"');
                remote = parts[5].Trim().Trim('"');
            }
            else if (third.Equals("TCP", StringComparison.OrdinalIgnoreCase) || third.Equals("UDP", StringComparison.OrdinalIgnoreCase))
            {
                process = first;
                pidStr = parts[1].Trim().Trim('"');
                protocol = third;
                state = parts[3].Trim().Trim('"');
                string localAddress = parts[4].Trim().Trim('"');
                string localPortText = parts.Length > 5 ? parts[5].Trim().Trim('"') : "";
                string remoteAddress = parts.Length > 6 ? parts[6].Trim().Trim('"') : "";
                string remotePortText = parts.Length > 7 ? parts[7].Trim().Trim('"') : "";
                local = string.IsNullOrWhiteSpace(localPortText) ? localAddress : $"{localAddress}:{localPortText}";
                remote = string.IsNullOrWhiteSpace(remotePortText) || remotePortText.Equals("*", StringComparison.Ordinal)
                    ? remoteAddress
                    : $"{remoteAddress}:{remotePortText}";
            }
            else
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(process) || process.Equals("System", StringComparison.OrdinalIgnoreCase) || process.Equals("[System Process]", StringComparison.OrdinalIgnoreCase))
                continue;

            int.TryParse(pidStr, out int pid);

            if (!scope.Includes(process, pid, treeCtx, discovery))
                continue;

            var matchedTarget = discovery?.MatchTarget(process, pid, treeCtx);

            string remoteIp = remote;
            int remotePort = 0;
            int colonIdx = remote.LastIndexOf(':');
            if (colonIdx > 0 && int.TryParse(remote[(colonIdx + 1)..], out int rPort))
            {
                remoteIp = remote[..colonIdx];
                remotePort = rPort;
            }

            string localIp = local;
            int localPort = 0;
            int lColon = local.LastIndexOf(':');
            if (lColon > 0 && int.TryParse(local[(lColon + 1)..], out int lPort))
            {
                localIp = local[..lColon];
                localPort = lPort;
            }

            if (!HostlistService.TestPublicIPv4(remoteIp))
                continue;

            string key = $"{process}_{protocol}_{remoteIp}_{remotePort}";
            uniqueIps.Add(remoteIp);

            if (!endpointsMap.ContainsKey(key))
            {
                added++;
                var domainCandidates = new List<string>();
                if (matchedTarget is not null && matchedTarget.KnownDomains.Count > 0)
                {
                    domainCandidates.AddRange(matchedTarget.KnownDomains);
                }

                endpointsMap[key] = new CapturedEndpoint(
                    processName: process,
                    pid: pid,
                    protocol: protocol,
                    state: state,
                    localAddress: localIp,
                    localPort: localPort,
                    remoteAddress: remoteIp,
                    remotePort: remotePort,
                    remoteKind: "ip",
                    domainCandidates: domainCandidates,
                    sources: new List<string> { "live_trace" },
                    targetId: matchedTarget?.Id,
                    targetName: matchedTarget?.DisplayName
                );
            }
        }

        return added;
    }

    internal static string NativeNetstatCsv(string output, Func<int, string?> processName)
    {
        var result = new StringBuilder();
        foreach (string line in output.Split('\n'))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 5 || fields[0] != "TCP" || !int.TryParse(fields[4], out int pid)) continue;
            string? name = processName(pid);
            if (string.IsNullOrWhiteSpace(name)) continue;
            result.AppendLine($"TCP,{name.Replace(',', '_')},{pid},{fields[3]},{fields[1]},{fields[2]}");
        }
        return result.ToString();
    }

    public static bool IsDiscordProcess(string processName) =>
        processName.Contains("Discord", StringComparison.OrdinalIgnoreCase);

    public static bool IsRiotProcess(string processName) =>
        processName.Contains("League", StringComparison.OrdinalIgnoreCase) ||
        processName.Contains("Riot", StringComparison.OrdinalIgnoreCase) ||
        processName.Contains("Blitz", StringComparison.OrdinalIgnoreCase) ||
        processName.Contains("Porofessor", StringComparison.OrdinalIgnoreCase) ||
        processName.Contains("Valorant", StringComparison.OrdinalIgnoreCase);

    public static bool IsRockstarProcess(string processName) =>
        processName.Contains("Rockstar", StringComparison.OrdinalIgnoreCase) ||
        processName.Contains("GTA", StringComparison.OrdinalIgnoreCase) ||
        processName.Contains("LauncherPatcher", StringComparison.OrdinalIgnoreCase);

    private static void SampleNativeSockets(
        Dictionary<string, CapturedEndpoint> endpointsMap,
        HashSet<string> uniqueIps)
    {
        try
        {
            var properties = IPGlobalProperties.GetIPGlobalProperties();
            var tcpConnections = properties.GetActiveTcpConnections();

            foreach (var conn in tcpConnections)
            {
                if (conn.RemoteEndPoint.AddressFamily != AddressFamily.InterNetwork) continue;

                string remoteIp = conn.RemoteEndPoint.Address.ToString();
                if (!HostlistService.TestPublicIPv4(remoteIp)) continue;

                int remotePort = conn.RemoteEndPoint.Port;
                string localIp = conn.LocalEndPoint.Address.ToString();
                int localPort = conn.LocalEndPoint.Port;
                string state = conn.State.ToString();

                string key = $"native_TCP_{remoteIp}_{remotePort}";
                uniqueIps.Add(remoteIp);

                if (!endpointsMap.ContainsKey(key))
                {
                    endpointsMap[key] = new CapturedEndpoint(
                        processName: "native_socket",
                        pid: 0,
                        protocol: "TCP",
                        state: state,
                        localAddress: localIp,
                        localPort: localPort,
                        remoteAddress: remoteIp,
                        remotePort: remotePort,
                        remoteKind: "ip",
                        domainCandidates: new List<string>(),
                        sources: new List<string> { "native_tcp_scan" }
                    );
                }
            }
        }
        catch
        {
            // Ignore socket scan errors
        }
    }
}
