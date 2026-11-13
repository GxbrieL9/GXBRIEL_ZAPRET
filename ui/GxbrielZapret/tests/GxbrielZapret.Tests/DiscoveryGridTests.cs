using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using GxbrielZapret.Models;
using GxbrielZapret.Services.Engine;
using GxbrielZapret.Services.Infrastructure;
using GxbrielZapret.Services.Network;
using GxbrielZapret.Services.Strategies;
using Xunit;
using Xunit.Abstractions;

namespace GxbrielZapret.Tests;

public class DiscoveryGridTests
{
    private readonly ITestOutputHelper _output;

    public DiscoveryGridTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private sealed record Bundle(string Name, string[] DesyncArgs);

    [Fact]
    public async Task Run_Discovery_Grid()
    {
        var bundles = new List<Bundle>
        {
            // 1. hostfakesplit variants with various hosts and options
            new("hostfakesplit google ts md5", new[] { "--lua-desync=hostfakesplit:host=www.google.com:tcp_ts=-1000:tcp_md5:repeats=4" }),
            new("hostfakesplit vk ts md5", new[] { "--lua-desync=hostfakesplit:host=vk.com:tcp_ts=-1000:tcp_md5:repeats=4" }),
            new("hostfakesplit sber ts md5", new[] { "--lua-desync=hostfakesplit:host=sberbank.ru:tcp_ts=-1000:tcp_md5:repeats=4" }),
            new("hostfakesplit gosuslugi ts md5", new[] { "--lua-desync=hostfakesplit:host=gosuslugi.ru:tcp_ts=-1000:tcp_md5:repeats=4" }),
            new("hostfakesplit ozon md5 x6", new[] { "--lua-desync=hostfakesplit:host=ozon.ru:tcp_md5:repeats=6" }),
            new("hostfakesplit vk + midhost + disorder", new[] { "--lua-desync=hostfakesplit:host=vk.com:midhost=midsld:disorder_after:tcp_ts=-1000:tcp_md5:repeats=4" }),
            new("hostfakesplit google + wssize 1:6", new[] { "--lua-desync=hostfakesplit:host=www.google.com:tcp_ts=-1000:tcp_md5:repeats=4", "--lua-desync=wssize:wsize=1:scale=6" }),
            new("hostfakesplit vk + wssize 1:6", new[] { "--lua-desync=hostfakesplit:host=vk.com:tcp_ts=-1000:tcp_md5:repeats=4", "--lua-desync=wssize:wsize=1:scale=6" }),
            new("hostfakesplit sber + wssize 1:6", new[] { "--lua-desync=hostfakesplit:host=sberbank.ru:tcp_ts=-1000:tcp_md5:repeats=4", "--lua-desync=wssize:wsize=1:scale=6" }),
            new("hostfakesplit gosuslugi + wssize 1:6", new[] { "--lua-desync=hostfakesplit:host=gosuslugi.ru:tcp_ts=-1000:tcp_md5:repeats=4", "--lua-desync=wssize:wsize=1:scale=6" }),

            // 2. fake + multidisorder variants
            new("fake google md5 + multidisorder midsld", new[] { "--lua-desync=fake:blob=tls_google:tcp_md5:repeats=6", "--lua-desync=multidisorder:pos=1,midsld" }),
            new("fake google ts + multidisorder midsld", new[] { "--lua-desync=fake:blob=tls_google:tcp_ts=-1000:repeats=6", "--lua-desync=multidisorder:pos=1,midsld" }),
            new("fake google md5 seq + multidisorder midsld", new[] { "--lua-desync=fake:blob=tls_google:tcp_md5:tcp_seq=-10000:repeats=6", "--lua-desync=multidisorder:pos=1,midsld" }),
            new("fake google dupsid + multidisorder SNI", new[] { "--lua-desync=fake:blob=tls_google:tls_mod=rnd,dupsid:repeats=6", "--lua-desync=multidisorder:pos=1,sniext+1,host+1,midsld-2,midsld,endhost-1" }),
            new("fake vk md5 + multidisorder midsld", new[] { "--lua-desync=fake:blob=tls_vk:tcp_md5:ip_autottl=-2,3-20:repeats=6", "--lua-desync=multidisorder:pos=1,midsld" }),
            new("fake sber md5 + multidisorder midsld", new[] { "--lua-desync=fake:blob=tls_sber:tcp_md5:ip_autottl=-2,3-20:repeats=6", "--lua-desync=multidisorder:pos=1,midsld" }),
            new("fake gos md5 + multidisorder midsld", new[] { "--lua-desync=fake:blob=tls_gos:tcp_md5:ip_autottl=-2,3-20:repeats=6", "--lua-desync=multidisorder:pos=1,midsld" }),
            new("fake google md5 seq + multidisorder + wssize", new[] { "--lua-desync=fake:blob=tls_google:tcp_md5:tcp_seq=-10000:repeats=6", "--lua-desync=multidisorder:pos=1,midsld", "--lua-desync=wssize:wsize=1:scale=6" }),
            new("fake vk md5 + multidisorder + wssize", new[] { "--lua-desync=fake:blob=tls_vk:tcp_md5:repeats=6", "--lua-desync=multidisorder:pos=1,midsld", "--lua-desync=wssize:wsize=1:scale=6" }),
            new("fake sber md5 + multidisorder + wssize", new[] { "--lua-desync=fake:blob=tls_sber:tcp_md5:repeats=6", "--lua-desync=multidisorder:pos=1,midsld", "--lua-desync=wssize:wsize=1:scale=6" }),
            new("fake gos md5 + multidisorder + wssize", new[] { "--lua-desync=fake:blob=tls_gos:tcp_md5:repeats=6", "--lua-desync=multidisorder:pos=1,midsld", "--lua-desync=wssize:wsize=1:scale=6" }),

            // 3. multidisorder marker combinations
            new("multidisorder pos=1,sniext,midsld,endhost", new[] { "--lua-desync=multidisorder:pos=1,sniext,midsld,endhost" }),
            new("multidisorder pos=1,host+2,sld+2,sld+5,sniext+1,sniext+2,endhost-2", new[] { "--lua-desync=multidisorder:pos=1,host+2,sld+2,sld+5,sniext+1,sniext+2,endhost-2" }),
            new("multidisorder 88x5", new[] { "--lua-desync=multidisorder:pos=88,176,264,352,440" }),

            // 4. wssize pure and with fooling
            new("wssize 1:6 pure", new[] { "--lua-desync=wssize:wsize=1:scale=6" }),
            new("wssize 1:7 pure", new[] { "--lua-desync=wssize:wsize=1:scale=7" }),
            new("fake google ts + wssize 1:6", new[] { "--lua-desync=fake:blob=tls_google:tcp_ts=-1000:repeats=6", "--lua-desync=wssize:wsize=1:scale=6" }),
            new("fake vk ts + wssize 1:6", new[] { "--lua-desync=fake:blob=tls_vk:tcp_ts=-1000:repeats=6", "--lua-desync=wssize:wsize=1:scale=6" }),

            // 5. multisplit seqovl
            new("multisplit pos=2,midsld-2 seqovl=1 tls_google", new[] { "--lua-desync=multisplit:pos=2,midsld-2:seqovl=1:seqovl_pattern=tls_google:optional" }),
            new("multisplit pos=2,midsld-2 seqovl=1 tls_google + wssize", new[] { "--lua-desync=multisplit:pos=2,midsld-2:seqovl=1:seqovl_pattern=tls_google:optional", "--lua-desync=wssize:wsize=1:scale=6" }),
            new("multisplit pos=1,midsld seqovl=681 tls_google", new[] { "--lua-desync=multisplit:pos=1,midsld:seqovl=681:seqovl_pattern=tls_google:optional" }),
            new("multisplit pos=1,midsld seqovl=654 tls_max", new[] { "--lua-desync=multisplit:pos=1,midsld:seqovl=654:seqovl_pattern=tls_google:optional" }),

            // 6. syndata and fakeddisorder
            new("syndata + fake ack + multisplit", new[] { "--lua-desync=send:repeats=2", "--lua-desync=syndata:blob=tls_google", "--lua-desync=fake:blob=tls_google:tcp_ack=-66000:tcp_ts_up:tls_mod=rnd:repeats=2", "--lua-desync=multisplit:pos=2,midsld-2:seqovl=700:seqovl_pattern=tls_google:optional" }),
            new("fakeddisorder badseq midsld", new[] { "--lua-desync=fakeddisorder:pos=midsld:tcp_ack=-66000:tcp_ts_up:repeats=6" }),
            new("fakedsplit md5 midsld", new[] { "--lua-desync=fakedsplit:pos=midsld:tcp_md5" }),
            new("fakedsplit ts -1000", new[] { "--lua-desync=fakedsplit:tcp_ts=-1000" }),
            new("tcpseg seqovl 5 + drop", new[] { "--lua-desync=tcpseg:pos=0,-1:seqovl=5:seqovl_pattern=tls_google", "--lua-desync=drop" })
        };

        _output.WriteLine($"Evaluating {bundles.Count} candidate bundles...");

        foreach (var b in bundles)
        {
            var p = new Preset
            {
                Name = b.Name,
                Args = PresetService.BuildComboArgs(b.DesyncArgs, b.DesyncArgs, b.DesyncArgs)
            };

            var args = EngineService.BuildArguments(p, null, gameFilter: false, bypassAll: true, forLaunch: true);

            using var runner = new ProbeEngineRunner();
            try
            {
                runner.Start(args);
                await runner.WaitReadyAsync(CancellationToken.None);

                if (!runner.IsAlive)
                {
                    _output.WriteLine($"[FAIL-START] {b.Name}");
                    continue;
                }

                var yt = await ProbeAsync("https://www.youtube.com/");
                var vid = await ProbeAsync("https://redirector.googlevideo.com/report_mapping");
                var img = await ProbeAsync("https://i.ytimg.com/");
                var dc = await ProbeAsync("https://discord.com/api/v10/gateway");
                var gat = await ProbeAsync("https://gateway.discord.gg/?v=10&encoding=json");

                bool allOk = yt.ok && vid.ok && img.ok && dc.ok && gat.ok;
                string mark = allOk ? ">>> ALL-WORK <<<" : (yt.ok || dc.ok ? "PARTIAL" : "NONE");

                _output.WriteLine($"[{mark}] {b.Name} | YT:{yt.code} Vid:{vid.code} Img:{img.code} DC:{dc.code} Gat:{gat.code}");
            }
            finally
            {
                runner.Stop();
                await Task.Delay(300);
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
        psi.ArgumentList.Add("4");
        psi.ArgumentList.Add("--max-time");
        psi.ArgumentList.Add("6");
        psi.ArgumentList.Add("-w");
        psi.ArgumentList.Add("%{http_code}");
        psi.ArgumentList.Add(url);

        try
        {
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("curl did not start.");
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
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
