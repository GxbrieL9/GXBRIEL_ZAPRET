using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using GxbrielZapret.Models;
using GxbrielZapret.Services.Engine;
using GxbrielZapret.Services.Infrastructure;
using GxbrielZapret.Services.Strategies;
using Xunit;

namespace GxbrielZapret.Tests;

public class StageCReferenceStrategiesAndListsTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public StageCReferenceStrategiesAndListsTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;
    // =========================================================================
    // Helper: redirect runtime to a temp folder for tests that need write access
    // =========================================================================

    /// <summary>
    /// Redirects GXBRIEL_ZAPRET_PROGRAMDATA_ROOT and GXBRIEL_ZAPRET_APPDATA_ROOT to
    /// a temp directory for the duration of the using-block, then cleans up.
    /// This avoids UnauthorizedAccessException when writing to C:\ProgramData without elevation.
    /// </summary>
    private sealed class TempRuntimeScope : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _prevProgramDataRoot;
        private readonly string? _prevAppDataRoot;

        public TempRuntimeScope()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "gx_test_runtime_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);

            _prevProgramDataRoot = Environment.GetEnvironmentVariable("GXBRIEL_ZAPRET_PROGRAMDATA_ROOT");
            _prevAppDataRoot = Environment.GetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT");

            Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_PROGRAMDATA_ROOT", _tempRoot);
            Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT", _tempRoot);

            // Reset cached static paths so next access picks up the new env vars
            AppPaths.ResetSystemRuntimeDirCache();
            AppPaths.ResetEngineDirCache();
        }

        public string Root => _tempRoot;

        public void Dispose()
        {
            // Restore original env vars and invalidate cache
            if (_prevProgramDataRoot == null)
                Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_PROGRAMDATA_ROOT", null);
            else
                Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_PROGRAMDATA_ROOT", _prevProgramDataRoot);

            if (_prevAppDataRoot == null)
                Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT", null);
            else
                Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT", _prevAppDataRoot);

            AppPaths.ResetSystemRuntimeDirCache();
            AppPaths.ResetEngineDirCache();

            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }
    }
    // =========================================================================
    // 1. Reference Strategy Catalog Integrity (Exactly 39 strategies)
    // =========================================================================

    [Fact]
    public void ReferenceCatalog_ContainsExactly39Strategies_WithDistinctIdsAndBaselines()
    {
        var all = ReferenceStrategyCatalog.All;
        Assert.Equal(47, all.Count);
        Assert.Equal(39, ReferenceStrategyCatalog.Baseline.Count);

        var builtIns = ReferenceStrategyCatalog.BuiltIns;
        Assert.Equal(9, builtIns.Count);

        var candidates = ReferenceStrategyCatalog.CatalogCandidates;
        Assert.Equal(38, candidates.Count);

        // Verify ID prefixing and continuity
        for (int i = 1; i <= 9; i++)
        {
            string expectedId = $"GX-B{i:D2}";
            Assert.Contains(builtIns, b => b.Id == expectedId);
        }

        for (int i = 1; i <= 30; i++)
        {
            string expectedId = $"GX-C{i:D2}";
            Assert.Contains(candidates, c => c.Id == expectedId);
        }

        // All IDs must be unique
        var distinctIds = all.Select(s => s.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(47, distinctIds.Count);

        // All names must be non-empty and unique
        var distinctNames = all.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(47, distinctNames.Count);

        // All fingerprints must be unique
        var distinctFingerprints = all.Select(s => s.Fingerprint).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(47, distinctFingerprints.Count);

        // Exactly one recommended strategy (GX-B01)
        var recommended = all.Where(s => s.IsRecommended).ToList();
        Assert.Single(recommended);
        Assert.Equal("GX-B01", recommended[0].Id);
        Assert.Equal("Комбо (рекомендуемый)", recommended[0].Name);
    }

    [Fact]
    public void ReferenceCatalog_AllEntries_HaveValidProvenance_TargetServices_AndArgs()
    {
        string[] validProvenances =
        {
            "ZAPRET2UI_ADAPTED",
            "GX_CUSTOM",
            "FLOWSEAL_ADAPTED",
            "UPSTREAM_ZAPRET2_ADAPTED",
            "UPSTREAM_ZAPRET2_STANDARD_20261017",
            "YOUTUBEDISCORD_ADAPTED"
        };

        foreach (var s in ReferenceStrategyCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Id), $"ID missing for {s.Name}");
            Assert.False(string.IsNullOrWhiteSpace(s.Name), $"Name missing for {s.Id}");
            Assert.False(string.IsNullOrWhiteSpace(s.Tagline), $"Tagline missing for {s.Id}");
            Assert.False(string.IsNullOrWhiteSpace(s.Description), $"Description missing for {s.Id}");
            Assert.Contains(s.Provenance, validProvenances);
            Assert.False(string.IsNullOrWhiteSpace(s.TargetServices), $"TargetServices missing for {s.Id}");
            Assert.NotEmpty(s.CompatibleLists);
            Assert.NotEmpty(s.Args);
            Assert.Equal(64, s.Fingerprint.Length);
        }
    }

    // =========================================================================
    // 2. PresetService Seeding & Immutability Protection
    // =========================================================================

    [Fact]
    public void PresetService_LoadsAll39ReferenceStrategies_WithStableIdsAndProperties()
    {
        var service = new PresetService();
        Assert.Equal(47, service.ReferenceStrategies.Count);

        var refPresets = service.ReferencePresets;
        Assert.Equal(47, refPresets.Count);

        foreach (var refDef in ReferenceStrategyCatalog.All)
        {
            var p = service.FindByFingerprint(refDef.Fingerprint);
            Assert.NotNull(p);
            Assert.Equal(refDef.Id, p.ReferenceId);
            Assert.Equal(refDef.Provenance, p.Provenance);
            Assert.Equal(refDef.TargetServices, p.TargetServices);
            Assert.Equal(refDef.Name, p.Name);
            Assert.Equal(refDef.Tagline, p.Tagline);
            Assert.True(p.IsBuiltIn);
            Assert.True(p.IsReferenceStrategy);
        }
    }

    [Fact]
    public void PresetService_ReferenceStrategies_AreOrderedFirst_InCanonicalSequence()
    {
        var service = new PresetService();
        var all = service.All;
        Assert.True(all.Count >= 47);

        for (int i = 0; i < 47; i++)
        {
            var expectedDef = ReferenceStrategyCatalog.All[i];
            var actualPreset = all[i];
            Assert.Equal(expectedDef.Id, actualPreset.ReferenceId);
            Assert.Equal(expectedDef.Fingerprint, actualPreset.Fingerprint);
        }
    }

    [Fact]
    public void PresetService_ReferenceStrategies_AreImmutable_AgainstDeleteAndEdit()
    {
        var service = new PresetService();
        var refPreset = service.ReferencePresets.First(p => p.ReferenceId == "GX-B01");
        string originalName = refPreset.Name;
        string originalDescription = refPreset.Description;
        var originalArgs = new List<string>(refPreset.Args);

        // 1. Attempt DeleteUser on reference preset
        service.DeleteUser(refPreset);
        Assert.NotNull(service.FindByName(originalName));
        Assert.NotNull(service.FindByFingerprint(refPreset.Fingerprint));

        // 2. Attempt UpdateUser with mutated values
        refPreset.Name = "MUTATED_NAME_SHOULD_BE_IGNORED";
        refPreset.Args = new List<string> { "--filter-tcp=80" };
        service.UpdateUser(refPreset);

        // Re-read from service
        var recheck = service.FindByFingerprint(CanonicalProfile.ComputeFingerprint(originalArgs));
        Assert.NotNull(recheck);
        Assert.Equal(originalName, recheck.Name);
        Assert.Equal(originalDescription, recheck.Description);
        Assert.Equal(originalArgs, recheck.Args);

        // 3. Attempt AddUser with duplicate fingerprint of reference preset
        var imposter = new Preset
        {
            Name = "IMPOSTER_STRATEGY",
            Description = "Imposter description",
            Tagline = "Imposter tagline",
            Args = new List<string>(originalArgs)
        };
        service.AddUser(imposter);

        var afterAdd = service.FindByFingerprint(CanonicalProfile.ComputeFingerprint(originalArgs));
        Assert.NotNull(afterAdd);
        Assert.Equal(originalName, afterAdd.Name);
        Assert.Equal(originalDescription, afterAdd.Description);
        Assert.True(afterAdd.IsBuiltIn);
        Assert.Equal("GX-B01", afterAdd.ReferenceId);
    }

    [Fact]
    public void PresetService_MoveToStatus_PreservesReferenceIdentity_AndBuiltInFlag()
    {
        var service = new PresetService();
        var refPreset = service.ReferencePresets.First(p => p.ReferenceId == "GX-B02");

        var updated = service.MoveToStatus(refPreset, "WORK", "unit_test_verified", "test_runner");
        Assert.Equal("WORK", updated.Status);
        Assert.Equal("unit_test_verified", updated.Reason);
        Assert.True(updated.IsBuiltIn, "Reference strategy must remain built-in even when moved to WORK");
        Assert.Equal("GX-B02", updated.ReferenceId);
        Assert.Equal("GX_CUSTOM", updated.Provenance);

        // Move back to BROKEN / unproven
        var reset = service.MoveToStatus(refPreset, "BROKEN", "unproven", "test_runner");
        Assert.Equal("BROKEN", reset.Status);
        Assert.True(reset.IsBuiltIn);
        Assert.Equal("GX-B02", reset.ReferenceId);
    }

    // =========================================================================
    // 3. Machine Audit: BuildArguments Parity and Zero Dev Paths
    // =========================================================================

    [Fact]
    public void ReferenceCatalog_All39Strategies_BuildValidArguments_WithoutUnresolvedTokens()
    {
        using var scope = new TempRuntimeScope();
        AppPaths.EnsureCreated();
        HostlistService.EnsureDefaultsSeeded();

        string appDataRoot = Path.GetFullPath(AppPaths.AppDataRoot);
        string systemRuntime = Path.GetFullPath(AppPaths.SystemRuntimeDir);

        foreach (var strat in ReferenceStrategyCatalog.All)
        {
            var preset = strat.ToPreset();

            // Build for preview
            var previewArgs = EngineService.BuildArguments(preset, null, forLaunch: false);
            Assert.NotEmpty(previewArgs);

            // Build for launch (materializes runtime lists)
            var launchArgs = EngineService.BuildArguments(preset, null, forLaunch: true);
            Assert.NotEmpty(launchArgs);

            foreach (var arg in launchArgs)
            {
                // Must not have unresolved macro tokens
                Assert.DoesNotContain("{FILES}", arg);
                Assert.DoesNotContain("{WF}", arg);
                Assert.DoesNotContain("{WF_TCP}", arg);
                Assert.DoesNotContain("{WF_UDP}", arg);
                Assert.DoesNotMatch(@"\{HOSTLIST(:[a-zA-Z0-9_\-]+)?\}", arg);
                Assert.DoesNotMatch(@"\{EXCLUDE(:[a-zA-Z0-9_\-]+)?\}", arg);
                Assert.DoesNotMatch(@"\{IPSET(:[a-zA-Z0-9_\-]+)?\}", arg);

                // Must NEVER contain development repository path
                Assert.DoesNotContain("!VS_MY_PROJECT", arg, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("GXBRIEL_ZAPRET\\ui", arg, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    // =========================================================================
    // 4. Machine Audit: Embedded Runtime Blobs, Filters, and Lua Parity
    // =========================================================================

    [Fact]
    public async Task EntireCatalogPassesBundledNativeValidationWithoutOpeningPacketCapture()
    {
        using var scope = new TempRuntimeScope();
        AppPaths.EnsureCreated();
        HostlistService.EnsureDefaultsSeeded();
        string discord = HostlistService.ResolveRuntimeListFilePath("discord");
        foreach (var strategy in ReferenceStrategyCatalog.All)
        {
            // On Windows even --intercept=0 opens a false WinDivert filter and needs admin.
            // --dry-run exits before driver setup; it validates CLI/files, not Lua execution.
            foreach (string validation in new[] { "--dry-run" })
            {
                var start = new System.Diagnostics.ProcessStartInfo(AppPaths.WinwsExe)
                {
                    WorkingDirectory = AppPaths.EngineDir, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                // winws2's manifest requests admin even for parsing. Validation exits before
                // opening WinDivert; invoke at the caller's existing privilege level only.
                start.Environment["__COMPAT_LAYER"] = "RunAsInvoker";
                foreach (string arg in EngineService.BuildArguments(strategy.ToPreset(), discord, disableQuic: true, forLaunch: true))
                    start.ArgumentList.Add(arg);
                start.ArgumentList.Add(validation);
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    using var process = System.Diagnostics.Process.Start(start)!;
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    try { await process.WaitForExitAsync(deadline.Token); }
                    catch { process.Kill(true); throw; }
                    string output = await stdout + await stderr;
                    // The observed Windows loader failure occurs before the parser runs.
                    // Retry that specific infrastructure failure, never a CLI/Lua rejection.
                    if (process.ExitCode == unchecked((int)0xC0000142) && output.Length == 0 && attempt < 3)
                    {
                        _output.WriteLine(strategy.Id + " loader STATUS_DLL_INIT_FAILED; retry " + attempt);
                        await Task.Delay(500);
                        continue;
                    }
                    Assert.True(process.ExitCode == 0, strategy.Id + " " + validation + " exit=" + process.ExitCode + ": " + output);
                    Assert.DoesNotContain("windivert initialized", output, StringComparison.OrdinalIgnoreCase);
                    _output.WriteLine(strategy.Id + " --dry-run exit=0, attempt=" + attempt);
                    break;
                }
                await Task.Delay(50);
            }
        }
    }

    [Fact]
    public void LegacyCandidatesCoverBothServicesEvenWithDiscordSelected()
    {
        using var scope = new TempRuntimeScope();
        AppPaths.EnsureCreated();
        HostlistService.EnsureDefaultsSeeded();
        var args = EngineService.BuildArguments(ReferenceStrategyCatalog.FindById("GX-C01")!.ToPreset(),
            HostlistService.ResolveRuntimeListFilePath("discord"), forLaunch: true);
        var listArg = Assert.Single(args.Where(a => a.StartsWith("--hostlist=") && a.EndsWith("catalog-services.txt")).Distinct());
        string text = File.ReadAllText(listArg["--hostlist=".Length..]);
        Assert.Contains("youtube.com", text);
        Assert.Contains("discord.com", text);
        Assert.DoesNotContain(args, a => a.StartsWith("--hostlist-exclude="));
    }

    [Fact]
    public void ReferenceCatalog_AllReferencedBlobsAndFilters_ExistInEmbeddedRuntimeArchive()
    {
        var asm = typeof(AppPaths).Assembly;
        using var stream = asm.GetManifestResourceStream("GXBRIEL_ZAPRET.engine.zapret2-runtime.zip");
        Assert.NotNull(stream);

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entryNames = archive.Entries
            .Select(e => e.FullName.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Required Lua libraries
        Assert.Contains("lua/zapret-lib.lua", entryNames);
        Assert.Contains("lua/zapret-antidpi.lua", entryNames);
        Assert.Contains("lua/zapret-auto.lua", entryNames);

        // Required Raw-part WinDivert filters
        Assert.Contains("windivert.filter/windivert_part.stun.txt", entryNames);
        Assert.Contains("windivert.filter/windivert_part.quic_initial_ietf.txt", entryNames);

        // Required Blobs referenced across all 39 reference strategies
        string[] requiredBlobs =
        {
            "files/fake/tls_clienthello_www_google_com.bin",
            "files/fake/quic_initial_www_google_com.bin",
            "files/fake/tls_clienthello_vk_com.bin",
            "files/fake/quic_initial_vk_com.bin",
            "files/fake/tls_clienthello_sberbank_ru.bin",
            "files/fake/tls_clienthello_gosuslugi_ru.bin"
        };

        foreach (var blob in requiredBlobs)
        {
            Assert.Contains(blob, entryNames);
        }

        // Verify that every blob or filter referenced in any reference strategy's args exists in the zip
        foreach (var strat in ReferenceStrategyCatalog.All)
        {
            foreach (var arg in strat.Args)
            {
                var blobMatches = Regex.Matches(arg, @"\{FILES\}\\fake\\([a-zA-Z0-9_\-\.]+\.bin)");
                foreach (Match m in blobMatches)
                {
                    string filename = m.Groups[1].Value;
                    string zipPath = $"files/fake/{filename}";
                    Assert.Contains(zipPath, entryNames);
                }

                var filterMatches = Regex.Matches(arg, @"\{WF\}\\([a-zA-Z0-9_\-\.]+\.txt)");
                foreach (Match m in filterMatches)
                {
                    string filename = m.Groups[1].Value;
                    string zipPath = $"windivert.filter/{filename}";
                    Assert.Contains(zipPath, entryNames);
                }
            }
        }
    }

    // =========================================================================
    // 5. Lists Architecture & Self-Healing Tests
    // =========================================================================

    [Fact]
    public void HostlistService_SourceOfTruth_And_RuntimeMaterialization_SelfHeal()
    {
        using var scope = new TempRuntimeScope();
        AppPaths.EnsureCreated();
        var hostlists = new HostlistService();
        hostlists.SeedDefaults();

        // 1. Verify source-of-truth lists exist under ListsDir
        string discordSource = Path.Combine(AppPaths.ListsDir, "list-discord.txt");
        string youtubeSource = Path.Combine(AppPaths.ListsDir, "list-youtube.txt");
        string excludeSource = Path.Combine(AppPaths.ListsDir, "list-exclude.txt");

        Assert.True(File.Exists(discordSource), "Discord source list must exist in AppData");
        Assert.True(File.Exists(youtubeSource), "YouTube source list must exist in AppData");
        Assert.True(File.Exists(excludeSource), "Exclude source list must exist in AppData");

        // 2. Materialize to runtime
        string discordRuntime = HostlistService.ResolveRuntimeListFilePath("discord");
        string youtubeRuntime = HostlistService.ResolveRuntimeListFilePath("youtube");
        string excludeRuntime = HostlistService.ResolveRuntimeExcludeFilePath("exclude");

        Assert.True(File.Exists(discordRuntime), "Discord runtime list must exist in runtime dir");
        Assert.True(File.Exists(youtubeRuntime), "YouTube runtime list must exist in runtime dir");
        Assert.True(File.Exists(excludeRuntime), "Exclude runtime list must exist in runtime dir");

        Assert.StartsWith(AppPaths.RuntimeListsDir, discordRuntime, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(AppPaths.RuntimeListsDir, youtubeRuntime, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(AppPaths.RuntimeListsDir, excludeRuntime, StringComparison.OrdinalIgnoreCase);

        // 3. Test self-healing: delete runtime list file and re-resolve
        File.Delete(discordRuntime);
        Assert.False(File.Exists(discordRuntime));

        string reResolved = HostlistService.ResolveRuntimeListFilePath("discord");
        Assert.True(File.Exists(reResolved), "Runtime list must be automatically re-materialized");
    }

    [Fact]
    public void HostlistService_Seeds_Bundled_Portable_Lol_Blitz_Lists()
    {
        using var scope = new TempRuntimeScope();

        var hostlists = new HostlistService();

        string lolPath = Path.Combine(AppPaths.ListsDir, "list-lol.txt");
        string allIpsPath = Path.Combine(AppPaths.ListsDir, "ipset-all.txt");

        Assert.True(File.Exists(lolPath), "Portable first-run must seed list-lol.txt from bundled resources.");
        Assert.True(File.Exists(allIpsPath), "Portable first-run must seed ipset-all.txt from bundled resources.");

        string lol = File.ReadAllText(lolPath);
        string allIps = File.ReadAllText(allIpsPath);

        Assert.Contains("blitz-main.blitz.gg", lol);
        Assert.Contains("entitlements.auth.riotgames.com", lol);
        Assert.Contains("169.150.247.40", allIps);
        Assert.Contains("138.199.36.9", allIps);
    }

    [Fact]
    public void PresetImporter_DuplicateOfReferenceStrategy_IsSkipped_AndDoesNotMutateCatalog()
    {
        var service = new PresetService();
        var refStrat = ReferenceStrategyCatalog.BuiltIns.First(b => b.Id == "GX-B01");

        string sandbox = Path.Combine(Path.GetTempPath(), "gx_test_import_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(sandbox);

            // Create a fake batch file with identical winws command
            string batFile = Path.Combine(sandbox, "duplicate_preset.bat");
            string batContent = "@echo off\r\nwinws2.exe " + string.Join(" ", refStrat.Args);
            File.WriteAllText(batFile, batContent);

            var result = PresetImporter.ImportFromDirectory(sandbox, service);
            Assert.Equal(1, result.TotalFilesScanned);
            Assert.Equal(1, result.SkippedDuplicates);
            Assert.Equal(0, result.NewImported);

            // Verify original reference strategy untouched
            var check = service.FindByFingerprint(refStrat.Fingerprint);
            Assert.NotNull(check);
            Assert.Equal("GX-B01", check.ReferenceId);
            Assert.Equal("Комбо (рекомендуемый)", check.Name);
            Assert.True(check.IsBuiltIn);
        }
        finally
        {
            try { Directory.Delete(sandbox, recursive: true); } catch { }
        }
    }

    [Fact]
    public void PresetImporter_PreservesIpsetAll_AsNamedIpset()
    {
        string sandbox = Path.Combine(Path.GetTempPath(), "gx_test_ipset_all_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(sandbox);
            string batFile = Path.Combine(sandbox, "ipset_all.bat");
            File.WriteAllText(batFile,
                "@echo off\r\n" +
                "winws2.exe --filter-tcp=443 --ipset=\"%LISTS%ipset-all.txt\" --dpi-desync=fake");

            var preset = PresetImporter.ParseFile(batFile);

            Assert.NotNull(preset);
            Assert.Contains("{IPSET:all}", preset!.Args);
            Assert.DoesNotContain("{IPSET}", preset.Args);
        }
        finally
        {
            try { Directory.Delete(sandbox, recursive: true); } catch { }
        }
    }
}
