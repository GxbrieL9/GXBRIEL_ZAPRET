using System.Buffers.Binary;
using System.IO;
using System.Text;
using GxbrielZapret.Services.Infrastructure;
using GxbrielZapret.Services.Platform;
using Xunit;

namespace GxbrielZapret.Tests;

public sealed class AntigravityPatcherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gx-ag-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previous = Environment.GetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT");
    private static readonly byte[] Manager = [0x80,0x78,0x08,0x00,0x74,0x0a,0x48,0x8b,0x11,0x24,0x22,0x48,0x89,0x33,0x60];
    private static readonly byte[] Cli = [0x48,0x85,0xc0,0x0f,0x84,1,2,3,4,0x80,0x78,8,0,0x0f,0x85,5,6,7,8];
    private const string Ide = "let a=(resetIsTierGCPTos(),this.account.isGoogleInternal);";
    private const string Extension = "async function install(options){outputChannel.appendLine('[INSTALL] Checking Antigravity releases...');const isChannelChanged = manifestFetched && lastInstalledUrl !== releaseBaseUrl;}";
    public AntigravityPatcherTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT", Path.Combine(_root, "data"));
    }
    public void Dispose() => Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT", _previous);
    private string Write(string name, byte[] bytes) { string path = Path.Combine(_root, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes); return path; }
    private string Write(string name, string text) => Write(name, Encoding.UTF8.GetBytes(text));

    private static byte[] Pe(byte[] payload, bool executable = true, ushort machine = 0x8664)
    {
        int raw = (payload.Length + 511) / 512 * 512;
        var data = new byte[512 + raw];
        void W16(int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(at), value);
        void W32(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), value);
        data[0] = (byte)'M'; data[1] = (byte)'Z'; W32(0x3c, 0x80); W32(0x80, 0x4550);
        W16(0x84, machine); W16(0x86, 1); W16(0x94, 240); W16(0x96, 0x22);
        W16(0x98, 0x20b); W32(0xa8, 0x1000); W32(0xb8, 0x1000); W32(0xbc, 0x200);
        W32(0xd0, (uint)(0x1000 + ((raw + 4095) / 4096 * 4096))); W32(0xd4, 0x200); W16(0xdc, 3); W32(0x104, 16);
        Encoding.ASCII.GetBytes(".text").CopyTo(data, 0x188);
        W32(0x190, (uint)payload.Length); W32(0x194, 0x1000); W32(0x198, (uint)raw); W32(0x19c, 0x200);
        W32(0x1ac, executable ? 0x60000020u : 0x40000040u);
        payload.CopyTo(data, 512);
        return data;
    }
    private static AntigravityPatchPlan Analyze(byte[] data, string key) { using var stream = new MemoryStream(data); return AntigravityPatchEngine.Analyze(stream, key); }

    [Theory]
    [InlineData("inexigible AG_LS_PROXY", "ExternalPatch")]
    [InlineData("inexigible https_proxy", "ExternalPartial")]
    [InlineData("ineligible AG_LS_PROXY", "ExternalPartial")]
    [InlineData("ineligible inexigible https_proxy AG_LS_PROXY", "ExternalPartial")]
    public void AlternativeMarkersAreReadOnlyAndNeverReceiveGxPatch(string markers, string state)
    {
        byte[] data = Pe(Manager.Concat(Encoding.ASCII.GetBytes(markers)).ToArray());
        Assert.Equal(state, Analyze(data, "LanguageServer").State);
        string path = Write("external.exe", data);
        Assert.Throws<InvalidOperationException>(() => AntigravityPatchEngine.Apply(path, "LanguageServer", CancellationToken.None));
        Assert.Equal(data, File.ReadAllBytes(path));
    }

    [Fact]
    public void AlternativeMarkersAcrossChunkBoundaryAreFoundAndStreamPositionIsRestored()
    {
        byte[] data = new byte[65600];
        Encoding.ASCII.GetBytes("inexigible AG_LS_PROXY").CopyTo(data, 65533);
        using var stream = new MemoryStream(data);
        stream.Position = 10;
        Assert.Equal("ExternalPatch", AntigravityPatchEngine.InspectAlternativePatch(stream)!.State);
        Assert.Equal(10, stream.Position);
    }

    [Fact]
    public void BlockedMutationPreservesVerifiedFileStateAndBackup()
    {
        string path = Write("blocked.exe", Pe(Manager));
        AntigravityPatchEngine.Apply(path, "LanguageServer", CancellationToken.None);
        string hash = AntigravityPatchEngine.Hash(path);
        var component = new AntigravityComponentStatus { Key = "LanguageServer", Path = path };
        AntigravityPatcherService.RecordOperationFailure(component, "Закройте Antigravity перед изменением файлов.");
        Assert.Equal("Patched", component.State);
        Assert.True(component.HasBackup);
        Assert.True(component.CanRestore);
        Assert.Contains("Операция не выполнена", component.ActionExplanation);
        Assert.Equal(hash, AntigravityPatchEngine.Hash(path));
        File.Delete(path);
        AntigravityPatcherService.RecordOperationFailure(component, "Файл исчез");
        Assert.Equal("Error", component.State);
        Assert.False(component.HasBackup);
    }

    [Theory]
    [InlineData("LanguageServer")]
    [InlineData("Agy")]
    public void BinaryPatchIsExactSameSizeIdempotentAndReversible(string key)
    {
        byte[] original = Pe(key == "Agy" ? Cli : Manager);
        string path = Write(key + ".exe", original);
        Assert.Equal("Unpatched", Analyze(original, key).State);
        AntigravityPatchEngine.Apply(path, key, CancellationToken.None);
        Assert.Equal(original.Length, new FileInfo(path).Length);
        Assert.Equal("Patched", Analyze(File.ReadAllBytes(path), key).State);
        string hash = AntigravityPatchEngine.Hash(path);
        AntigravityPatchEngine.Apply(path, key, CancellationToken.None);
        Assert.Equal(hash, AntigravityPatchEngine.Hash(path));
        Assert.True(AntigravityPatchEngine.CanRestore(path, key));
        AntigravityPatchEngine.Restore(path, key, CancellationToken.None);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(AntigravityPatchEngine.CanRestore(path, key));
    }

    [Fact]
    public void DuplicateExecutableMatchesCannotBePatched()
    {
        string path = Write("language_server.exe", Pe(Manager.Concat(Manager).ToArray()));
        string before = AntigravityPatchEngine.Hash(path);
        Assert.Throws<InvalidOperationException>(() => AntigravityPatchEngine.Apply(path, "LanguageServer", CancellationToken.None));
        Assert.Equal(before, AntigravityPatchEngine.Hash(path));
        Assert.False(Directory.Exists(Path.Combine(AppPaths.AppDataRoot, "antigravity")));
    }

    [Theory]
    [InlineData(false, 0x8664)]
    [InlineData(true, 0xaa64)]
    public void DataSectionsAndUnsupportedArchitecturesAreRejected(bool executable, int machine) =>
        Assert.Equal("Unknown", Analyze(Pe(Manager, executable, (ushort)machine), "LanguageServer").State);

    [Fact]
    public void PatternAcrossScanChunkBoundaryIsFoundOnce()
    {
        byte[] payload = new byte[65530 + Manager.Length];
        Manager.CopyTo(payload, 65530);
        var result = Analyze(Pe(payload), "LanguageServer");
        Assert.Equal("Unpatched", result.State);
        Assert.Single(result.Changes);
        Assert.Equal(512 + 65530, result.Changes[0].Offset);
    }

    [Theory]
    [InlineData("Ide", "// UNLOCKED")]
    [InlineData("Ide", "isGoogleInternal")]
    [InlineData("VsCode", "Skipping version check and re-download.")]
    [InlineData("VsCode", "const isChannelChanged = false;")]
    [InlineData("VsCode", "const isChannelChanged = false; // Skipping version check and re-download.")]
    public void LooseMarkersNeverCountAsACompletePatch(string key, string text) =>
        Assert.Equal("Unknown", AntigravityPatchEngine.AnalyzeText(text, key).State);

    [Fact]
    public void PartialExtensionIsCompletedOnlyWhenTheOtherSignatureIsRecognized()
    {
        var complete = AntigravityPatchEngine.AnalyzeText(Extension, "VsCode");
        string partial = Encoding.UTF8.GetString(complete.Text!).Replace("const isChannelChanged = false;", "const isChannelChanged = manifestFetched && lastInstalledUrl !== releaseBaseUrl;");
        var plan = AntigravityPatchEngine.AnalyzeText(partial, "VsCode");
        Assert.Equal("Partial", plan.State);
        Assert.Equal("Patched", AntigravityPatchEngine.AnalyzeText(Encoding.UTF8.GetString(plan.Text!), "VsCode").State);
    }

    [Theory]
    [InlineData("Ide")]
    [InlineData("VsCode")]
    public void TextPatchesPreserveBomAndRestoreOriginalBytes(string key)
    {
        byte[] original = new byte[] {0xef,0xbb,0xbf}.Concat(Encoding.UTF8.GetBytes(key == "Ide" ? Ide : Extension)).ToArray();
        string path = Write(key + ".js", original);
        AntigravityPatchEngine.Apply(path, key, CancellationToken.None);
        Assert.Equal(new byte[] {0xef,0xbb,0xbf}, File.ReadAllBytes(path)[..3]);
        string? audit = Environment.GetEnvironmentVariable("GXBRIEL_ZAPRET_PATCHER_AUDIT_DIR");
        if (key == "VsCode" && !string.IsNullOrWhiteSpace(audit))
        {
            Directory.CreateDirectory(audit);
            File.Copy(path, Path.Combine(audit, "extension-patched.js"), true);
        }
        Assert.True(AntigravityPatchEngine.CanRestore(path, key));
        AntigravityPatchEngine.Restore(path, key, CancellationToken.None);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void AnUpdatedComponentCannotBeOverwrittenByAnOldBackup()
    {
        string path = Write("main.js", Ide);
        AntigravityPatchEngine.Apply(path, "Ide", CancellationToken.None);
        File.WriteAllText(path, "new upstream version");
        Assert.False(AntigravityPatchEngine.CanRestore(path, "Ide"));
        Assert.Throws<InvalidOperationException>(() => AntigravityPatchEngine.Restore(path, "Ide", CancellationToken.None));
        Assert.Equal("new upstream version", File.ReadAllText(path));
    }

    [Fact]
    public void CorruptBackupIsRejected()
    {
        string path = Write("main.js", Ide);
        AntigravityPatchEngine.Apply(path, "Ide", CancellationToken.None);
        string backup = Directory.GetFiles(AppPaths.AppDataRoot, "*.bin", SearchOption.AllDirectories).Single();
        File.WriteAllText(backup, "broken");
        Assert.False(AntigravityPatchEngine.CanRestore(path, "Ide"));
        Assert.Throws<InvalidOperationException>(() => AntigravityPatchEngine.Restore(path, "Ide", CancellationToken.None));
    }

    [Fact]
    public void CancellationBeforeCommitLeavesTheFileUntouched()
    {
        string path = Write("main.js", Ide);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => AntigravityPatchEngine.Apply(path, "Ide", stop.Token));
        Assert.Equal(Ide, File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task MissingFilesAndUnknownVersionsCannotProduceSuccessNotifications()
    {
        var missing = await AntigravityPatcherService.PatchAsync(root: _root);
        Assert.False(missing.Succeeded);
        Assert.Equal(0, missing.ChangedCount);
        Write("main.js", "// UNLOCKED");
        var unknown = await AntigravityPatcherService.PatchAsync("Ide", root: _root);
        Assert.False(unknown.Succeeded);
        Assert.Equal(0, unknown.ChangedCount);
    }

    [Fact]
    public async Task ManualScopeStatusIsReadOnlyAndSelectivePatchDoesNotModifyOtherFiles()
    {
        string path = Write("resources/app/out/main.js", Ide);
        string other = Write("resources/bin/language_server.exe", Pe(Manager));
        string otherHash = AntigravityPatchEngine.Hash(other);
        var status = await AntigravityPatcherService.GetStatusAsync(root: _root);
        Assert.Equal("Unpatched", status.Ide.State);
        Assert.False(Directory.Exists(AppPaths.AppDataRoot));
        var result = await AntigravityPatcherService.PatchAsync("Ide", root: _root);
        Assert.True(result.Succeeded);
        Assert.Equal(1, result.ChangedCount);
        Assert.Equal(otherHash, AntigravityPatchEngine.Hash(other));
        Assert.Equal("Patched", result.Ide.State);
        Assert.True((await AntigravityPatcherService.RestoreAsync(root: _root)).Succeeded);
        Assert.Equal(Ide, File.ReadAllText(path));
    }

    [Fact]
    public async Task ApplyAvailableSkipsUnsupportedComponentsWithoutTouchingThem()
    {
        Write("main.js", Ide);
        string unsupported = Write("agy.exe", Pe(Cli, machine: 0xaa64));
        string hash = AntigravityPatchEngine.Hash(unsupported);
        var result = await AntigravityPatcherService.PatchAsync(root: _root);
        Assert.True(result.Succeeded);
        Assert.Equal(1, result.ChangedCount);
        Assert.Equal("Unknown", result.Agy.State);
        Assert.Equal(hash, AntigravityPatchEngine.Hash(unsupported));
    }

    [Fact]
    public async Task UnknownTargetsAreRejectedBeforeDiscovery() =>
        await Assert.ThrowsAsync<ArgumentException>(() => AntigravityPatcherService.PatchAsync("invalid", root: _root));

    [Fact]
    public void PatcherLocalizationStringsArePresentAndTranslated()
    {
        GxbrielZapret.Localization.Loc.Init(GxbrielZapret.Localization.Loc.English);
        try
        {
            Assert.Equal("Patcher", GxbrielZapret.Localization.Loc.T("Патчер"));
            Assert.Equal("Check files", GxbrielZapret.Localization.Loc.T("Проверить файлы"));
            Assert.Equal("Apply available patches", GxbrielZapret.Localization.Loc.T("Применить доступные патчи"));
            Assert.Equal("Restore original files", GxbrielZapret.Localization.Loc.T("Восстановить исходные файлы"));
            Assert.Equal("Launch Antigravity", GxbrielZapret.Localization.Loc.T("Запустить Antigravity"));
            Assert.Equal("Stop operation", GxbrielZapret.Localization.Loc.T("Остановить операцию"));
            Assert.Equal("Installation directory", GxbrielZapret.Localization.Loc.T("Каталог установки"));
            Assert.Equal("Apply patch", GxbrielZapret.Localization.Loc.T("Применить патч"));
            Assert.Equal("Restore", GxbrielZapret.Localization.Loc.T("Откатить"));
            Assert.Equal("Clear log", GxbrielZapret.Localization.Loc.T("Очистить журнал"));
            Assert.Equal("Operation log", GxbrielZapret.Localization.Loc.T("Журнал операций"));
            Assert.Equal("Patch detected", GxbrielZapret.Localization.Loc.T("Патч обнаружен"));
            Assert.Equal("Patch available", GxbrielZapret.Localization.Loc.T("Патч доступен"));
            Assert.Equal("Partial patch", GxbrielZapret.Localization.Loc.T("Частичный патч"));
            Assert.Equal("Unknown version", GxbrielZapret.Localization.Loc.T("Неизвестная версия"));
            Assert.Equal("Check error", GxbrielZapret.Localization.Loc.T("Ошибка проверки"));
            Assert.Equal("Not found", GxbrielZapret.Localization.Loc.T("Не найден"));
            Assert.Equal("Antigravity unlocked", GxbrielZapret.Localization.Loc.T("Antigravity разблокирован"));
            Assert.Equal("Updates or new patches available", GxbrielZapret.Localization.Loc.T("Доступны обновления или новые патчи"));
            Assert.Equal("Restart Antigravity", GxbrielZapret.Localization.Loc.T("Перезапустить Antigravity"));
            Assert.Equal("Close running processes and restart Antigravity", GxbrielZapret.Localization.Loc.T("Закрыть запущенные процессы и запустить Antigravity заново"));
            Assert.Equal("Close processes", GxbrielZapret.Localization.Loc.T("Закрыть процессы"));
            Assert.Equal("Terminate all running Antigravity and Language Server processes", GxbrielZapret.Localization.Loc.T("Завершить все запущенные процессы Antigravity и Language Server"));
            Assert.Equal("Antigravity processes running", GxbrielZapret.Localization.Loc.T("Запущены процессы Antigravity"));
        }
        finally
        {
            GxbrielZapret.Localization.Loc.Init(GxbrielZapret.Localization.Loc.Russian);
        }
    }

    [Fact]
    public void ClearV8CachesExecutesSafely()
    {
        string install = Path.Combine(_root, "install");
        Write("install/Antigravity IDE.exe", "fixture");
        string component = Write("install/resources/app/out/main.js", Ide);
        string cache = Write("roaming/Antigravity IDE/Code Cache/old.bin", "cache");
        string other = Write("roaming/Antigravity/Code Cache/keep.bin", "other installation");
        string settings = Write("roaming/Antigravity IDE/User/settings.json", "settings");
        AntigravityPatchEngine.ClearV8Caches(component, Path.Combine(_root, "roaming"));
        Assert.False(File.Exists(cache));
        Assert.True(File.Exists(other));
        Assert.True(File.Exists(settings));
    }

    [Fact]
    public async Task ProcessMethodsExecuteSafely()
    {
        var names = AntigravityPatcherService.GetRunningProcessNames(_root);
        Assert.NotNull(names);
        var remaining = await AntigravityPatcherService.RequestCloseProcessesAsync(_root);
        Assert.Empty(remaining);
        Assert.Empty(await AntigravityProcesses.TerminateAsync(_root, CancellationToken.None));
    }

    [Fact]
    public async Task PackedInterfaceIsFoundButNeverPatchedOrReportedMissing()
    {
        Write("packed/Antigravity.exe", "fixture");
        string archive = Write("packed/resources/app.asar", "opaque archive fixture");
        string root = Path.Combine(_root, "packed");
        var status = await AntigravityPatcherService.GetStatusAsync(root: root);
        Assert.Equal("PackedUnsupported", status.Ide.State);
        Assert.True(status.Ide.IsFound);
        Assert.False(status.Ide.CanPatch);
        Assert.False(status.Ide.CanRestore);
        Assert.Contains("Региональная доступность", status.Summary);
        var patch = await AntigravityPatcherService.PatchAsync("Ide", root: root);
        Assert.False(patch.Succeeded);
        Assert.Equal(0, patch.ChangedCount);
        Assert.Equal("opaque archive fixture", File.ReadAllText(archive));
    }

    private static byte[] RegionPayload() => Encoding.Latin1.GetBytes("AuthResult\0\n\nineligible\x18\x03 gap R\nineligible\x12 gap name=ineligible,proto3,oneof unrelated ineligible_tiers");
    private static byte[] CliRegionPayload() => RegionPayload().Concat(Encoding.Latin1.GetBytes(
        AntigravityCodeAssistSchema.Descriptor + " gap " + AntigravityCodeAssistSchema.Tag)).ToArray();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CliRequiresBothAuthGateAndRegionSchemaAndRestoresOriginal(bool legacyPatched)
    {
        byte[] code = Cli.ToArray();
        if (legacyPatched) new byte[] { 0x48, 0x85, 0xc0, 0x90 }.CopyTo(code, 9);
        byte[] data = Pe(code);
        Array.Resize(ref data, 1536);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x86), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0xd0), 0x3000);
        Encoding.ASCII.GetBytes(".rdata").CopyTo(data, 0x1b0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x1b8), 512);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x1bc), 0x2000);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x1c0), 512);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x1c4), 1024);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x1d4), 0x40000040);
        CliRegionPayload().CopyTo(data, 1024);
        string path = Write("cli-schema.exe", data);
        var plan = Analyze(data, "Agy");
        Assert.Equal(legacyPatched ? "Partial" : "Unpatched", plan.State);
        Assert.Equal("cli-eligibility-schema-v2", plan.RuleId);
        Assert.Equal(legacyPatched ? 5 : 6, plan.Changes.Count);
        if (!legacyPatched)
            Assert.Contains(plan.Changes, c => c.Offset == 512 + 9 && c.Bytes.SequenceEqual(new byte[] { 0x48, 0x85, 0xc0, 0x90 }));
        AntigravityPatchEngine.Apply(path, "Agy", default);
        byte[] patched = File.ReadAllBytes(path);
        Assert.Equal("Patched", Analyze(patched, "Agy").State);
        Assert.Equal(code.Take(9), patched.Skip(512).Take(9));
        Assert.Contains("unrelated ineligible_tiers", Encoding.Latin1.GetString(patched));
        AntigravityPatchEngine.Restore(path, "Agy", default);
        Assert.Equal(data, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65533)]
    [InlineData(170021)]
    public void StructuralRegionRuleSurvivesRelocationAndRestoresExactOriginal(int padding)
    {
        byte[] payload = new byte[padding + RegionPayload().Length];
        RegionPayload().CopyTo(payload, padding);
        byte[] original = Pe(payload, executable: false);
        string path = Write("region-" + padding + ".exe", original);
        Assert.Equal("Unpatched", Analyze(original, "LanguageServer").State);
        AntigravityPatchEngine.Apply(path, "LanguageServer", CancellationToken.None);
        Assert.Equal("Patched", Analyze(File.ReadAllBytes(path), "LanguageServer").State);
        Assert.Equal(original.Length, new FileInfo(path).Length);
        Assert.Contains("unrelated ineligible_tiers", Encoding.Latin1.GetString(File.ReadAllBytes(path)));
        string hash = AntigravityPatchEngine.Hash(path);
        AntigravityPatchEngine.Apply(path, "LanguageServer", CancellationToken.None);
        Assert.Equal(hash, AntigravityPatchEngine.Hash(path));
        AntigravityPatchEngine.Restore(path, "LanguageServer", CancellationToken.None);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("LanguageServer")]
    [InlineData("Agy")]
    public void StructuralRegionRuleRejectsDuplicatesAndIncompleteSchemas(string component)
    {
        Assert.Equal("Unknown", Analyze(Pe(RegionPayload().Concat(RegionPayload()).ToArray(), false), component).State);
        Assert.Equal("Unknown", Analyze(Pe(Encoding.Latin1.GetBytes("\n\nineligible\x18\x03"), false), component).State);
        byte[] partial = Pe(Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(component == "Agy" ? CliRegionPayload() : RegionPayload())
            .Replace("name=ineligible,", "name=gxeligible,")), false);
        Assert.Equal("Partial", Analyze(partial, component).State);
        string path = Write("partial-region.exe", partial);
        AntigravityPatchEngine.Apply(path, component, CancellationToken.None);
        Assert.Equal("Patched", Analyze(File.ReadAllBytes(path), component).State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65510)]
    public void CliSchemaRoundTripsAcrossChunkBoundariesAndPreservesOtherTierStrings(int padding)
    {
        byte[] payload = new byte[padding + CliRegionPayload().Length];
        CliRegionPayload().CopyTo(payload, padding);
        byte[] original = Pe(payload, false);
        string path = Write("cli-relocated.exe", original);
        AntigravityPatchEngine.Apply(path, "Agy", default);
        byte[] patched = File.ReadAllBytes(path);
        Assert.Equal("Patched", Analyze(patched, "Agy").State);
        Assert.Equal(original.Length, patched.Length);
        Assert.Contains("unrelated ineligible_tiers", Encoding.Latin1.GetString(patched));
        AntigravityPatchEngine.Restore(path, "Agy", default);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void CliWithMissingDuplicateOrChangedCodeAssistSchemaIsNeverFullyPatched()
    {
        Assert.Equal("Unknown", Analyze(Pe(RegionPayload(), false), "Agy").State);
        Assert.Equal("Unknown", Analyze(Pe(CliRegionPayload().Concat(Encoding.Latin1.GetBytes(AntigravityCodeAssistSchema.Tag)).ToArray(), false), "Agy").State);
        byte[] changed = Pe(Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(CliRegionPayload()).Replace("bytes,5,rep", "bytes,6,rep")
            .Replace("name=ineligible_tiers,json=", "name=other_elig_tiers,json=")), false);
        Assert.Equal("Unknown", Analyze(changed, "Agy").State);
        byte[] oldSchemaOnly = Pe(Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(CliRegionPayload())
            .Replace("\n\nineligible\x18\x03", "\n\ngxeligible\x18\x03").Replace("R\nineligible\x12", "R\ngxeligible\x12")
            .Replace("name=ineligible,", "name=gxeligible,")), false);
        Assert.Equal("Partial", Analyze(oldSchemaOnly, "Agy").State);
    }

    [Theory]
    [InlineData(false, "LanguageServer")]
    [InlineData(true, "LanguageServer")]
    [InlineData(false, "Agy")]
    [InlineData(true, "Agy")]
    public void PartialUpgradePreservesTrueOriginalAndLockedWriteKeepsPriorManifest(bool locked, string component)
    {
        byte[] original = Pe(component == "Agy" ? CliRegionPayload() : RegionPayload(), false);
        var initial = Analyze(original, component);
        string path = Write("upgrade.exe", original);
        AntigravityPatchEngine.Apply(path, component, CancellationToken.None);
        byte[] partial = File.ReadAllBytes(path);
        int at = checked((int)initial.Changes[0].Offset);
        Array.Copy(original, at, partial, at, 10);
        File.WriteAllBytes(path, partial);
        string manifest = Directory.GetFiles(AppPaths.AppDataRoot, "current.json", SearchOption.AllDirectories)
            .Single(file => File.ReadAllText(file).Contains("upgrade.exe", StringComparison.Ordinal));
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifest))!;
        json["PatchedHash"] = AntigravityPatchEngine.Hash(path);
        File.WriteAllText(manifest, json.ToJsonString());
        string savedManifest = File.ReadAllText(manifest);
        Assert.True(AntigravityPatchEngine.CanRestore(path, component));
        if (locked)
        {
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.ThrowsAny<IOException>(() => AntigravityPatchEngine.Apply(path, component, CancellationToken.None));
            Assert.Equal(partial, File.ReadAllBytes(path));
            Assert.Equal(savedManifest, File.ReadAllText(manifest));
        }
        else
        {
            AntigravityPatchEngine.Apply(path, component, CancellationToken.None);
            Assert.Equal("Patched", Analyze(File.ReadAllBytes(path), component).State);
        }
        Assert.True(AntigravityPatchEngine.CanRestore(path, component));
        AntigravityPatchEngine.Restore(path, component, CancellationToken.None);
        Assert.Equal(original, File.ReadAllBytes(path));
    }
}
