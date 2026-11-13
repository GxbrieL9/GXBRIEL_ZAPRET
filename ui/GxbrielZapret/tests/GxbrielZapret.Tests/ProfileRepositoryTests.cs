using System.IO;
using System.Security.Cryptography;
using System.Text;
using Xunit;
using GxbrielZapret.Models;
using GxbrielZapret.Services.Engine;
using GxbrielZapret.Services.Infrastructure;
using GxbrielZapret.Services.Strategies;

namespace GxbrielZapret.Tests;

public class ProfileRepositoryTests
{
    [Fact]
    public void ComputeFingerprint_NormalizesWhitespaceAndCase()
    {
        var args1 = new List<string>
        {
            "--filter-tcp=443",
            "  --payload=tls_client_hello  ",
            "--lua-desync=fake:blob=tls_google:repeats=6"
        };

        var args2 = new List<string>
        {
            "--filter-tcp=443",
            "--PAYLOAD=tls_client_hello",
            "--lua-desync=fake:blob=tls_google:repeats=6",
            "" // empty line ignored
        };

        string fp1 = CanonicalProfile.ComputeFingerprint(args1);
        string fp2 = CanonicalProfile.ComputeFingerprint(args2);

        Assert.NotEmpty(fp1);
        Assert.Equal(fp1, fp2);
    }

    [Fact]
    public void ComputeFingerprint_DifferentiatesDifferentArguments()
    {
        var args1 = new List<string> { "--filter-tcp=443", "--lua-desync=fake:repeats=6" };
        var args2 = new List<string> { "--filter-tcp=443", "--lua-desync=fake:repeats=4" };

        string fp1 = CanonicalProfile.ComputeFingerprint(args1);
        string fp2 = CanonicalProfile.ComputeFingerprint(args2);

        Assert.NotEqual(fp1, fp2);
    }

    [Fact]
    public void ExtractReferencedLists_IdentifiesAllTokens()
    {
        var args = new List<string>
        {
            "--filter-tcp=443 {HOSTLIST:discord}",
            "--filter-tcp=80 {EXCLUDE:exclude}",
            "--filter-udp=443 {HOSTLIST:youtube}",
            "--filter-tcp=443 {IPSET:telegram}"
        };

        var lists = CanonicalProfile.ExtractReferencedLists(args);

        Assert.Contains("discord", lists);
        Assert.Contains("youtube", lists);
        Assert.Contains("exclude", lists);
        Assert.Contains("telegram", lists);
    }

    [Fact]
    public void CanonicalProfile_SaveAndLoad_Roundtrip()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"gx_test_profile_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var p = new Preset
            {
                Name = "Test Strategy Alpha",
                Description = "Testing strategy serialization roundtrip",
                Tagline = "Test tagline",
                Args = new List<string> { "--filter-tcp=443", "{HOSTLIST:discord}", "--lua-desync=fake:repeats=6" },
                Status = "WORK",
                Reason = "verified_active_pass",
                IsBuiltIn = false
            };

            var canonical = CanonicalProfile.FromPreset(p, "WORK", "verified_active_pass", "unit_test");
            string savedPath = canonical.SaveToDirectory(tempDir);

            Assert.True(File.Exists(savedPath));

            var loaded = CanonicalProfile.LoadFromFile(savedPath);
            Assert.NotNull(loaded);
            Assert.Equal("Test Strategy Alpha", loaded.Name);
            Assert.Equal("WORK", loaded.Status);
            Assert.Equal("verified_active_pass", loaded.Reason);
            Assert.Contains("discord", loaded.Lists);
            Assert.Equal(canonical.Fingerprint, loaded.Fingerprint);

            var reconstructed = loaded.ToPreset();
            Assert.Equal(p.Name, reconstructed.Name);
            Assert.True(reconstructed.IsWork);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void HostlistService_ResolveListFilePath_FindsPrefixedAndUnprefixed()
    {
        string resolvedDiscord = HostlistService.ResolveListFilePath("discord");
        Assert.NotEmpty(resolvedDiscord);

        string resolvedYoutube = HostlistService.ResolveListFilePath("youtube");
        Assert.NotEmpty(resolvedYoutube);

        string resolvedExclude = HostlistService.ResolveExcludeFilePath("exclude");
        Assert.NotEmpty(resolvedExclude);
    }

    [Fact]
    public void PresetService_LoadsCompleteBaselineAndPreservesImmutability()
    {
        var svc = new PresetService();
        Assert.True(svc.All.Count >= 37, $"Expected at least 37 strategies, found {svc.All.Count}");
        
        // All baseline presets must have IsBuiltIn = true and non-empty Name and Args
        foreach (var p in svc.All.Where(p => p.IsBuiltIn))
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Name));
            Assert.NotEmpty(p.Args);
            Assert.False(string.IsNullOrWhiteSpace(p.Fingerprint));
        }

        // Built-ins cannot be modified or deleted via UpdateUser / DeleteUser
        var firstBuiltin = svc.All.First(p => p.IsBuiltIn);
        string originalName = firstBuiltin.Name;
        firstBuiltin.Name = "Attempted Hack";
        svc.UpdateUser(firstBuiltin);
        svc.DeleteUser(firstBuiltin);

        var reloaded = svc.FindByName(originalName);
        Assert.NotNull(reloaded);
        Assert.True(reloaded.IsBuiltIn);
    }

    [Fact]
    public void PresetService_GeneratedAndUserStrategies_PersistInStrategiesDirectories()
    {
        var svc = new PresetService();
        string userStratName = "User Custom " + Guid.NewGuid().ToString("N")[..8];
        string genStratName = "Auto Gen " + Guid.NewGuid().ToString("N")[..8];

        var userPreset = new Preset
        {
            Name = userStratName,
            Description = "Custom User Strat",
            Args = new List<string> { "--filter-tcp=443", "--lua-desync=fake:repeats=5" },
            Status = "WORK",
            Reason = "user_configured",
            IsBuiltIn = false
        };

        var genPreset = new Preset
        {
            Name = genStratName,
            Description = "Auto Generated Strat",
            Args = new List<string> { "--filter-tcp=443", "--lua-desync=fake:repeats=7" },
            Status = "WORK",
            Reason = "auto_tuned",
            IsBuiltIn = false
        };

        try
        {
            svc.AddUser(userPreset);
            svc.ReplaceAutoLeaderboard(new[] { genPreset });

            // Verify files exist in strategies/user and strategies/generated
            Assert.True(Directory.Exists(AppPaths.StrategiesUserDir));
            Assert.True(Directory.Exists(AppPaths.StrategiesGeneratedDir));

            var userFiles = Directory.GetFiles(AppPaths.StrategiesUserDir, "*.gxprofile.json");
            var genFiles = Directory.GetFiles(AppPaths.StrategiesGeneratedDir, "*.gxprofile.json");

            Assert.Contains(userFiles, f => File.ReadAllText(f).Contains(userStratName));
            Assert.Contains(genFiles, f => File.ReadAllText(f).Contains(genStratName));

            // Reload into fresh service
            var reloadedSvc = new PresetService();
            var loadedUser = reloadedSvc.FindByName(userStratName);
            var loadedGen = reloadedSvc.FindByName(genStratName);

            Assert.NotNull(loadedUser);
            Assert.False(loadedUser.IsBuiltIn);
            Assert.NotNull(loadedGen);
            Assert.False(loadedGen.IsBuiltIn);

            // Clean up
            svc.DeleteUser(userPreset);
            svc.ReplaceAutoLeaderboard(Array.Empty<Preset>());

            var userFilesAfter = Directory.GetFiles(AppPaths.StrategiesUserDir, "*.gxprofile.json");
            Assert.DoesNotContain(userFilesAfter, f => File.ReadAllText(f).Contains(userStratName));
        }
        finally
        {
            try
            {
                svc.DeleteUser(userPreset);
                svc.ReplaceAutoLeaderboard(Array.Empty<Preset>());
            }
            catch { }
        }
    }
}
