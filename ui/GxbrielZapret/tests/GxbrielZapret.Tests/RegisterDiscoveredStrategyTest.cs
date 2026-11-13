using System.IO;
using GxbrielZapret.Models;
using GxbrielZapret.Services.Engine;
using GxbrielZapret.Services.Infrastructure;
using GxbrielZapret.Services.Strategies;
using Xunit;
using Xunit.Abstractions;

namespace GxbrielZapret.Tests;

public class RegisterDiscoveredStrategyTest
{
    private readonly ITestOutputHelper _output;

    public RegisterDiscoveredStrategyTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Add_Discovered_Strategy_As_New_Candidate()
    {
        var presetService = new PresetService();
        int initialRefCount = presetService.ReferenceStrategies.Count;
        Assert.Equal(47, initialRefCount);

        var desyncArgs = new[] { "--lua-desync=hostfakesplit:host=www.google.com:tcp_ts=-1000:tcp_md5:repeats=4" };
        var comboArgs = PresetService.BuildComboArgs(desyncArgs, desyncArgs, desyncArgs);

        var discoveredPreset = new Preset
        {
            Name = "HostFakeSplit Google (Current Network Discovery)",
            Description = "Discovered working combo on current network: hostfakesplit google TLS desync for Discord & YouTube + QUIC fake + Discord Voice anti-drop",
            Tagline = "Current-Network Discovered Strategy (All 5 Probes OK)",
            IsBuiltIn = false,
            Status = "WORK",
            Reason = "All 5 mandatory probes passed (YouTube: 200, googlevideo: 200, ytimg: 404, Discord API: 200, Discord Gateway: 404)",
            Args = comboArgs
        };

        string fp = CanonicalProfile.ComputeFingerprint(comboArgs);
        var existing = presetService.All.FirstOrDefault(x => string.Equals(x.Fingerprint, fp, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            _output.WriteLine($"Matches existing preset: Name='{existing.Name}', RefId='{existing.ReferenceId}', Status='{existing.Status}', IsBuiltIn={existing.IsBuiltIn}");
        }

        presetService.AddUser(discoveredPreset);

        // Verify that reference strategies are still exactly 39
        Assert.Equal(47, presetService.ReferenceStrategies.Count);

        var fullArgs = EngineService.BuildArguments(discoveredPreset, null, gameFilter: false, bypassAll: true, forLaunch: false);
        _output.WriteLine("=== REGISTERED DISCOVERED STRATEGY ===");
        _output.WriteLine($"Name: {discoveredPreset.Name}");
        _output.WriteLine($"Fingerprint: {discoveredPreset.Fingerprint}");
        _output.WriteLine($"Status: {discoveredPreset.Status}");
        _output.WriteLine($"Winws2 Full Command Line:\nwinws2.exe {string.Join(" ", fullArgs)}");
    }
}
