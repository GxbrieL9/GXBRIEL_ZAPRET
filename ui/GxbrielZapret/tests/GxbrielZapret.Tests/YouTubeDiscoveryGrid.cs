using System.Diagnostics;
using System.IO;
using System.Text;
using GxbrielZapret.Models;
using GxbrielZapret.Services.Engine;
using GxbrielZapret.Services.Strategies;
using Xunit;
using Xunit.Abstractions;

namespace GxbrielZapret.Tests;

public class YouTubeDiscoveryGrid
{
    private readonly ITestOutputHelper _output;

    public YouTubeDiscoveryGrid(ITestOutputHelper output)
    {
        _output = output;
    }

    private sealed record TestCandidate(string Name, string[] DesyncArgs);

    [Fact]
    public async Task Discover_YouTube_Desync()
    {
        string logPath = Path.Combine(Path.GetTempPath(), "gxbriel-discovery-live.log");
        await File.WriteAllTextAsync(logPath, $"=== DISCOVERY ROUND 2 STARTED {DateTime.Now:O} ===\n");

        var candidates = new List<TestCandidate>
        {
            // 1. hostfakesplit with midhost and disorder_after
            new("HFS google midhost=midsld", new[] { "--lua-desync=hostfakesplit:host=www.google.com:midhost=midsld:tcp_ts=-1000:tcp_md5:repeats=4" }),
            new("HFS vk midhost=midsld", new[] { "--lua-desync=hostfakesplit:host=vk.com:midhost=midsld:tcp_ts=-1000:tcp_md5:repeats=4" }),
            new("HFS sber midhost=midsld", new[] { "--lua-desync=hostfakesplit:host=sberbank.ru:midhost=midsld:tcp_ts=-1000:tcp_md5:repeats=4" }),
            new("HFS gos midhost=midsld", new[] { "--lua-desync=hostfakesplit:host=gosuslugi.ru:midhost=midsld:tcp_ts=-1000:tcp_md5:repeats=4" }),
            new("HFS ozon midhost=midsld", new[] { "--lua-desync=hostfakesplit:host=ozon.ru:midhost=midsld:tcp_ts=-1000:tcp_md5:repeats=4" }),
            new("HFS google midhost disorder_after", new[] { "--lua-desync=hostfakesplit:host=www.google.com:midhost=midsld:disorder_after:tcp_ts=-1000:tcp_md5:repeats=4" }),
            new("HFS vk midhost disorder_after", new[] { "--lua-desync=hostfakesplit:host=vk.com:midhost=midsld:disorder_after:tcp_ts=-1000:tcp_md5:repeats=4" }),
            new("HFS sber midhost disorder_after", new[] { "--lua-desync=hostfakesplit:host=sberbank.ru:midhost=midsld:disorder_after:tcp_ts=-1000:tcp_md5:repeats=4" }),
            new("HFS gos midhost disorder_after", new[] { "--lua-desync=hostfakesplit:host=gosuslugi.ru:midhost=midsld:disorder_after:tcp_ts=-1000:tcp_md5:repeats=4" }),
            new("HFS ozon midhost disorder_after", new[] { "--lua-desync=hostfakesplit:host=ozon.ru:midhost=midsld:disorder_after:tcp_ts=-1000:tcp_md5:repeats=4" }),

            // 2. Pure multidisorder and multisplit pos patterns
            new("multidisorder pos=1,midsld", new[] { "--lua-desync=multidisorder:pos=1,midsld" }),
            new("multidisorder pos=1,sniext,midsld,endhost", new[] { "--lua-desync=multidisorder:pos=1,sniext,midsld,endhost" }),
            new("multidisorder pos=1,sniext+1,host+1,midsld-2,midsld,endhost-1", new[] { "--lua-desync=multidisorder:pos=1,sniext+1,host+1,midsld-2,midsld,endhost-1" }),
            new("multidisorder pos=1,host+2,sld+2,sld+5,sniext+1,sniext+2,endhost-2", new[] { "--lua-desync=multidisorder:pos=1,host+2,sld+2,sld+5,sniext+1,sniext+2,endhost-2" }),
            new("multidisorder 88x5", new[] { "--lua-desync=multidisorder:pos=88,176,264,352,440" }),
            new("multidisorder 1..5", new[] { "--lua-desync=multidisorder:pos=1,2,3,4,5" }),
            new("multisplit pos=1,midsld", new[] { "--lua-desync=multisplit:pos=1,midsld" }),
            new("multisplit pos=1,sniext,midsld,endhost", new[] { "--lua-desync=multisplit:pos=1,sniext,midsld,endhost" }),
            new("multisplit pos=1,sniext+1,host+1,midsld-2,midsld,endhost-1", new[] { "--lua-desync=multisplit:pos=1,sniext+1,host+1,midsld-2,midsld,endhost-1" }),
            new("multisplit pos=1,host+2,sld+2,sld+5,sniext+1,sniext+2,endhost-2", new[] { "--lua-desync=multisplit:pos=1,host+2,sld+2,sld+5,sniext+1,sniext+2,endhost-2" }),

            // 3. wssize combinations with multidisorder / multisplit / HFS
            new("wssize 1:6 + multidisorder pos=1,midsld", new[] { "--lua-desync=wssize:wsize=1:scale=6", "--lua-desync=multidisorder:pos=1,midsld" }),
            new("wssize 1:6 + multidisorder pos=1,sniext,midsld,endhost", new[] { "--lua-desync=wssize:wsize=1:scale=6", "--lua-desync=multidisorder:pos=1,sniext,midsld,endhost" }),
            new("wssize 1:6 + multisplit pos=1,midsld", new[] { "--lua-desync=wssize:wsize=1:scale=6", "--lua-desync=multisplit:pos=1,midsld" }),
            new("wssize 1:6 + multisplit pos=1,sniext,midsld,endhost", new[] { "--lua-desync=wssize:wsize=1:scale=6", "--lua-desync=multisplit:pos=1,sniext,midsld,endhost" }),
            new("wssize 1:6 + HFS vk midhost", new[] { "--lua-desync=wssize:wsize=1:scale=6", "--lua-desync=hostfakesplit:host=vk.com:midhost=midsld:tcp_ts=-1000:tcp_md5:repeats=4" }),
            new("wssize 1:6 + HFS google midhost", new[] { "--lua-desync=wssize:wsize=1:scale=6", "--lua-desync=hostfakesplit:host=www.google.com:midhost=midsld:tcp_ts=-1000:tcp_md5:repeats=4" }),
            new("wssize 1:6 + HFS sber midhost", new[] { "--lua-desync=wssize:wsize=1:scale=6", "--lua-desync=hostfakesplit:host=sberbank.ru:midhost=midsld:tcp_ts=-1000:tcp_md5:repeats=4" }),
            new("wssize 1:6 + HFS gos midhost", new[] { "--lua-desync=wssize:wsize=1:scale=6", "--lua-desync=hostfakesplit:host=gosuslugi.ru:midhost=midsld:tcp_ts=-1000:tcp_md5:repeats=4" }),
            new("wssize 1:6 + HFS ozon midhost", new[] { "--lua-desync=wssize:wsize=1:scale=6", "--lua-desync=hostfakesplit:host=ozon.ru:midhost=midsld:tcp_ts=-1000:tcp_md5:repeats=4" }),
            new("wssize 1:6 + HFS vk midhost disorder_after", new[] { "--lua-desync=wssize:wsize=1:scale=6", "--lua-desync=hostfakesplit:host=vk.com:midhost=midsld:disorder_after:tcp_ts=-1000:tcp_md5:repeats=4" }),
            new("wssize 1:6 + HFS google midhost disorder_after", new[] { "--lua-desync=wssize:wsize=1:scale=6", "--lua-desync=hostfakesplit:host=www.google.com:midhost=midsld:disorder_after:tcp_ts=-1000:tcp_md5:repeats=4" }),
            new("wssize 1:6 + HFS sber midhost disorder_after", new[] { "--lua-desync=wssize:wsize=1:scale=6", "--lua-desync=hostfakesplit:host=sberbank.ru:midhost=midsld:disorder_after:tcp_ts=-1000:tcp_md5:repeats=4" }),
            new("wssize 1:6 + HFS gos midhost disorder_after", new[] { "--lua-desync=wssize:wsize=1:scale=6", "--lua-desync=hostfakesplit:host=gosuslugi.ru:midhost=midsld:disorder_after:tcp_ts=-1000:tcp_md5:repeats=4" }),
            new("wssize 1:6 + HFS ozon midhost disorder_after", new[] { "--lua-desync=wssize:wsize=1:scale=6", "--lua-desync=hostfakesplit:host=ozon.ru:midhost=midsld:disorder_after:tcp_ts=-1000:tcp_md5:repeats=4" }),

            // 4. IP TTL scans with fake google + multisplit
            new("fake google ttl=1 + multisplit pos=2,midsld seqovl", new[] { "--lua-desync=fake:blob=tls_google:ip_ttl=1:repeats=6", "--lua-desync=multisplit:pos=2,midsld:seqovl=681:seqovl_pattern=tls_google:optional" }),
            new("fake google ttl=2 + multisplit pos=2,midsld seqovl", new[] { "--lua-desync=fake:blob=tls_google:ip_ttl=2:repeats=6", "--lua-desync=multisplit:pos=2,midsld:seqovl=681:seqovl_pattern=tls_google:optional" }),
            new("fake google ttl=3 + multisplit pos=2,midsld seqovl", new[] { "--lua-desync=fake:blob=tls_google:ip_ttl=3:repeats=6", "--lua-desync=multisplit:pos=2,midsld:seqovl=681:seqovl_pattern=tls_google:optional" }),
            new("fake google ttl=4 + multisplit pos=2,midsld seqovl", new[] { "--lua-desync=fake:blob=tls_google:ip_ttl=4:repeats=6", "--lua-desync=multisplit:pos=2,midsld:seqovl=681:seqovl_pattern=tls_google:optional" }),
            new("fake google ttl=5 + multisplit pos=2,midsld seqovl", new[] { "--lua-desync=fake:blob=tls_google:ip_ttl=5:repeats=6", "--lua-desync=multisplit:pos=2,midsld:seqovl=681:seqovl_pattern=tls_google:optional" }),
            new("fake google ttl=6 + multisplit pos=2,midsld seqovl", new[] { "--lua-desync=fake:blob=tls_google:ip_ttl=6:repeats=6", "--lua-desync=multisplit:pos=2,midsld:seqovl=681:seqovl_pattern=tls_google:optional" }),
            new("fake google ttl=7 + multisplit pos=2,midsld seqovl", new[] { "--lua-desync=fake:blob=tls_google:ip_ttl=7:repeats=6", "--lua-desync=multisplit:pos=2,midsld:seqovl=681:seqovl_pattern=tls_google:optional" }),
            new("fake google ttl=8 + multisplit pos=2,midsld seqovl", new[] { "--lua-desync=fake:blob=tls_google:ip_ttl=8:repeats=6", "--lua-desync=multisplit:pos=2,midsld:seqovl=681:seqovl_pattern=tls_google:optional" }),
            new("fake google ttl=9 + multisplit pos=2,midsld seqovl", new[] { "--lua-desync=fake:blob=tls_google:ip_ttl=9:repeats=6", "--lua-desync=multisplit:pos=2,midsld:seqovl=681:seqovl_pattern=tls_google:optional" }),
            new("fake google ttl=10 + multisplit pos=2,midsld seqovl", new[] { "--lua-desync=fake:blob=tls_google:ip_ttl=10:repeats=6", "--lua-desync=multisplit:pos=2,midsld:seqovl=681:seqovl_pattern=tls_google:optional" }),
            new("fake google ttl=11 + multisplit pos=2,midsld seqovl", new[] { "--lua-desync=fake:blob=tls_google:ip_ttl=11:repeats=6", "--lua-desync=multisplit:pos=2,midsld:seqovl=681:seqovl_pattern=tls_google:optional" }),
            new("fake google ttl=12 + multisplit pos=2,midsld seqovl", new[] { "--lua-desync=fake:blob=tls_google:ip_ttl=12:repeats=6", "--lua-desync=multisplit:pos=2,midsld:seqovl=681:seqovl_pattern=tls_google:optional" }),

            // 5. IP frag
            new("ipfrag pos=32", new[] { "--lua-desync=send:ipfrag2:ipfrag_pos_tcp=32" }),
            new("ipfrag pos=24", new[] { "--lua-desync=send:ipfrag2:ipfrag_pos_tcp=24" }),
            new("ipfrag pos=32 disorder", new[] { "--lua-desync=send:ipfrag2:ipfrag_pos_tcp=32:ipfrag_disorder" }),

            // 6. oob
            new("oob pos=1", new[] { "--lua-desync=oob:pos=1" }),
            new("oob pos=2", new[] { "--lua-desync=oob:pos=2" }),
            new("oob pos=midsld", new[] { "--lua-desync=oob:pos=midsld" })
        };

        _output.WriteLine($"Evaluating {candidates.Count} candidate bundles in Round 2...");

        foreach (var c in candidates)
        {
            var p = new Preset
            {
                Name = c.Name,
                Args = PresetService.BuildComboArgs(c.DesyncArgs, c.DesyncArgs, c.DesyncArgs)
            };

            var args = EngineService.BuildArguments(p, null, gameFilter: false, bypassAll: true, forLaunch: true);

            using var runner = new ProbeEngineRunner();
            try
            {
                runner.Start(args);
                await runner.WaitReadyAsync(CancellationToken.None);

                if (!runner.IsAlive)
                {
                    string failMsg = $"[FAIL-START] {c.Name}";
                    _output.WriteLine(failMsg);
                    await File.AppendAllTextAsync(logPath, failMsg + "\n");
                    continue;
                }

                var yt = await ProbeAsync("https://www.youtube.com/");
                var vid = await ProbeAsync("https://redirector.googlevideo.com/report_mapping");
                var img = await ProbeAsync("https://i.ytimg.com/");
                var dc = await ProbeAsync("https://discord.com/api/v10/gateway");
                var gat = await ProbeAsync("https://gateway.discord.gg/?v=10&encoding=json");

                bool all5 = yt.ok && vid.ok && img.ok && dc.ok && gat.ok;
                string mark = all5 ? "*** ALL-5-WORK ***" : (yt.ok || vid.ok || dc.ok || gat.ok ? "PARTIAL" : "NONE");
                string line = $"[{mark}] {c.Name} => YT:{yt.code} Vid:{vid.code} Img:{img.code} DC:{dc.code} Gat:{gat.code}";

                _output.WriteLine(line);
                await File.AppendAllTextAsync(logPath, line + "\n");
            }
            finally
            {
                runner.Stop();
                await Task.Delay(200);
            }
        }
    }

    private static async Task<(bool ok, string code)> ProbeAsync(string url)
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
        psi.ArgumentList.Add("2");
        psi.ArgumentList.Add("--max-time");
        psi.ArgumentList.Add("4");
        psi.ArgumentList.Add("-w");
        psi.ArgumentList.Add("%{http_code}");
        psi.ArgumentList.Add(url);

        try
        {
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("curl did not start.");
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await proc.WaitForExitAsync(cts.Token);
            string code = (await stdoutTask).Trim();
            bool ok = proc.ExitCode == 0 && code.Length >= 3 && code != "000";
            return (ok, code);
        }
        catch
        {
            return (false, "000");
        }
    }
}
