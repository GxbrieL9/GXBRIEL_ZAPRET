using System.Text;
using GxbrielZapret.Services.Platform;
using Xunit;

namespace GxbrielZapret.Tests;

public sealed class AntigravityHubInstallerTests
{
    [Fact]
    public void MissingHubBackendIsRequiredAndCannotBeReplacedByAPatch()
    {
        var cli = new AntigravityComponentStatus { Key = "Agy", RequiredForIde = true };
        Assert.False(cli.CanPatch);
        Assert.Contains("требуется CLI", cli.PatchReason);
        Assert.DoesNotContain("не обязателен", cli.PatchReason);
    }

    private static string Source => "Va.checkBinaryVersion=Ib;Va.acquireInstalledBinaryPath=Oc;" +
        AntigravityHubInstaller.Validation + ";" + AntigravityHubInstaller.Acquisition;

    [Fact]
    public void HubInstallerBackupAndRestoreRoundTrip()
    {
        string? audit = Environment.GetEnvironmentVariable("GX_AG_HUB_AUDIT_SOURCE");
        string source = string.IsNullOrWhiteSpace(audit) ? Source : System.IO.File.ReadAllText(audit);
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gx-hub-roundtrip-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        string path = System.IO.Path.Combine(directory, "extension.js");
        string? previous = Environment.GetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT", System.IO.Path.Combine(directory, "backups"));
            System.IO.File.WriteAllText(path, source, new UTF8Encoding(false));
            string hash = AntigravityPatchEngine.Hash(path);
            AntigravityPatchEngine.Apply(path, "VsCode", default);
            Assert.True(AntigravityPatchEngine.CanRestore(path, "VsCode"));
            Assert.NotEqual(hash, AntigravityPatchEngine.Hash(path));
            using (var patched = System.IO.File.OpenRead(path)) Assert.Equal("Patched", AntigravityPatchEngine.Analyze(patched, "VsCode").State);
            AntigravityPatchEngine.Restore(path, "VsCode", default);
            Assert.Equal(hash, AntigravityPatchEngine.Hash(path));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT", previous);
            System.IO.Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void KnownHubInstallerIsPatchedIdempotentlyBeforeNetworkAcquisition()
    {
        var plan = AntigravityPatchEngine.AnalyzeText(Source, "VsCode");
        Assert.Equal("Unpatched", plan.State);
        Assert.Equal("hub-installer-1.11-v1", plan.RuleId);
        string patched = Encoding.UTF8.GetString(plan.Text!);
        Assert.Contains(AntigravityHubInstaller.Guard + AntigravityHubInstaller.Acquisition, patched);
        var checkedPlan = AntigravityPatchEngine.AnalyzeText(patched, "VsCode");
        Assert.Equal("Patched", checkedPlan.State);
        Assert.Null(checkedPlan.Text);
        Assert.Equal(Source, patched.Replace(AntigravityHubInstaller.Guard, ""));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("renamed")]
    [InlineData("missing-export")]
    [InlineData("stray-marker")]
    public void ChangedOrMixedHubInstallerIsNeverWritten(string change)
    {
        string source = change switch
        {
            "duplicate" => Source + AntigravityHubInstaller.Acquisition,
            "renamed" => Source.Replace("existingBinaryValid:nb", "existingBinaryValid:other"),
            "missing-export" => Source.Replace("Va.checkBinaryVersion=Ib;", ""),
            _ => Source + "/* GXBRIEL_AG_VALIDATED_HUB_BINARY_V1 */"
        };
        var plan = AntigravityPatchEngine.AnalyzeText(source, "VsCode");
        Assert.Equal("Unknown", plan.State);
        Assert.Null(plan.Text);
        Assert.Empty(plan.Changes);
    }
}
