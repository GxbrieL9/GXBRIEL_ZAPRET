using System.IO;
using GxbrielZapret.Models;
using GxbrielZapret.Services.Engine;
using GxbrielZapret.Services.Infrastructure;
using Xunit;

namespace GxbrielZapret.Tests;

public sealed class SteamRoutingTests
{
    [Fact]
    public void EngineArguments_AddTargetedSteamTlsBypass()
    {
        using var scope = new TemporaryRuntime();
        var hostlists = new HostlistService();
        var preset = new Preset { Name = "Steam routing test", Args = new List<string>() };

        var args = EngineService.BuildArguments(preset, null, forLaunch: true);

        Assert.Contains(args, a => a.StartsWith("--hostlist=", StringComparison.Ordinal) && a.Contains("steam", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("--lua-desync=multisplit:pos=2", args);
        int steamHostlistIndex = args.FindIndex(a => a.Contains("list-steam", StringComparison.OrdinalIgnoreCase));
        Assert.True(steamHostlistIndex >= 2);
        Assert.Equal("--filter-l7=tls", args[steamHostlistIndex - 1]);
        Assert.Equal("--payload=tls_client_hello", args[steamHostlistIndex + 1]);
        Assert.Equal("--lua-desync=multisplit:pos=2", args[steamHostlistIndex + 2]);
        int steamQuicHostlistIndex = args.FindIndex(steamHostlistIndex + 1, a => a.Contains("list-steam", StringComparison.OrdinalIgnoreCase));
        Assert.True(steamQuicHostlistIndex >= 2);
        Assert.Equal("--filter-l7=quic", args[steamQuicHostlistIndex - 1]);
        Assert.Equal("--payload=quic_initial", args[steamQuicHostlistIndex + 1]);
        Assert.Equal("--lua-desync=drop", args[steamQuicHostlistIndex + 2]);
        string steamList = File.ReadAllText(Path.Combine(AppPaths.ListsDir, "list-steam.txt"));
        Assert.True(steamList.Contains("steamcommunity.com", StringComparison.OrdinalIgnoreCase));
        Assert.True(steamList.Contains("steampowered.com", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class TemporaryRuntime : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "gx-steam-routing-" + Guid.NewGuid().ToString("N"));
        private readonly string? _oldProgramData = Environment.GetEnvironmentVariable("GXBRIEL_ZAPRET_PROGRAMDATA_ROOT");
        private readonly string? _oldAppData = Environment.GetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT");

        public TemporaryRuntime()
        {
            Directory.CreateDirectory(_root);
            Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_PROGRAMDATA_ROOT", _root);
            Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT", _root);
            AppPaths.ResetSystemRuntimeDirCache();
            AppPaths.ResetEngineDirCache();
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_PROGRAMDATA_ROOT", _oldProgramData);
            Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT", _oldAppData);
            AppPaths.ResetSystemRuntimeDirCache();
            AppPaths.ResetEngineDirCache();
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }
}
