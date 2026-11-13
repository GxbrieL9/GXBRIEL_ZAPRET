using System;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GxbrielZapret.Models;
using GxbrielZapret.Services.Engine;
using GxbrielZapret.Services.Infrastructure;
using GxbrielZapret.Services.Network;
using GxbrielZapret.Services.Strategies;
using Xunit;
using Xunit.Abstractions;

namespace GxbrielZapret.Tests;

public class SingleCandidateDiffTest
{
    private readonly ITestOutputHelper _output;

    public SingleCandidateDiffTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Run_Single_Candidate_CaseB()
    {
        var s = ReferenceStrategyCatalog.FindById("GX-B04");
        Assert.NotNull(s);

        var preset = s.ToPreset();
        var args = EngineService.BuildArguments(preset, null, gameFilter: false, bypassAll: true, forLaunch: true);
        string exe = AppPaths.WinwsExe;
        string cwd = AppPaths.EngineDir;
        string cmd = $"\"{exe}\" {string.Join(" ", args)}";

        var sw = Stopwatch.StartNew();
        using var runner = new ProbeEngineRunner();
        runner.Start(args);
        int pid = runner.ProcessId;
        await runner.WaitReadyAsync(CancellationToken.None);
        sw.Stop();
        long readyMs = sw.ElapsedMilliseconds;

        var yt = await ProbeAsync("https://www.youtube.com/");
        var vid = await ProbeAsync("https://redirector.googlevideo.com/report_mapping");
        var img = await ProbeAsync("https://i.ytimg.com/");
        var dc = await ProbeAsync("https://discord.com/api/v10/gateway");
        var gat = await ProbeAsync("https://gateway.discord.gg/?v=10&encoding=json");

        runner.Stop();

        var result = new
        {
            Case = "B (Stage E Core winws2)",
            Executable = exe,
            PID = pid,
            Cwd = cwd,
            ReadinessMs = readyMs,
            CommandLine = cmd,
            Probes = new[] { yt, vid, img, dc, gat }
        };

        _output.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task<object> ProbeAsync(string url)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "curl.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        psi.ArgumentList.Add("-4");
        psi.ArgumentList.Add("--noproxy");
        psi.ArgumentList.Add("*");
        psi.ArgumentList.Add("-L");
        psi.ArgumentList.Add("-sS");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("NUL");
        psi.ArgumentList.Add("--connect-timeout");
        psi.ArgumentList.Add("5");
        psi.ArgumentList.Add("--max-time");
        psi.ArgumentList.Add("7");
        psi.ArgumentList.Add("-w");
        psi.ArgumentList.Add("%{http_code}");
        psi.ArgumentList.Add(url);

        try
        {
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("curl did not start.");
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(9));
            await proc.WaitForExitAsync(cts.Token);
            string code = (await stdoutTask).Trim();
            return new { Url = url, HttpCode = code, ExitCode = proc.ExitCode };
        }
        catch (Exception ex)
        {
            return new { Url = url, HttpCode = "000", ExitCode = -1, Error = ex.Message };
        }
    }
}
