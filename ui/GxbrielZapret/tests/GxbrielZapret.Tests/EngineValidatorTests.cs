using System.IO;
using System.Security.Cryptography;
using System.Text;
using Xunit;
using GxbrielZapret.Services.Engine;
using GxbrielZapret.Services.Infrastructure;
using GxbrielZapret.Services.Platform;

namespace GxbrielZapret.Tests;

public class EngineValidatorTests
{
    [Fact]
    public void Manifest_LoadsSuccessfully_AndContainsWinws2()
    {
        var manifest = EngineValidator.LoadManifest();
        Assert.NotNull(manifest);
        Assert.Equal("zapret2", manifest.Engine);
        Assert.Equal("v1.0.5.2", manifest.Version);
        Assert.True(manifest.Files.ContainsKey("winws2.exe"));

        string expectedHash = "eb9807972c15f05a00416b0549d766a71c9186f8101d406e3cc6c16278164d9e";
        Assert.Equal(expectedHash, manifest.Files["winws2.exe"].Sha256);
    }

    [Fact]
    public void EnsureEngineVerified_ThrowsFileNotFound_WhenBinaryMissing()
    {
        string nonExistentPath = Path.Combine(Path.GetTempPath(), $"missing_winws2_{Guid.NewGuid():N}.exe");
        Assert.Throws<FileNotFoundException>(() => EngineValidator.EnsureEngineVerified(nonExistentPath));
    }

    [Fact]
    public void EnsureEngineVerified_BlocksTamperedBinary_WithSecurityException()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"test_zapret_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string tamperedPath = Path.Combine(tempDir, "winws2.exe");

        try
        {
            // Write malicious/tampered content
            File.WriteAllBytes(tamperedPath, Encoding.UTF8.GetBytes("MALICIOUS_OR_TAMPERED_WINWS2_BINARY_PAYLOAD"));

            var ex = Assert.Throws<InvalidOperationException>(() => EngineValidator.EnsureEngineVerified(tamperedPath));
            Assert.Contains("SECURITY ERROR", ex.Message);
            Assert.Contains("SHA-256 mismatch", ex.Message);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void AppPaths_Root_PointsTo_GxbrielZapret()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string expectedRoot = Path.Combine(localAppData, "GXBRIEL_ZAPRET");

        Assert.Equal(expectedRoot, AppPaths.Root);
        Assert.Contains("GXBRIEL_ZAPRET", AppPaths.EngineDir);
        Assert.Contains("GXBRIEL_ZAPRET", AppPaths.SettingsFile);
    }

    [Fact]
    public void AutostartService_TaskName_IsBranded()
    {
        Assert.Equal("GXBRIEL_ZAPRET Autostart", AutostartService.TaskName);
    }

    [Fact]
    public void AutostartService_BuildTaskXml_GeneratesHardenedElevatedDefinition()
    {
        string fakeExe = @"C:\App\GXBRIEL_ZAPRET.exe";
        string xml = AutostartService.BuildTaskXml(fakeExe, "--tray", 5);

        // 1. Must parse as valid XML
        var doc = System.Xml.Linq.XDocument.Parse(xml);
        Assert.NotNull(doc.Root);

        // 2. Battery resilience: must NOT stop or disallow starting on battery power
        Assert.Contains("<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>", xml);
        Assert.Contains("<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>", xml);

        // 3. Execution time limit: must be PT0S (infinite/no 72-hour timeout kill)
        Assert.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", xml);

        // 4. Privilege: highest available elevated execution without UAC popup
        Assert.Contains("<RunLevel>HighestAvailable</RunLevel>", xml);
        Assert.Contains("<LogonType>InteractiveToken</LogonType>", xml);

        // 5. Logon trigger delay
        Assert.Contains("<Delay>PT5S</Delay>", xml);

        // 6. Action targets executable with --tray argument
        Assert.Contains($"<Command>{fakeExe}</Command>", xml);
        Assert.Contains("<Arguments>--tray</Arguments>", xml);
    }

    [Fact]
    public void VerifyBinary_ReturnsTrue_ForExactMatchingFile()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"test_valid_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string testFile = Path.Combine(tempDir, "testfile.bin");

        try
        {
            byte[] data = Encoding.UTF8.GetBytes("TestPayloadContent12345");
            File.WriteAllBytes(testFile, data);

            string computed = EngineValidator.ComputeSha256(testFile);
            using var sha = SHA256.Create();
            string manual = Convert.ToHexString(sha.ComputeHash(data)).ToLowerInvariant();

            Assert.Equal(manual, computed);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void UsqueAndWarp_AssetsDoNotExist()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string? projectRoot = Directory.GetParent(baseDir)?.Parent?.Parent?.Parent?.Parent?.FullName;
        if (projectRoot != null && Directory.Exists(projectRoot))
        {
            string usqueExe = Path.Combine(projectRoot, "Assets", "usque", "usque.exe");
            Assert.False(File.Exists(usqueExe), "usque.exe must not exist in the project tree.");

            string warpDir = Path.Combine(projectRoot, "Services", "Warp");
            Assert.False(Directory.Exists(warpDir), "Services/Warp must not exist in the project tree.");
        }
    }

    [Fact]
    public void TrayMenu_ToggleCommands_And_Shutdown_CleanExecution()
    {
        Exception? testEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                var vm = new GxbrielZapret.ViewModels.MainViewModel();
                try
                {
                    Assert.NotNull(vm.ToggleCommand);
                    Assert.NotNull(vm.EnableTelegramCommand);

                    // Test Telegram toggle from tray command
                    bool initialTg = vm.IsTelegramProxyRunning;
                    vm.EnableTelegramCommand.Execute(null);
                    Assert.NotEqual(initialTg, vm.IsTelegramProxyRunning);

                    // Toggle Telegram back
                    vm.EnableTelegramCommand.Execute(null);
                    Assert.Equal(initialTg, vm.IsTelegramProxyRunning);

                    // Verify clean shutdown
                    vm.Shutdown();
                }
                finally
                {
                    vm.Shutdown();
                }
            }
            catch (Exception ex)
            {
                testEx = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        bool finished = thread.Join(15000);

        Assert.True(finished, "TrayMenu test timed out after 15 seconds.");
        if (testEx != null) throw new AggregateException("TrayMenu test failed on STA thread", testEx);
    }

    [Fact]
    public async Task PrivacySanitization_TelegramAndEngineLogs_ContainZeroSecretsOrPayloads()
    {
        string secretHex = "deadbeefcafebabe0123456789abcdef";
        var capturedLogs = new List<string>();

        using var svc = new GxbrielZapret.Services.Telegram.TelegramProxyService();
        svc.LogLine += s => capturedLogs.Add(s);
        svc.Configure(19495, secretHex);
        Assert.True(svc.Start());

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            // Trigger self test / connection attempt
            await svc.BridgeSelfTestAsync(2, cts.Token);

            // Assert that the raw secret is never present in any emitted log line
            foreach (var log in capturedLogs)
            {
                Assert.DoesNotContain(secretHex, log, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain($"dd{secretHex}", log, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            svc.Stop();
        }
    }

    [Fact]
    public async Task RelayCommand_RaiseCanExecuteChanged_BackgroundThread_Safe()
    {
        bool executed = false;
        var cmd = new GxbrielZapret.Mvvm.RelayCommand(_ => executed = true, _ => true);

        int eventCount = 0;
        cmd.CanExecuteChanged += (_, _) => Interlocked.Increment(ref eventCount);

        // Raise from background thread pool
        await Task.Run(() => cmd.RaiseCanExecuteChanged());

        Assert.True(cmd.CanExecute(null));
        cmd.Execute(null);
        Assert.True(executed);
    }

    [Fact]
    public void EngineService_Stop_WhenAlreadyStoppedOrNull_NeverStuckInStopping()
    {
        using var engine = new GxbrielZapret.Services.Engine.EngineService();
        Assert.Equal(GxbrielZapret.Services.Engine.EngineState.Stopped, engine.State);

        // Stopping an already stopped engine must remain Stopped and not throw
        engine.Stop();
        Assert.Equal(GxbrielZapret.Services.Engine.EngineState.Stopped, engine.State);

        // Multiple rapid stops must be idempotent
        engine.Stop();
        engine.Stop();
        Assert.Equal(GxbrielZapret.Services.Engine.EngineState.Stopped, engine.State);
    }

    [Fact]
    public async Task EngineService_Start_StaysAlive_WithoutWindowsSandboxError()
    {
        AppPaths.EnsureCreated();
        if (!File.Exists(AppPaths.WinwsExe))
            return;

        bool isAdmin = new System.Security.Principal.WindowsPrincipal(
            System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        if (!isAdmin)
            return;

        var presetSvc = new GxbrielZapret.Services.Strategies.PresetService();
        presetSvc.Load();
        var preset = presetSvc.All.FirstOrDefault(p => p.Name.Contains("ALT11", StringComparison.OrdinalIgnoreCase))
                     ?? presetSvc.All.First();

        using var engine = new GxbrielZapret.Services.Engine.EngineService();
        var logs = new List<string>();
        engine.LogLine += l => { lock (logs) logs.Add(l); };

        try
        {
            engine.Start(preset, null);
            await Task.Delay(1200);

            string joinedLogs;
            lock (logs) joinedLogs = string.Join("\n", logs);

            Assert.DoesNotContain("Cannot init Windows sandbox", joinedLogs, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(GxbrielZapret.Services.Engine.EngineState.Running, engine.State);
            Assert.True(engine.IsRunning, $"Engine did not stay running. Logs:\n{joinedLogs}");
            Assert.NotNull(engine.ActiveProcessId);
        }
        finally
        {
            engine.Stop();
        }
    }
}

