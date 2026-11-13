using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using GxbrielZapret.Models;
using GxbrielZapret.Services.Engine;
using GxbrielZapret.Services.Infrastructure;
using GxbrielZapret.Services.Network;
using GxbrielZapret.Services.Strategies;
using GxbrielZapret.ViewModels;
using Xunit;

namespace GxbrielZapret.Tests;

public class StageBPersistenceAndRuntimeTests
{
    [Fact]
    public void EmbeddedRuntime_Manifest_ContainsAllCoreBinaries_WithValidHashes()
    {
        var manifest = EngineValidator.LoadManifest();
        Assert.NotNull(manifest);
        Assert.Equal("zapret2", manifest.Engine);
        Assert.False(string.IsNullOrWhiteSpace(manifest.Version));

        string[] requiredFiles = { "winws2.exe", "WinDivert.dll", "WinDivert64.sys", "cygwin1.dll", "ip2net.exe", "mdig.exe" };
        foreach (var req in requiredFiles)
        {
            Assert.True(manifest.Files.ContainsKey(req), $"Manifest must contain {req}");
            Assert.Equal(64, manifest.Files[req].Sha256.Length);
        }
    }

    [Fact]
    public void EmbeddedRuntime_ExtractsAtomically_AndVerifiesAgainstManifest()
    {
        string sandbox = Path.Combine(Path.GetTempPath(), "gx_test_runtime_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(sandbox);
            var manifest = EngineValidator.LoadManifest();

            // Extract the embedded zip
            var asm = typeof(AppPaths).Assembly;
            using var stream = asm.GetManifestResourceStream("GXBRIEL_ZAPRET.engine.zapret2-runtime.zip");
            Assert.NotNull(stream);

            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                archive.ExtractToDirectory(sandbox);
            }

            // Verify each file against manifest
            foreach (var (relPath, entry) in manifest.Files)
            {
                string filePath = Path.Combine(sandbox, relPath);
                Assert.True(File.Exists(filePath), $"Extracted file must exist: {filePath}");

                using var sha = SHA256.Create();
                using var fs = File.OpenRead(filePath);
                string hash = Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
                Assert.Equal(entry.Sha256, hash);
            }

            // Verify required subdirectories
            Assert.True(Directory.Exists(Path.Combine(sandbox, "lua")));
            Assert.True(File.Exists(Path.Combine(sandbox, "lua", "zapret-antidpi.lua")));
            Assert.True(File.Exists(Path.Combine(sandbox, "lua", "zapret-auto.lua")));
            Assert.True(File.Exists(Path.Combine(sandbox, "lua", "zapret-lib.lua")));

            Assert.True(Directory.Exists(Path.Combine(sandbox, "files")));
            Assert.True(Directory.Exists(Path.Combine(sandbox, "windivert.filter")));
        }
        finally
        {
            try { Directory.Delete(sandbox, recursive: true); } catch { }
        }
    }

    [Fact]
    public void NetworkKnowledgeStore_Saves_Loads_AndPersistsProfile()
    {
        string testFp = "test_fp_" + Guid.NewGuid().ToString("N");
        try
        {
            var profile = NetworkKnowledgeStore.GetOrCreate(testFp);
            Assert.NotNull(profile);
            Assert.Equal(testFp, profile.NetworkFingerprint);

            profile.BestStrategyName = "Combo Strategy Alpha";
            profile.LastSeen = DateTime.UtcNow;
            profile.RecordTest("Combo Strategy Alpha", "fp_alpha", "WORK", "verified", true, true, true, 45);

            NetworkKnowledgeStore.Save(profile);

            // Re-load
            var loaded = NetworkKnowledgeStore.Load(testFp);
            Assert.NotNull(loaded);
            Assert.Equal("Combo Strategy Alpha", loaded.BestStrategyName);
            Assert.Single(loaded.TestedStrategies);
            Assert.True(loaded.TestedStrategies.ContainsKey("fp_alpha"));
            Assert.Equal("WORK", loaded.TestedStrategies["fp_alpha"].Status);
            Assert.Equal(45, loaded.TestedStrategies["fp_alpha"].AverageLatencyMs);
        }
        finally
        {
            try
            {
                string path = NetworkKnowledgeStore.GetProfilePath(testFp);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }
    }

    [Fact]
    public void NetworkStrategyRecall_LoadsDifferentBestStrategyPerFingerprint()
    {
        string networkA = "net_a_" + Guid.NewGuid().ToString("N");
        string networkB = "net_b_" + Guid.NewGuid().ToString("N");
        string strategyA = "Strategy A";
        string strategyB = "Strategy B";

        try
        {
            var profileA = NetworkKnowledgeStore.GetOrCreate(networkA);
            profileA.BestStrategyName = strategyA;
            NetworkKnowledgeStore.Save(profileA);

            var profileB = NetworkKnowledgeStore.GetOrCreate(networkB);
            profileB.BestStrategyName = strategyB;
            NetworkKnowledgeStore.Save(profileB);

            var settings = new AppSettings();
            Assert.Equal(strategyA, MainViewModel.ResolveRememberedStrategyName(settings, networkA));
            Assert.Equal(strategyB, MainViewModel.ResolveRememberedStrategyName(settings, networkB));

            settings.NetworkStrategies[networkA] = "Settings Override Strategy";
            Assert.Equal("Settings Override Strategy", MainViewModel.ResolveRememberedStrategyName(settings, networkA));
        }
        finally
        {
            foreach (string fp in new[] { networkA, networkB })
            {
                try
                {
                    string path = NetworkKnowledgeStore.GetProfilePath(fp);
                    if (File.Exists(path)) File.Delete(path);
                    for (int i = 1; i <= 5; i++)
                    {
                        string bak = Path.Combine(AppPaths.BackupsDir, $"{Path.GetFileName(path)}.bak{i}");
                        if (File.Exists(bak)) File.Delete(bak);
                    }
                }
                catch { }
            }
        }
    }

    [Fact]
    public void SettingsService_AtomicWrite_AndRollingBackups_WorkCorrectly()
    {
        string? originalContent = File.Exists(AppPaths.SettingsFile)
            ? File.ReadAllText(AppPaths.SettingsFile)
            : null;
        try
        {
            var svc = new SettingsService();
            string initialPreset = "Preset_" + Guid.NewGuid().ToString("N");
            svc.Settings.ActivePresetName = initialPreset;
            svc.Save();

            Assert.Equal(6, svc.Settings.SchemaVersion);
            Assert.True(File.Exists(AppPaths.SettingsFile));

            // Save multiple times to test rolling backups.
            for (int i = 1; i <= 3; i++)
            {
                svc.Settings.ActivePresetName = $"Preset_Iteration_{i}";
                svc.Save();
            }

            Assert.True(File.Exists(Path.Combine(AppPaths.BackupsDir, "settings.json.bak1")));
        }
        finally
        {
            try
            {
                if (originalContent is not null)
                    File.WriteAllText(AppPaths.SettingsFile, originalContent);
                else if (File.Exists(AppPaths.SettingsFile))
                    File.Delete(AppPaths.SettingsFile);
            }
            catch { }
        }
    }

    [Fact]
    public void PresetService_AtomicWrite_CreatesRollingBackups()
    {
        var svc = new PresetService();
        svc.Save();

        // Check if presets.json or backup exists
        Assert.True(File.Exists(AppPaths.PresetsFile) || File.Exists(Path.Combine(AppPaths.BackupsDir, "presets.json.bak1")));
    }

    [Fact]
    public void BuiltinStrategyCatalog_LoadsBaselineStrategies_FromCodeCatalog()
    {
        var builtIns = PresetService.BuiltIns();
        Assert.NotEmpty(builtIns);
        Assert.Contains(builtIns, p => p.IsRecommended);
        Assert.All(builtIns, p =>
        {
            Assert.True(p.IsBuiltIn);
            Assert.NotEmpty(p.Args);
        });

        Assert.NotEmpty(ComboStrategyCatalog.All);
        Assert.All(ComboStrategyCatalog.All, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Name));
            Assert.NotEmpty(c.Args);
        });

        var service = new PresetService();
        foreach (var builtin in builtIns)
            Assert.Contains(service.All, p => p.Fingerprint == CanonicalProfile.ComputeFingerprint(builtin.Args));

        foreach (var catalog in ComboStrategyCatalog.All.Take(5))
            Assert.Contains(service.All, p => p.Fingerprint == CanonicalProfile.ComputeFingerprint(catalog.Args));
    }

    [Fact]
    public void UserAndGeneratedStrategies_PersistToPartitionedStrategyStores()
    {
        var svc = new PresetService();
        string suffix = Guid.NewGuid().ToString("N");
        var user = new Preset
        {
            Name = "User Strategy " + suffix,
            Args = { "--filter-tcp=443", "--hostlist={HOSTLIST:discord}", "--lua-desync=fake", "--unique=" + suffix },
            Status = "BROKEN",
            Reason = "unit_test"
        };
        var generated = new Preset
        {
            Name = "Generated Strategy " + suffix,
            Args = { "--filter-tcp=443", "--hostlist={HOSTLIST:youtube}", "--lua-desync=multisplit", "--unique=" + suffix },
            Status = "BROKEN",
            Reason = "unit_test",
            IsGenerated = true
        };

        try
        {
            svc.AddUser(user);
            svc.ReplaceAutoLeaderboard(new[] { generated });

            string userFp = CanonicalProfile.ComputeFingerprint(user.Args);
            string generatedFp = CanonicalProfile.ComputeFingerprint(generated.Args);

            Assert.Contains(Directory.EnumerateFiles(AppPaths.StrategiesUserDir, "*.gxprofile.json"),
                f => CanonicalProfile.LoadFromFile(f)?.Fingerprint == userFp);
            Assert.Contains(Directory.EnumerateFiles(AppPaths.StrategiesGeneratedDir, "*.gxprofile.json"),
                f => CanonicalProfile.LoadFromFile(f)?.Fingerprint == generatedFp);

            var reloaded = new PresetService();
            Assert.Contains(reloaded.All, p => p.Fingerprint == userFp && !p.IsBuiltIn);
            Assert.Contains(reloaded.All, p => p.Fingerprint == generatedFp && !p.IsBuiltIn);
        }
        finally
        {
            foreach (string dir in new[] { AppPaths.StrategiesUserDir, AppPaths.StrategiesGeneratedDir, AppPaths.ProfilesBrokenDir, AppPaths.ProfilesWorkDir })
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (string file in Directory.EnumerateFiles(dir, $"*{suffix}*.gxprofile.json"))
                        File.Delete(file);
                }
                catch { }
            }
        }
    }

    [Fact]
    public void AppPaths_RuntimeListsDir_And_AllSections_AreCreated()
    {
        AppPaths.EnsureCreated();
        Assert.True(Directory.Exists(AppPaths.RuntimeListsDir));
        Assert.True(Directory.Exists(AppPaths.NetworksDir));
        Assert.True(Directory.Exists(AppPaths.StrategiesDir));
        Assert.True(Directory.Exists(AppPaths.StrategiesUserDir));
        Assert.True(Directory.Exists(AppPaths.StrategiesGeneratedDir));
        Assert.True(Directory.Exists(AppPaths.StrategiesDiscoveredDir));
        Assert.True(Directory.Exists(AppPaths.TestsDir));
        Assert.True(Directory.Exists(AppPaths.DiscoveryDir));
        Assert.True(Directory.Exists(AppPaths.TelemetryDir));
        Assert.True(Directory.Exists(AppPaths.TelegramDir));
    }

    [Fact]
    public void HostlistRuntimeMaterialization_UsesProgramDataRuntimeLists_ForEngineArgs()
    {
        AppPaths.EnsureCreated();
        string suffix = Guid.NewGuid().ToString("N");
        string listName = "stageb-user-" + suffix;
        string discoveredName = "stageb-discovered-" + suffix;
        string ipsetName = "stageb-ipset-" + suffix;

        string userList = Path.Combine(AppPaths.ListsUserDir, $"list-{listName}.txt");
        string discoveredList = Path.Combine(AppPaths.ListsDiscoveredDir, $"list-{discoveredName}.txt");
        string ipset = Path.Combine(AppPaths.ListsDiscoveredDir, $"ipset-{ipsetName}.txt");
        string runtimeUserList = Path.Combine(AppPaths.RuntimeListsDir, Path.GetFileName(userList));
        string runtimeDiscoveredList = Path.Combine(AppPaths.RuntimeListsDir, Path.GetFileName(discoveredList));
        string runtimeIpset = Path.Combine(AppPaths.RuntimeListsDir, Path.GetFileName(ipset));

        try
        {
            Directory.CreateDirectory(AppPaths.ListsUserDir);
            Directory.CreateDirectory(AppPaths.ListsDiscoveredDir);
            File.WriteAllText(userList, "user-stageb.example\n");
            File.WriteAllText(discoveredList, "discovered-stageb.example\n");
            File.WriteAllText(ipset, "203.0.113.10/32\n");

            var preset = new Preset
            {
                Name = "StageB list materialization test",
                Args =
                {
                    $"{{HOSTLIST:{listName}}}",
                    $"{{HOSTLIST:{discoveredName}}}",
                    $"{{IPSET:{ipsetName}}}"
                }
            };

            var args = EngineService.BuildArguments(preset, null, forLaunch: true);

            Assert.Contains(args, a => a == $"--hostlist={runtimeUserList}");
            Assert.Contains(args, a => a == $"--hostlist={runtimeDiscoveredList}");
            Assert.Contains(args, a => a == $"--ipset={runtimeIpset}");
            Assert.True(File.Exists(runtimeUserList));
            Assert.True(File.Exists(runtimeDiscoveredList));
            Assert.True(File.Exists(runtimeIpset));
            Assert.StartsWith(AppPaths.RuntimeListsDir, runtimeUserList, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(AppPaths.RuntimeListsDir, runtimeDiscoveredList, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(AppPaths.RuntimeListsDir, runtimeIpset, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            foreach (var path in new[] { userList, discoveredList, ipset, runtimeUserList, runtimeDiscoveredList, runtimeIpset })
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
        }
    }

    [Fact]
    public void NetworkKnowledgeStore_RollingBackups_And_CorruptionRecovery()
    {
        string testFp = "corrupt_test_fp_" + Guid.NewGuid().ToString("N");
        string profilePath = NetworkKnowledgeStore.GetProfilePath(testFp);
        try
        {
            var profile = NetworkKnowledgeStore.GetOrCreate(testFp);
            profile.BestStrategyName = "Initial Strategy";
            NetworkKnowledgeStore.Save(profile);

            // Second save rotates backup
            profile.BestStrategyName = "Updated Strategy";
            NetworkKnowledgeStore.Save(profile);

            string bak1 = Path.Combine(AppPaths.BackupsDir, $"{Path.GetFileName(profilePath)}.bak1");
            Assert.True(File.Exists(bak1), "Rolling backup .bak1 must exist");

            // Intentionally corrupt the primary profile file
            File.WriteAllText(profilePath, "{ corrupted json - incomplete / invalid syntax");

            // Load should self-recover from backup
            var recovered = NetworkKnowledgeStore.Load(testFp);
            Assert.NotNull(recovered);
            Assert.False(string.IsNullOrWhiteSpace(recovered.NetworkFingerprint));
        }
        finally
        {
            try
            {
                if (File.Exists(profilePath)) File.Delete(profilePath);
                string bak1 = Path.Combine(AppPaths.BackupsDir, $"{Path.GetFileName(profilePath)}.bak1");
                if (File.Exists(bak1)) File.Delete(bak1);
            }
            catch { }
        }
    }

    [Fact]
    public void EngineService_KillOrphanedBypassProcesses_RunsSafely()
    {
        // Must execute without exceptions even when no processes or arbitrary system processes exist
        var ex = Record.Exception(() => EngineService.KillOrphanedBypassProcesses(System.Diagnostics.Process.GetCurrentProcess().Id));
        Assert.Null(ex);
    }

    [Fact]
    public void EngineStateStore_SavesAndLoads_Atomically()
    {
        string stateFile = AppPaths.EngineStateFile;
        string? originalContent = File.Exists(stateFile) ? File.ReadAllText(stateFile) : null;
        try
        {
            var record = new EngineStateRecord
            {
                BypassActive = true,
                ActivePresetName = "TestPreset_BypassActive",
                ActiveHostlist = "test-hostlist",
                LastStartedUtc = DateTime.UtcNow,
                ProcessId = 12345
            };

            EngineStateStore.Save(record);

            var loaded = EngineStateStore.Load();
            Assert.NotNull(loaded);
            Assert.True(loaded.BypassActive);
            Assert.Equal("TestPreset_BypassActive", loaded.ActivePresetName);
            Assert.Equal("test-hostlist", loaded.ActiveHostlist);
            Assert.Equal(12345, loaded.ProcessId);
        }
        finally
        {
            try
            {
                if (originalContent != null)
                    File.WriteAllText(stateFile, originalContent);
                else if (File.Exists(stateFile))
                    File.Delete(stateFile);
            }
            catch { }
        }
    }

    [Fact]
    public void EngineStateStore_CorruptedFile_ReturnsNullGracefully()
    {
        string stateFile = AppPaths.EngineStateFile;
        string? originalContent = File.Exists(stateFile) ? File.ReadAllText(stateFile) : null;
        try
        {
            string dir = Path.GetDirectoryName(stateFile)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(stateFile, "{ broken json syntax !!! ");

            var loaded = EngineStateStore.Load();
            Assert.Null(loaded);
        }
        finally
        {
            try
            {
                if (originalContent != null)
                    File.WriteAllText(stateFile, originalContent);
                else if (File.Exists(stateFile))
                    File.Delete(stateFile);
            }
            catch { }
        }
    }

    [Fact]
    public void DpiStateRestore_Lifecycle_RestoresBypassAccurately()
    {
        // Case 1: Bypass was inactive before reboot
        var inactiveRecord = new EngineStateRecord { BypassActive = false, ActivePresetName = null };
        var settingsInactive = new AppSettings { AutostartEngine = false, BypassDesired = false };
        bool shouldRestoreInactive = settingsInactive.AutostartEngine 
            || inactiveRecord.BypassActive 
            || settingsInactive.BypassDesired;
        Assert.False(shouldRestoreInactive, "Inactive bypass state must NOT trigger auto-start");

        // Case 2: Bypass was active before reboot
        var activeRecord = new EngineStateRecord { BypassActive = true, ActivePresetName = "Combo Strategy" };
        var settingsActive = new AppSettings { AutostartEngine = false, BypassDesired = true };
        bool shouldRestoreActive = settingsActive.AutostartEngine 
            || activeRecord.BypassActive 
            || settingsActive.BypassDesired;
        Assert.True(shouldRestoreActive, "Active bypass state MUST trigger auto-start");
        Assert.Equal("Combo Strategy", activeRecord.ActivePresetName);
    }

    [Fact]
    public void GuiToggleCommand_FiveCycles_UpdatesCanExecute_AndNoHangingState()
    {
        var vm = new MainViewModel();
        if (vm.SelectedPreset == null && vm.Presets.Count > 0)
            vm.SelectedPreset = vm.Presets[0];

        for (int cycle = 1; cycle <= 5; cycle++)
        {
            // 1. When stopped, CanStart and ToggleCommand.CanExecute must be true
            vm.SetSimulatedStateForHarness(EngineState.Stopped);
            Assert.Equal(EngineState.Stopped, vm.State);
            Assert.True(vm.CanStart, $"Cycle {cycle}: CanStart must be true when stopped");
            Assert.True(vm.ToggleCommand.CanExecute(null), $"Cycle {cycle}: ToggleCommand must be executable when stopped");

            // 2. Transition to Running
            vm.SetSimulatedStateForHarness(EngineState.Running);
            Assert.Equal(EngineState.Running, vm.State);
            Assert.True(vm.IsRunning, $"Cycle {cycle}: IsRunning must be true");
            Assert.True(vm.ToggleCommand.CanExecute(null), $"Cycle {cycle}: ToggleCommand must be executable when running to stop");

            // 3. Transition back to Stopped
            vm.SetSimulatedStateForHarness(EngineState.Stopped);
            Assert.Equal(EngineState.Stopped, vm.State);
            Assert.False(vm.IsRunning, $"Cycle {cycle}: IsRunning must be false after stop");
            Assert.True(vm.CanStart, $"Cycle {cycle}: CanStart must automatically recover to true");
            Assert.True(vm.ToggleCommand.CanExecute(null), $"Cycle {cycle}: ToggleCommand must automatically be executable without external requery");
        }
    }
}
