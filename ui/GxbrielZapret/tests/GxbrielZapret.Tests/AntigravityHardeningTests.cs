using System.IO;
using System.Text.Json;
using GxbrielZapret.Services.Infrastructure;
using GxbrielZapret.Services.Platform;
using Xunit;

namespace GxbrielZapret.Tests;

public sealed class AntigravityHardeningTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _prevAppDataRoot;

    public AntigravityHardeningTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "gxb-hardening-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _prevAppDataRoot = Environment.GetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT") ?? "";
        Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT", _tempDir);
        AppPaths.EnsureCreated();
    }

    public void Dispose()
    {
        AntigravityProxyCredentialStore.SimulateProtectionFailure = false;
        try
        {
            if (string.IsNullOrEmpty(_prevAppDataRoot))
                Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT", null);
            else
                Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT", _prevAppDataRoot);

            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch { }
    }

    [Fact]
    public void UserPasswordUrlIsAbsentFromSerializedSettings()
    {
        var settings = new AppSettings();
        const string syntheticProxy = "http://synth_user:synth_secret_pass99@127.0.0.1:8080";
        settings.SetAntigravityProxy(syntheticProxy);
        settings.AntigravityUseProxy = true;

        Assert.Equal("http://127.0.0.1:8080", settings.AntigravityProxy);
        Assert.False(string.IsNullOrWhiteSpace(settings.AntigravityProxyProtectedCredentials));
        Assert.Equal(syntheticProxy, settings.GetEffectiveAntigravityProxy());

        string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });

        Assert.DoesNotContain("synth_secret_pass99", json);
        Assert.DoesNotContain("synth_user", json);
        Assert.DoesNotContain("synth_user:synth_secret_pass99@", json);
        Assert.Contains("http://127.0.0.1:8080", json);
        Assert.Contains("AntigravityProxyProtectedCredentials", json);
    }

    [Fact]
    public void ProxyCredentialsPersistNowhereInUserEnvironmentAfterSave()
    {
        string? initialUserEnv = Environment.GetEnvironmentVariable("AG_LS_PROXY", EnvironmentVariableTarget.User);
        string? initialProcEnv = Environment.GetEnvironmentVariable("AG_LS_PROXY");
        try
        {
            var svc = new SettingsService();
            const string syntheticSecret = "synth_ultra_secret_pw77";
            string syntheticProxy = $"http://synthetic_usr:{syntheticSecret}@127.0.0.1:8888";

            svc.Settings.SetAntigravityProxy(syntheticProxy);
            svc.Settings.AntigravityUseProxy = true;
            bool saved = svc.Save();
            Assert.True(saved);

            string? currentUserEnv = Environment.GetEnvironmentVariable("AG_LS_PROXY", EnvironmentVariableTarget.User);
            if (currentUserEnv is not null)
            {
                Assert.DoesNotContain(syntheticSecret, currentUserEnv);
                Assert.DoesNotContain("synthetic_usr", currentUserEnv);
            }

            string? currentProcEnv = Environment.GetEnvironmentVariable("AG_LS_PROXY");
            if (currentProcEnv is not null)
            {
                Assert.DoesNotContain(syntheticSecret, currentProcEnv);
                Assert.DoesNotContain("synthetic_usr", currentProcEnv);
            }

            string savedJson = File.ReadAllText(AppPaths.SettingsFile);
            Assert.DoesNotContain(syntheticSecret, savedJson);
            Assert.DoesNotContain("synthetic_usr", savedJson);
            Assert.Contains("http://127.0.0.1:8888", savedJson);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AG_LS_PROXY", initialUserEnv, EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable("AG_LS_PROXY", initialProcEnv);
        }
    }

    [Fact]
    public void ChildProcessStartInfoReceivesFullProxyWithCredentials()
    {
        string fakeExe = Path.Combine(_tempDir, "Antigravity IDE.exe");
        File.WriteAllText(fakeExe, "binary fixture");

        var settings = new AppSettings();
        const string syntheticProxy = "http://child_tester:synthetic_tok_123@127.0.0.1:10809";
        settings.SetAntigravityProxy(syntheticProxy);
        settings.AntigravityUseProxy = true;

        string effective = settings.GetEffectiveAntigravityProxy();
        Assert.Equal(syntheticProxy, effective);

        var info = AntigravityLauncher.BuildStartInfo(fakeExe, directConnection: false, customProxy: effective);
        Assert.Equal(syntheticProxy, info.Environment["AG_LS_PROXY"]);
        Assert.Equal(syntheticProxy, info.Environment["HTTPS_PROXY"]);
        Assert.Equal(syntheticProxy, info.Environment["HTTP_PROXY"]);
        Assert.Equal(syntheticProxy, info.Environment["ALL_PROXY"]);

        var direct = AntigravityLauncher.BuildStartInfo(fakeExe, directConnection: true, customProxy: effective);
        Assert.False(direct.Environment.ContainsKey("AG_LS_PROXY"));
        Assert.False(direct.Environment.ContainsKey("HTTPS_PROXY"));
        Assert.False(direct.Environment.ContainsKey("HTTP_PROXY"));
        Assert.False(direct.Environment.ContainsKey("ALL_PROXY"));
    }

    [Fact]
    public void FullLoadSaveMigrationSanitizesCurrentSettingsAndAllBackups()
    {
        const string legacySecret = "legacy_integration_secret_888";
        string legacyJson = $$"""
        {
          "SchemaVersion": 5,
          "AntigravityProxy": "http://legacy_int_user:{{legacySecret}}@127.0.0.1:8080",
          "AntigravityUseProxy": true
        }
        """;
        File.WriteAllText(AppPaths.SettingsFile, legacyJson);

        // Instantiate SettingsService which triggers Load() -> MigrateInMemory() -> Save() (rotating to .bak1)
        var svc = new SettingsService();
        Assert.Equal("http://127.0.0.1:8080", svc.Settings.AntigravityProxy);
        Assert.Equal($"http://legacy_int_user:{legacySecret}@127.0.0.1:8080", svc.Settings.GetEffectiveAntigravityProxy());

        // Perform multiple saves to rotate across all backups
        for (int i = 1; i <= 3; i++)
        {
            svc.Settings.ActivePresetName = $"Preset_Iteration_{i}";
            svc.Save();
        }

        // Verify that current settings.json contains NO plaintext credentials
        string currentJson = File.ReadAllText(AppPaths.SettingsFile);
        Assert.DoesNotContain(legacySecret, currentJson);
        Assert.DoesNotContain("legacy_int_user", currentJson);
        Assert.Contains("http://127.0.0.1:8080", currentJson);

        // Verify that EVERY backup file (.bak1, .bak2, .bak3) contains NO plaintext credentials
        for (int i = 1; i <= 3; i++)
        {
            string bakPath = Path.Combine(AppPaths.BackupsDir, $"settings.json.bak{i}");
            if (File.Exists(bakPath))
            {
                string bakJson = File.ReadAllText(bakPath);
                Assert.DoesNotContain(legacySecret, bakJson);
                Assert.DoesNotContain("legacy_int_user", bakJson);
                Assert.Contains("http://127.0.0.1:8080", bakJson);
            }
        }
    }

    [Fact]
    public void OldHkcuAndProcessProxyMigrationAndClear()
    {
        string? initialUser = Environment.GetEnvironmentVariable("AG_LS_PROXY", EnvironmentVariableTarget.User);
        string? initialProc = Environment.GetEnvironmentVariable("AG_LS_PROXY");
        string? initialUnrelated = Environment.GetEnvironmentVariable("GXB_TEST_UNRELATED");

        try
        {
            Environment.SetEnvironmentVariable("GXB_TEST_UNRELATED", "keep_me_intact");
            const string syntheticSecret = "synth_env_secret_1234";
            string syntheticProxy = $"http://env_user:{syntheticSecret}@127.0.0.1:8080";

            Environment.SetEnvironmentVariable("AG_LS_PROXY", syntheticProxy, EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable("AG_LS_PROXY", syntheticProxy);

            var settings = new AppSettings();
            int saveCount = 0;
            bool migrated = AntigravityProxyManager.MigrateAndClearUserEnvironment(settings, () => saveCount++);

            Assert.True(migrated);
            Assert.Equal(1, saveCount);
            Assert.Equal("http://127.0.0.1:8080", settings.AntigravityProxy);
            Assert.Equal(syntheticProxy, settings.GetEffectiveAntigravityProxy());

            // Verify both HKCU and Process environment variables are cleared
            Assert.Null(Environment.GetEnvironmentVariable("AG_LS_PROXY", EnvironmentVariableTarget.User));
            Assert.Null(Environment.GetEnvironmentVariable("AG_LS_PROXY"));

            // Verify unrelated environment variable was untouched
            Assert.Equal("keep_me_intact", Environment.GetEnvironmentVariable("GXB_TEST_UNRELATED"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("AG_LS_PROXY", initialUser, EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable("AG_LS_PROXY", initialProc);
            Environment.SetEnvironmentVariable("GXB_TEST_UNRELATED", initialUnrelated);
        }
    }

    [Fact]
    public void ProtectionFailurePreservesCallerStateAndFailsClosedWithoutSaving()
    {
        var settings = new AppSettings
        {
            AntigravityProxy = "http://preserved:8080",
            AntigravityProxyProtectedCredentials = "original_cipher"
        };

        AntigravityProxyCredentialStore.SimulateProtectionFailure = true;
        try
        {
            var ex = Assert.Throws<AntigravityCredentialException>(() =>
                settings.SetAntigravityProxy("http://synth_fail_user:synth_fail_pass@127.0.0.1:9090"));

            Assert.Contains("Не удалось безопасно зашифровать", ex.Message);
            Assert.DoesNotContain("synth_fail_pass", ex.Message);
            Assert.DoesNotContain("synth_fail_user", ex.Message);

            // Caller state is preserved
            Assert.Equal("http://preserved:8080", settings.AntigravityProxy);
            Assert.Equal("original_cipher", settings.AntigravityProxyProtectedCredentials);

            // Migration failure fails closed: settings.json on disk is NOT overwritten with broken state
            string testSettingsFile = AppPaths.SettingsFile;
            string initialContent = """
            {
              "SchemaVersion": 5,
              "AntigravityProxy": "http://user:secret@127.0.0.1:8080",
              "AntigravityUseProxy": true
            }
            """;
            File.WriteAllText(testSettingsFile, initialContent);

            var svc = new SettingsService();
            Assert.NotNull(svc.LastSaveError);
            Assert.Contains("Не удалось безопасно зашифровать", svc.LastSaveError);
            Assert.DoesNotContain("secret", svc.LastSaveError);

            // File on disk was NOT overwritten by silent save
            string diskContent = File.ReadAllText(testSettingsFile);
            Assert.Equal(initialContent, diskContent);
        }
        finally
        {
            AntigravityProxyCredentialStore.SimulateProtectionFailure = false;
        }
    }

    [Fact]
    public void HkcuMigrationFailsClosedWhenProtectionFails()
    {
        string? initialUser = Environment.GetEnvironmentVariable("AG_LS_PROXY", EnvironmentVariableTarget.User);
        string? initialProc = Environment.GetEnvironmentVariable("AG_LS_PROXY");
        try
        {
            const string syntheticSecret = "synth_fail_hkcu_secret";
            string syntheticProxy = $"http://env_user:{syntheticSecret}@127.0.0.1:8080";

            Environment.SetEnvironmentVariable("AG_LS_PROXY", syntheticProxy, EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable("AG_LS_PROXY", syntheticProxy);

            var settings = new AppSettings();
            int saveCount = 0;
            AntigravityProxyCredentialStore.SimulateProtectionFailure = true;

            var ex = Assert.Throws<AntigravityCredentialException>(() =>
                AntigravityProxyManager.MigrateAndClearUserEnvironment(settings, () => saveCount++));

            Assert.Contains("Не удалось безопасно зашифровать", ex.Message);
            Assert.DoesNotContain(syntheticSecret, ex.Message);
            Assert.Equal(0, saveCount);

            // HKCU and process environment must NOT be cleared when protection fails (fail closed)
            Assert.Equal(syntheticProxy, Environment.GetEnvironmentVariable("AG_LS_PROXY", EnvironmentVariableTarget.User));
            Assert.Equal(syntheticProxy, Environment.GetEnvironmentVariable("AG_LS_PROXY"));
        }
        finally
        {
            AntigravityProxyCredentialStore.SimulateProtectionFailure = false;
            Environment.SetEnvironmentVariable("AG_LS_PROXY", initialUser, EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable("AG_LS_PROXY", initialProc);
        }
    }

    [Theory]
    [InlineData("http://alice:pass123@127.0.0.1:8080", "http://127.0.0.1:8080")]
    [InlineData("https://bob:p%40ss@proxy.example:8443", "https://proxy.example:8443")]
    [InlineData("socks5://user:pass@127.0.0.1:1080", "socks5://127.0.0.1:1080")]
    [InlineData("http://127.0.0.1:7890", "http://127.0.0.1:7890")]
    [InlineData("http://[::1]:8080", "http://[::1]:8080")]
    [InlineData("http://synth_user@domain.local:synth_pass@word99@127.0.0.1:8080", "http://127.0.0.1:8080")]
    public void RedactionStripsCredentialsAccurately(string input, string expected)
    {
        string redacted = AntigravityProxy.Redact(input);
        Assert.Equal(expected, redacted);
    }

    [Fact]
    public void RedactionHandlesMultipleAtSignsWithoutLeakingCredentialSuffix()
    {
        const string malformedSynthetic = "http://synth_user@domain.local:synth_pass@word99@127.0.0.1:8080";
        string redacted = AntigravityProxy.Redact(malformedSynthetic);
        Assert.Equal("http://127.0.0.1:8080", redacted);
        Assert.DoesNotContain("synth_user", redacted);
        Assert.DoesNotContain("synth_pass", redacted);
        Assert.DoesNotContain("word99", redacted);
    }

    [Fact]
    public void DiagnosticOutputNeverLeaksCredentials()
    {
        const string secret1 = "secret_scoped_abc";
        const string secret2 = "secret_user_def";
        const string secret3 = "secret_proc_ghi";

        string diagnostic = AntigravityProxyDiagnostic.Describe(
            process: $"http://proc_usr:{secret3}@127.0.0.1:5000",
            user: $"http://user_usr:{secret2}@127.0.0.1:6000",
            machine: null,
            scoped: $"http://scoped_usr:{secret1}@127.0.0.1:7000");

        Assert.Contains("127.0.0.1:7000", diagnostic);
        Assert.DoesNotContain(secret1, diagnostic);
        Assert.DoesNotContain(secret2, diagnostic);
        Assert.DoesNotContain(secret3, diagnostic);
        Assert.DoesNotContain("scoped_usr", diagnostic);
        Assert.DoesNotContain("user_usr", diagnostic);
        Assert.DoesNotContain("proc_usr", diagnostic);
    }

    [Fact]
    public void ParseErrorNeverDisclosesCredentialsInException()
    {
        const string secret = "sensitive_pass_999";
        string invalidUrl = $"http://secret_user:{secret}@127.0.0.1:0/illegal_path?query=secret";

        var ex = Assert.Throws<ArgumentException>(() => AntigravityProxy.Parse(invalidUrl));
        Assert.DoesNotContain(secret, ex.Message);
        Assert.DoesNotContain("secret_user", ex.Message);
        Assert.DoesNotContain("illegal_path", ex.Message);
    }

    [Fact]
    public void ScopedPrecedenceOverridesUserMachineAndProcessExplicitly()
    {
        var scoped = AntigravityProxy.ResolveDetailed(
            scoped: "http://127.0.0.1:1001",
            user: "http://127.0.0.1:1002",
            machine: "http://127.0.0.1:1003",
            process: "http://127.0.0.1:1004");
        Assert.Equal(AntigravityProxySource.Scoped, scoped.Source);
        Assert.Equal("http://127.0.0.1:1001", scoped.Value);

        var user = AntigravityProxy.ResolveDetailed(
            scoped: null,
            user: "http://127.0.0.1:1002",
            machine: "http://127.0.0.1:1003",
            process: "http://127.0.0.1:1004");
        Assert.Equal(AntigravityProxySource.User, user.Source);
        Assert.Equal("http://127.0.0.1:1002", user.Value);

        var machine = AntigravityProxy.ResolveDetailed(
            scoped: "",
            user: " ",
            machine: "http://127.0.0.1:1003",
            process: "http://127.0.0.1:1004");
        Assert.Equal(AntigravityProxySource.Machine, machine.Source);
        Assert.Equal("http://127.0.0.1:1003", machine.Value);

        var process = AntigravityProxy.ResolveDetailed(
            scoped: null,
            user: null,
            machine: null,
            process: "http://127.0.0.1:1004");
        Assert.Equal(AntigravityProxySource.Process, process.Source);
        Assert.Equal("http://127.0.0.1:1004", process.Value);

        var none = AntigravityProxy.ResolveDetailed(null, null, null, null);
        Assert.Equal(AntigravityProxySource.None, none.Source);
        Assert.Null(none.Value);
    }
}
