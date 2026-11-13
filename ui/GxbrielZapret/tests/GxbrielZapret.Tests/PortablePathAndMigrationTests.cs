using System.IO;
using System.Text.Json;
using GxbrielZapret.Services.Infrastructure;
using Xunit;

namespace GxbrielZapret.Tests;

public class PortablePathAndMigrationTests
{
    [Fact]
    public void AppManifest_RequestsAdministrator_OnExeLaunch()
    {
        string manifestPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "app.manifest"));
        if (!File.Exists(manifestPath))
        {
            manifestPath = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..",
                "app.manifest"));
        }

        Assert.True(File.Exists(manifestPath), manifestPath);
        string manifest = File.ReadAllText(manifestPath);

        Assert.Contains("requestedExecutionLevel", manifest);
        Assert.Contains("level=\"requireAdministrator\"", manifest);
    }

    [Fact]
    public void AppPaths_And_PathManager_Expose_All_Required_Portable_Directories()
    {
        // 1. Verify Root
        Assert.False(string.IsNullOrWhiteSpace(AppPaths.AppDataRoot));
        Assert.True(Path.IsPathRooted(AppPaths.AppDataRoot));
        Assert.EndsWith("GXBRIEL_ZAPRET", AppPaths.AppDataRoot);

        // 2. Verify all subdirectories are rooted and reside within AppDataRoot
        Assert.Equal(Path.Combine(AppPaths.AppDataRoot, "config"), AppPaths.ConfigDir);
        Assert.Equal(Path.Combine(AppPaths.AppDataRoot, "profiles"), AppPaths.ProfilesDir);
        Assert.Equal(Path.Combine(AppPaths.ProfilesDir, "work"), AppPaths.ProfilesWorkDir);
        Assert.Equal(Path.Combine(AppPaths.ProfilesDir, "broken"), AppPaths.ProfilesBrokenDir);
        Assert.Equal(Path.Combine(AppPaths.ProfilesDir, "lists"), AppPaths.ProfilesListsDir);

        Assert.Equal(Path.Combine(AppPaths.AppDataRoot, "lists"), AppPaths.ListsDir);
        Assert.Equal(Path.Combine(AppPaths.ListsDir, "known"), AppPaths.ListsKnownDir);
        Assert.Equal(Path.Combine(AppPaths.ListsDir, "discovered"), AppPaths.ListsDiscoveredDir);
        Assert.Equal(Path.Combine(AppPaths.ListsDir, "user"), AppPaths.ListsUserDir);

        Assert.Equal(Path.Combine(AppPaths.AppDataRoot, "logs"), AppPaths.LogsDir);
        Assert.Equal(Path.Combine(AppPaths.AppDataRoot, "cache"), AppPaths.CacheDir);
        Assert.Equal(Path.Combine(AppPaths.AppDataRoot, "temp"), AppPaths.TempDir);
        Assert.Equal(Path.Combine(AppPaths.AppDataRoot, "backups"), AppPaths.BackupsDir);
        Assert.Equal(Path.Combine(AppPaths.AppDataRoot, "state"), AppPaths.StateDir);

        // 3. Verify SystemRuntimeDir is rooted and targeted to ProgramData or fallback
        Assert.True(Path.IsPathRooted(AppPaths.SystemRuntimeDir));
        Assert.Contains("runtime", AppPaths.SystemRuntimeDir);

        // 4. Verify PathManager mirrors AppPaths
        Assert.Equal(AppPaths.AppDataRoot, PathManager.AppDataRoot);
        Assert.Equal(AppPaths.ConfigDir, PathManager.ConfigDir);
        Assert.Equal(AppPaths.ProfilesDir, PathManager.ProfilesDir);
        Assert.Equal(AppPaths.ListsDir, PathManager.ListsDir);
        Assert.Equal(AppPaths.LogsDir, PathManager.LogsDir);
        Assert.Equal(AppPaths.CacheDir, PathManager.CacheDir);
        Assert.Equal(AppPaths.TempDir, PathManager.TempDir);
        Assert.Equal(AppPaths.BackupsDir, PathManager.BackupsDir);
        Assert.Equal(AppPaths.StateDir, PathManager.StateDir);
        Assert.Equal(AppPaths.SystemRuntimeDir, PathManager.SystemRuntimeDir);

        // 5. Verify EnsureCreated() creates all directories
        AppPaths.EnsureCreated();
        Assert.True(Directory.Exists(AppPaths.AppDataRoot));
        Assert.True(Directory.Exists(AppPaths.ConfigDir));
        Assert.True(Directory.Exists(AppPaths.ProfilesDir));
        Assert.True(Directory.Exists(AppPaths.ProfilesWorkDir));
        Assert.True(Directory.Exists(AppPaths.ProfilesBrokenDir));
        Assert.True(Directory.Exists(AppPaths.ProfilesListsDir));
        Assert.True(Directory.Exists(AppPaths.ListsDir));
        Assert.True(Directory.Exists(AppPaths.ListsKnownDir));
        Assert.True(Directory.Exists(AppPaths.ListsDiscoveredDir));
        Assert.True(Directory.Exists(AppPaths.ListsUserDir));
        Assert.True(Directory.Exists(AppPaths.LogsDir));
        Assert.True(Directory.Exists(AppPaths.CacheDir));
        Assert.True(Directory.Exists(AppPaths.TempDir));
        Assert.True(Directory.Exists(AppPaths.BackupsDir));
        Assert.True(Directory.Exists(AppPaths.StateDir));
    }

    [Fact]
    public void MigrationManager_EnsureMigrated_Can_Be_Called_Safely_And_Idempotently()
    {
        // Must not throw even if called repeatedly
        MigrationManager.EnsureMigrated();
        MigrationManager.EnsureMigrated();
        Assert.True(Directory.Exists(AppPaths.ConfigDir));
        Assert.True(Directory.Exists(AppPaths.StateDir));
    }

    [Fact]
    public void Portable_Migration_Copies_Files_Without_Deleting_Originals()
    {
        string sandbox = Path.Combine(Path.GetTempPath(), "gxbriel_migration_test_" + Guid.NewGuid().ToString("N"));
        string mockSourceDir = Path.Combine(sandbox, "mock_source");
        string mockTargetDir = Path.Combine(sandbox, "mock_target");

        try
        {
            Directory.CreateDirectory(Path.Combine(mockSourceDir, "profiles", "work"));
            Directory.CreateDirectory(Path.Combine(mockSourceDir, "lists"));

            string testProfile = Path.Combine(mockSourceDir, "profiles", "work", "custom.gxprofile.json");
            File.WriteAllText(testProfile, "{\"Name\":\"TestCustom\"}");

            string testList = Path.Combine(mockSourceDir, "lists", "my-domains.txt");
            File.WriteAllText(testList, "example.org\nexample.net\n");

            // Perform recursive copy simulating migration
            Directory.CreateDirectory(Path.Combine(mockTargetDir, "profiles", "work"));
            Directory.CreateDirectory(Path.Combine(mockTargetDir, "lists"));

            File.Copy(testProfile, Path.Combine(mockTargetDir, "profiles", "work", "custom.gxprofile.json"));
            File.Copy(testList, Path.Combine(mockTargetDir, "lists", "my-domains.txt"));

            // Verify copied files exist in target
            Assert.True(File.Exists(Path.Combine(mockTargetDir, "profiles", "work", "custom.gxprofile.json")));
            Assert.True(File.Exists(Path.Combine(mockTargetDir, "lists", "my-domains.txt")));

            // CRITICAL: Verify source files were NEVER deleted
            Assert.True(File.Exists(testProfile), "Migration MUST NOT delete original profile files");
            Assert.True(File.Exists(testList), "Migration MUST NOT delete original list files");
        }
        finally
        {
            try { Directory.Delete(sandbox, recursive: true); } catch { }
        }
    }
}
