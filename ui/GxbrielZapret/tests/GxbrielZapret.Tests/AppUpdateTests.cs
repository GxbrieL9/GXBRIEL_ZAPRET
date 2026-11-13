using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using GxbrielZapret.Services.Infrastructure;
using Xunit;

namespace GxbrielZapret.Tests;

public sealed class AppUpdateTests
{
    [Fact]
    public void NativeCapturePreservesPidAndDoesNotRequireSysinternals()
    {
        string csv = GxbrielZapret.Services.Engine.LiveCaptureService.NativeNetstatCsv(
            "  TCP    192.168.1.2:1234    1.1.1.1:443    ESTABLISHED    42\n  TCP 0.0.0.0:80 0.0.0.0:0 LISTENING 999\ninvalid line",
            pid => pid == 42 ? "Discord" : null);
        Assert.Contains("TCP,Discord,42,ESTABLISHED,192.168.1.2:1234,1.1.1.1:443", csv);
        Assert.DoesNotContain("999", csv);
    }
    private static AppUpdateManifest Manifest() => new("4.0.2", "GXBRIEL_ZAPRET.exe", AppUpdateService.AssetUri("v4.0.2", "GXBRIEL_ZAPRET.exe").AbsoluteUri, new string('a', 64), 123);
    [Fact]
    public void SignedManifestVerifiesAndByteTamperingFails()
    {
        using var key = RSA.Create(2048);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(Manifest());
        byte[] sig = key.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        Assert.Equal(Manifest(), AppUpdateService.VerifyManifest(bytes, sig, key.ExportSubjectPublicKeyInfoPem(), "v4.0.2"));
        bytes[3] ^= 1;
        Assert.Throws<InvalidDataException>(() => AppUpdateService.VerifyManifest(bytes, sig, key.ExportSubjectPublicKeyInfoPem(), "v4.0.2"));
    }
    [Fact]
    public void WrongSigningKeyFails()
    {
        using var key = RSA.Create(2048); using var other = RSA.Create(2048);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(Manifest());
        Assert.Throws<InvalidDataException>(() => AppUpdateService.VerifyManifest(bytes, key.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1), other.ExportSubjectPublicKeyInfoPem(), "v4.0.2"));
    }
    [Theory]
    [InlineData("url")][InlineData("name")][InlineData("size")][InlineData("hash")][InlineData("version")]
    public void EvenSignedButInvalidManifestCannotInstall(string field)
    {
        using var key = RSA.Create(2048);
        var m = field switch { "url" => Manifest() with { Url = "https://example.com/evil.exe" },
            "name" => Manifest() with { FileName = "../GXBRIEL_ZAPRET.exe" }, "size" => Manifest() with { Size = long.MaxValue },
            "hash" => Manifest() with { Sha256 = "invalid" }, _ => Manifest() with { Version = "4.0.3" } };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(m);
        Assert.Throws<InvalidDataException>(() => AppUpdateService.VerifyManifest(bytes, key.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1), key.ExportSubjectPublicKeyInfoPem(), "v4.0.2"));
    }
    [Theory]
    [InlineData("v4.0.2")][InlineData("4.0")][InlineData("../4.0.2")][InlineData("4.0.2-preview")]
    public void NonStableVersionIsRejected(string version) => Assert.Throws<InvalidDataException>(() => AppUpdateService.ParseVersion(version));
    [Fact]
    public void VersionNormalizationDoesNotOfferSameVersionAgain() => Assert.Equal(AppUpdateService.ParseVersion("4.0.2"), AppUpdateService.ParseVersion("4.0.2.0"));
    [Fact]
    public async Task MissingFirstReleaseIsNormal()
    {
        using var key = RSA.Create(2048);
        var service = new AppUpdateService(new HttpClient(new Handler(_ => new(HttpStatusCode.NotFound))), key.ExportSubjectPublicKeyInfoPem());
        Assert.Null(await service.FetchLatestAsync());
    }
    [Fact]
    public async Task ReleaseCheckRequiresSignedAssetsAndExactVersion()
    {
        using var key = RSA.Create(2048);
        var cur = AppUpdateService.ParseVersion(UpdaterService.AppVersion);
        string futureVer = $"{cur.Major + 1}.0.0";
        var manifest = new AppUpdateManifest(futureVer, "GXBRIEL_ZAPRET.exe", AppUpdateService.AssetUri("v" + futureVer, "GXBRIEL_ZAPRET.exe").AbsoluteUri, new string('a', 64), 123);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        byte[] signature = key.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var service = new AppUpdateService(new HttpClient(new Handler(request =>
            new(HttpStatusCode.OK) { Content = new ByteArrayContent(request.RequestUri!.Host == "api.github.com" ?
                JsonSerializer.SerializeToUtf8Bytes(new { tag_name = "v" + futureVer, draft = false, prerelease = false }) :
                request.RequestUri.AbsolutePath.EndsWith(".sig") ? signature : bytes) })), key.ExportSubjectPublicKeyInfoPem());
        Assert.Equal(manifest, (await service.FetchLatestAsync())!.Manifest);
    }
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task UpdateTransactionPreservesSettingsAndRollsBackFailedStartup(bool healthy)
    {
        string root = Path.Combine(Path.GetTempPath(), "gx-update-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string target = Path.Combine(root, "GXBRIEL_ZAPRET.exe"), source = Path.Combine(root, "download.exe"), settings = Path.Combine(root, "settings.json");
            File.WriteAllText(target, "previous"); File.WriteAllText(source, "new"); File.WriteAllText(settings, "keep user settings");
            Func<Task> apply = () => AppUpdateTransaction.ApplyAsync(target, source, AppUpdateTransaction.Hash(target), AppUpdateTransaction.Hash(source), () => Task.FromResult(healthy));
            if (healthy) await apply(); else await Assert.ThrowsAsync<IOException>(apply);
            Assert.Equal(healthy ? "new" : "previous", File.ReadAllText(target));
            Assert.Equal("keep user settings", File.ReadAllText(settings));
            Assert.Equal("previous", File.ReadAllText(Assert.Single(Directory.GetFiles(root, "*.previous-*"))));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task ChangedTargetIsNeverOverwritten()
    {
        string root = Path.Combine(Path.GetTempPath(), "gx-update-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string target = Path.Combine(root, "GXBRIEL_ZAPRET.exe"), source = Path.Combine(root, "download.exe");
            File.WriteAllText(target, "original"); File.WriteAllText(source, "new"); string oldHash = AppUpdateTransaction.Hash(target);
            File.WriteAllText(target, "concurrent replacement");
            await Assert.ThrowsAsync<IOException>(() => AppUpdateTransaction.ApplyAsync(target, source, oldHash, AppUpdateTransaction.Hash(source), () => Task.FromResult(true)));
            Assert.Equal("concurrent replacement", File.ReadAllText(target));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void HealthMarkerCannotWriteToUserSuppliedPath() => Assert.Throws<IOException>(() => AppUpdateInstaller.SignalHealthy(Path.Combine(Path.GetTempPath(), "request.json")));
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
}
