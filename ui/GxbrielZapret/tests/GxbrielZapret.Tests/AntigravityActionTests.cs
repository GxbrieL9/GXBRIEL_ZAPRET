using System.IO;
using GxbrielZapret.Services.Platform;
using GxbrielZapret.ViewModels;
using Xunit;

namespace GxbrielZapret.Tests;

public sealed class AntigravityActionTests : IDisposable
{
    [Fact]
    public void AppAndIdeAreSeparateProductsWhileDuplicateAppsRequireSelection()
    {
        Write("app/Antigravity.exe");
        Write("ide/Antigravity IDE.exe");
        string server = Write("app/resources/bin/language_server.exe");
        string app = Path.Combine(_root, "app"), ide = Path.Combine(_root, "ide");
        Assert.False(AntigravityDiscovery.RequiresSelection([ide, app]));
        Assert.Equal(server, AntigravityDiscovery.Find(null, [ide, app])["LanguageServer"]);
        Write("second-app/Antigravity.exe");
        Assert.True(AntigravityDiscovery.RequiresSelection([app, ide, Path.Combine(_root, "second-app")]));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LaunchNeverInheritsRemovedGoDebugFlag(bool direct)
    {
        string exe = Write("launch-env/Antigravity.exe");
        string? previous = Environment.GetEnvironmentVariable("GODEBUG");
        try
        {
            Environment.SetEnvironmentVariable("GODEBUG", "tlsmlkem=0,tlskyber=0,http2debug=1");
            var info = AntigravityLauncher.BuildStartInfo(exe, direct);
            Assert.False(info.UseShellExecute);
            Assert.Equal("tlsmlkem=0,http2debug=1", info.Environment["GODEBUG"]);
            Assert.Equal("tlsmlkem=0,tlskyber=0,http2debug=1", Environment.GetEnvironmentVariable("GODEBUG"));
        }
        finally { Environment.SetEnvironmentVariable("GODEBUG", previous); }
    }
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gx-ag-actions-" + Guid.NewGuid().ToString("N"));
    public AntigravityActionTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
    private string Write(string name) { string path = Path.GetFullPath(Path.Combine(_root, name)); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "fixture"); return path; }

    [Theory]
    [InlineData("NotChecked", false, false, false)]
    [InlineData("NotFound", false, false, false)]
    [InlineData("Patched", false, false, false)]
    [InlineData("Patched", true, false, true)]
    [InlineData("Unpatched", false, true, false)]
    [InlineData("Partial", false, true, false)]
    [InlineData("Unknown", false, false, false)]
    [InlineData("Error", true, false, false)]
    [InlineData("ExternalPatch", false, false, false)]
    [InlineData("ExternalPartial", false, false, false)]
    public void EveryComponentStateExplainsItsAvailableActions(string state, bool backup, bool patch, bool restore)
    {
        var component = new AntigravityComponentStatus { State = state, HasBackup = backup, Path = state is "NotFound" or "NotChecked" ? null : "C:\\fixture\\main.js" };
        Assert.Equal(patch, component.CanPatch);
        Assert.Equal(restore, component.CanRestore);
        Assert.False(string.IsNullOrWhiteSpace(component.PatchReason));
        Assert.False(string.IsNullOrWhiteSpace(component.RestoreReason));
        Assert.False(string.IsNullOrWhiteSpace(component.ActionExplanation));
        Assert.DoesNotContain("разблокирован", component.StateDisplay);
        if (state is "ExternalPatch" or "ExternalPartial") Assert.Equal("Другой патч", component.PatchLabel);
    }

    [Fact]
    public void AlreadyPatchedAndNoBackupClearlyExplainsWhyBothActionsAreUnavailable()
    {
        var component = new AntigravityComponentStatus { State = "Patched", Path = Write("main.js") };
        Assert.Equal("Уже применён", component.PatchLabel);
        Assert.Contains("Нет исходной копии", component.ActionExplanation);
        Assert.Contains("официальный установщик", component.RestoreReason);
        Assert.False(component.CanPatch);
        Assert.False(component.CanRestore);
    }

    [Fact]
    public void NotCheckedIsNotReportedAsMissing()
    {
        var result = MainViewModel.UncheckedPatcherStatus();
        Assert.All(result.Components, c => Assert.Equal("NotChecked", c.State));
        Assert.All(result.Components, c => Assert.Equal("Не проверен", c.PatchLabel));
    }

    [Fact]
    public void AutomaticDiscoveryDoesNotBorrowMainJsFromAnotherInstallation()
    {
        Write("desktop/Antigravity.exe");
        string server = Write("desktop/resources/bin/language_server.exe");
        Write("old-ide/Antigravity IDE.exe");
        string oldJs = Write("old-ide/resources/app/out/main.js");
        string oldServer = Write("old-ide/resources/app/extensions/antigravity/bin/language_server_windows_x64.exe");
        var found = AntigravityDiscovery.Find(null, [Path.Combine(_root, "desktop"), Path.Combine(_root, "old-ide")]);
        Assert.Equal(server, found["LanguageServer"]);
        Assert.False(found.ContainsKey("Ide"));
        var explicitOldIde = AntigravityDiscovery.Find(Path.Combine(_root, "old-ide"));
        Assert.Equal(oldJs, explicitOldIde["Ide"]);
        Assert.Equal(oldServer, explicitOldIde["LanguageServer"]);
    }

    [Fact]
    public void BundledHubExtensionBelongsToSelectedInstallation()
    {
        Write("desktop/Antigravity.exe");
        Write("hub/Antigravity IDE.exe");
        string extension = Write("hub/resources/app/extensions/agy/vscode/extension.js");
        string other = Write("other/extension.js");
        var installations = AntigravityDiscovery.FindInstallations([Path.Combine(_root, "desktop"), Path.Combine(_root, "hub"), Path.Combine(_root, "desktop")]);
        Assert.Equal(2, installations.Count);
        var desktop = AntigravityDiscovery.Find(null, [Path.Combine(_root, "desktop"), Path.Combine(_root, "hub"), Path.Combine(_root, "other")]);
        Assert.Equal(other, desktop["VsCode"]);
        string cli = Write("shared-cli/agy.exe");
        var hub = AntigravityDiscovery.Find(Path.Combine(_root, "hub"), sharedCliRoot: Path.Combine(_root, "shared-cli"));
        Assert.Equal(extension, hub["VsCode"]);
        Assert.Equal(cli, hub["Agy"]);
        Assert.False(hub.ContainsKey("LanguageServer"));
    }

    [Fact]
    public void LauncherPrefersCurrentDesktopInstallationAndManualScopeNeverFallsBack()
    {
        Write("server/Antigravity.exe");
        string ide = Write("ide/Antigravity IDE.exe");
        var components = new[]
        {
            new AntigravityComponentStatus { Key = "LanguageServer", Path = Write("server/resources/bin/language_server.exe") },
            new AntigravityComponentStatus { Key = "Ide", Path = Write("ide/resources/app/out/main.js") }
        };
        Assert.Equal(Path.Combine(_root, "server", "Antigravity.exe"), MainViewModel.ResolvePatcherLauncher(null, components));
        Assert.Equal(ide, MainViewModel.ResolvePatcherLauncher(Path.Combine(_root, "ide"), components));
        Assert.Equal(ide, MainViewModel.ResolvePatcherLauncher(null, components.Where(c => c.Key == "Ide")));
        Assert.Null(MainViewModel.ResolvePatcherLauncher(Path.Combine(_root, "missing"), components));
        Assert.Null(MainViewModel.ResolvePatcherLauncher("bad\0path", components));
        Assert.Equal(Path.Combine(_root, "server", "Antigravity.exe"), MainViewModel.ResolvePatcherLauncher(Path.Combine(_root, "server"), components));
    }

    [Fact]
    public void ProcessOwnershipRejectsUnknownPathsSiblingPrefixesAndUnrelatedLanguageServers()
    {
        Write("install/Antigravity.exe");
        string component = Write("install/resources/bin/language_server.exe");
        Assert.True(AntigravityProcesses.BelongsTo(Path.Combine(_root, "install", "Antigravity.exe"), [component]));
        Assert.True(AntigravityProcesses.BelongsTo(component, [component]));
        Assert.False(AntigravityProcesses.BelongsTo(null, [component]));
        Assert.False(AntigravityProcesses.BelongsTo(Path.Combine(_root, "install-other", "language_server.exe"), [component]));
        Assert.False(AntigravityProcesses.BelongsTo("C:\\unrelated\\language_server.exe", [component]));
    }

    [Fact]
    public void StandaloneComponentDoesNotAuthorizeClosingTheEntireParentDirectory()
    {
        string component = Write("standalone/agy.exe");
        Assert.True(AntigravityProcesses.BelongsTo(component, [component]));
        Assert.False(AntigravityProcesses.BelongsTo(Path.Combine(_root, "standalone", "language_server.exe"), [component]));
    }

    [Fact]
    public void CacheCleanupWithNoVerifiedInstallationDoesNotDeleteAnyProfile()
    {
        string component = Write("fixture/main.js");
        string cache = Write("roaming/Antigravity/Code Cache/keep.bin");
        AntigravityPatchEngine.ClearV8Caches(component, Path.Combine(_root, "roaming"));
        Assert.True(File.Exists(cache));
    }

    [Fact]
    public void ProxyDiagnosticSeparatesSavedAndInheritedSettingsAndRedactsSecrets()
    {
        string diagnostic = AntigravityProxyDiagnostic.Describe("http://alice:secret@127.0.0.1:53129/private?token=abc", "http://localhost:8080", null);
        Assert.Contains("127.0.0.1:53129", diagnostic);
        Assert.Contains("различаются", diagnostic);
        Assert.DoesNotContain("alice", diagnostic);
        Assert.DoesNotContain("secret", diagnostic);
        Assert.DoesNotContain("private", diagnostic);
        Assert.DoesNotContain("abc", diagnostic);
        Assert.Contains("не задан", AntigravityProxyDiagnostic.Describe(null, null, null));
        Assert.Contains("неподдерживаемый", AntigravityProxyDiagnostic.Describe("socks5://secret", null, null));
        Assert.Contains("localhost:8080", AntigravityProxyDiagnostic.Describe(null, null, "http://localhost:8080"));
    }

    [Fact]
    public void DirectLaunchStripsOnlyChildProxyAndUsesDurableOutputHandles()
    {
        string exe = Write("space & percent%/Antigravity.exe");
        string? previous = Environment.GetEnvironmentVariable("AG_LS_PROXY");
        try
        {
            Environment.SetEnvironmentVariable("AG_LS_PROXY", "http://127.0.0.1:53129");
            var direct = AntigravityLauncher.BuildStartInfo(exe, true);
            Assert.False(direct.UseShellExecute);
            Assert.True(direct.CreateNoWindow);
            Assert.False(direct.Environment.ContainsKey("AG_LS_PROXY"));
            Assert.Equal(exe, direct.Environment["GX_ANTIGRAVITY_EXECUTABLE"]);
            Assert.DoesNotContain(exe, direct.Arguments);
            Assert.Contains("1>NUL 2>NUL", direct.Arguments);
            Assert.Equal("http://127.0.0.1:53129", Environment.GetEnvironmentVariable("AG_LS_PROXY"));
            var inherited = AntigravityLauncher.BuildStartInfo(exe, false);
            Assert.False(inherited.UseShellExecute);
            Assert.Equal("http://127.0.0.1:53129", inherited.Environment["AG_LS_PROXY"]);
        }
        finally { Environment.SetEnvironmentVariable("AG_LS_PROXY", previous); }
    }

    [Fact]
    public async Task DirectLaunchRunsQuotedFixtureAndSurvivesLauncherDisposal()
    {
        string script = Write("spaces & percent%/child.cmd");
        File.WriteAllText(script, "@echo off\r\necho %AG_LS_PROXY%>\"%~dp0child-env.txt\"\r\necho done>\"%~dp0finished.txt\"\r\n");
        using var child = System.Diagnostics.Process.Start(AntigravityLauncher.BuildStartInfo(script, true));
        Assert.NotNull(child);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await child.WaitForExitAsync(timeout.Token);
        Assert.Equal(0, child.ExitCode);
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(script)!, "finished.txt")));
        Assert.DoesNotContain("53129", File.ReadAllText(Path.Combine(Path.GetDirectoryName(script)!, "child-env.txt")));
    }

    [Fact]
    public async Task IdeModeMarksLanguageServerNotRequiredAndNamesComponentsAccurately()
    {
        string ideRoot = Path.Combine(_root, "ide-install");
        Directory.CreateDirectory(ideRoot);
        File.WriteAllText(Path.Combine(ideRoot, "Antigravity IDE.exe"), "fixture");
        Write("ide-install/resources/app/out/main.js");
        Write("ide-install/resources/app/extensions/agy/vscode/extension.js");

        var status = await AntigravityPatcherService.GetStatusAsync(default, ideRoot);
        Assert.Equal("NotRequired", status.LanguageServer.State);
        Assert.Equal("Не требуется", status.LanguageServer.PatchLabel);
        Assert.Equal("Antigravity IDE · Language Server", status.LanguageServer.Name);
        Assert.Contains("agy --hub", status.LanguageServer.Details);
        Assert.Equal("Antigravity IDE · main.js", status.Ide.Name);
        Assert.Equal("Antigravity IDE · расширение agy", status.VsCode.Name);
        Assert.Equal("Antigravity IDE · CLI (agy)", status.Agy.Name);
    }

    [Fact]
    public async Task DesktopModeNamesAppAsarClearly()
    {
        string desktopRoot = Path.Combine(_root, "desktop-install");
        Directory.CreateDirectory(desktopRoot);
        File.WriteAllText(Path.Combine(desktopRoot, "Antigravity.exe"), "fixture");
        Write("desktop-install/resources/bin/language_server.exe");
        Write("desktop-install/resources/app.asar");

        var status = await AntigravityPatcherService.GetStatusAsync(default, desktopRoot);
        Assert.Equal("Antigravity · Language Server", status.LanguageServer.Name);
        Assert.Equal("Antigravity · app.asar", status.Ide.Name);
        Assert.Equal("Расширение VS Code", status.VsCode.Name);
        Assert.Equal("Antigravity CLI · agy", status.Agy.Name);
    }

    [Fact]
    public void HubInstallerExtensionSelectsGeminiBinAgyEvenIfOtherCliExistsFirst()
    {
        string ideRoot = Path.Combine(_root, "ide-with-cli");
        Directory.CreateDirectory(ideRoot);
        File.WriteAllText(Path.Combine(ideRoot, "Antigravity IDE.exe"), "fixture");
        Write("ide-with-cli/resources/app/extensions/agy/vscode/extension.js");

        string strayAgyDir = Path.Combine(_root, "stray-agy", "bin");
        Directory.CreateDirectory(strayAgyDir);
        string strayCli = Path.Combine(strayAgyDir, "agy.exe");
        File.WriteAllText(strayCli, "stray");

        string geminiDir = Path.Combine(_root, "gemini-bin");
        Directory.CreateDirectory(geminiDir);
        string targetCli = Path.Combine(geminiDir, "agy.exe");
        File.WriteAllText(targetCli, "target");

        var found = AntigravityDiscovery.Find(manualRoot: ideRoot, searchRoots: new[] { strayAgyDir }, sharedCliRoot: geminiDir);
        Assert.True(found.ContainsKey("Agy"));
        Assert.Equal(Path.GetFullPath(targetCli), found["Agy"]);
    }
}
