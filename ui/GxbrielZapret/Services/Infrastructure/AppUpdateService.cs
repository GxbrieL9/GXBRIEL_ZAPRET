using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GxbrielZapret.Services.Infrastructure;

public sealed record AppUpdateManifest(string Version, string FileName, string Url, string Sha256, long Size);
public sealed record AppRelease(string Tag, AppUpdateManifest Manifest, byte[] ManifestBytes, byte[] Signature);

public sealed class AppUpdateService
{
    public const string Repository = "GxbrieL9/GXBRIEL_ZAPRET";
    public const string ReleasePage = "https://github.com/" + Repository + "/releases";
    private readonly HttpClient _http;
    private readonly string _publicKey;
    private static readonly HttpClient SharedHttp = new UpdaterService().ReleaseHttp;
    public AppUpdateService() : this(SharedHttp, PublicKey()) { }
    internal AppUpdateService(HttpClient http, string publicKey)
    {
        _http = http; _publicKey = publicKey;
        if (!_http.DefaultRequestHeaders.UserAgent.Any()) _http.DefaultRequestHeaders.UserAgent.ParseAdd("GXBRIEL-ZAPRET/" + UpdaterService.AppVersion);
    }
    private static string PublicKey()
    {
        using var stream = typeof(AppUpdateService).Assembly.GetManifestResourceStream("GXBRIEL_ZAPRET.release-public-key.pem")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    internal static Version ParseVersion(string value)
    {
        if (!Regex.IsMatch(value, @"^\d+\.\d+\.\d+(?:\.\d+)?$", RegexOptions.CultureInvariant) || !Version.TryParse(value, out var v))
            throw new InvalidDataException("Некорректная версия обновления.");
        return new Version(v.Major, v.Minor, v.Build, Math.Max(0, v.Revision));
    }
    internal static Uri AssetUri(string tag, string name) => new($"https://github.com/{Repository}/releases/download/{tag}/{name}");
    internal static AppUpdateManifest VerifyManifest(byte[] bytes, byte[] signature, string key, string tag)
    {
        if (bytes.Length > 32768 || signature.Length > 1024) throw new InvalidDataException("Манифест обновления слишком большой.");
        using var rsa = RSA.Create(); rsa.ImportFromPem(key);
        if (!rsa.VerifyData(bytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new InvalidDataException("Подпись обновления не подтверждена. Установка запрещена.");
        var m = JsonSerializer.Deserialize<AppUpdateManifest>(bytes) ?? throw new InvalidDataException("Пустой манифест.");
        if (m.Version is null || m.FileName != "GXBRIEL_ZAPRET.exe" || m.Sha256 is null ||
            !Regex.IsMatch(m.Sha256, "^[a-fA-F0-9]{64}$") || m.Size is < 1 or > 536870912 ||
            tag != "v" + m.Version || m.Url != AssetUri(tag, m.FileName).AbsoluteUri)
            throw new InvalidDataException("Манифест не соответствует официальному выпуску GXBRIEL_ZAPRET.");
        _ = ParseVersion(m.Version);
        return m;
    }
    private async Task<byte[]> SmallAsync(Uri uri, int limit, CancellationToken ct)
    {
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        byte[] buffer = new byte[4096]; int n;
        while ((n = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + n > limit) throw new InvalidDataException("Ответ сервера обновлений слишком большой.");
            output.Write(buffer, 0, n);
        }
        return output.ToArray();
    }
    public async Task<AppRelease?> FetchLatestAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+(?:\.\d+)?$")) return null;
        if (ParseVersion(tag[1..]) <= ParseVersion(UpdaterService.AppVersion)) return null;
        var bytes = await SmallAsync(AssetUri(tag, "app-update.json"), 32768, ct);
        var signature = await SmallAsync(AssetUri(tag, "app-update.sig"), 1024, ct);
        var manifest = VerifyManifest(bytes, signature, _publicKey, tag);
        return new(tag, manifest, bytes, signature);
    }
    internal static void VerifyFile(string path, AppUpdateManifest manifest)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length != manifest.Size || !Convert.ToHexString(SHA256.HashData(stream)).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Контрольная сумма или размер обновления не совпадает.");
        stream.Position = 0;
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        if (pe.PEHeaders.CoffHeader.Machine != System.Reflection.PortableExecutable.Machine.Amd64)
            throw new InvalidDataException("Обновление предназначено не для Windows x64.");
        string version = FileVersionInfo.GetVersionInfo(path).FileVersion ?? "";
        if (ParseVersion(version) != ParseVersion(manifest.Version)) throw new InvalidDataException("Версия EXE не совпадает с подписанным манифестом.");
    }
    public async Task<string> PrepareAsync(AppRelease release, IProgress<double>? progress, CancellationToken ct = default)
    {
        // Program Files inherits administrator-only write access. Never execute an elevated updater from a user-writable temp directory.
        string directory = Path.Combine(AppUpdateInstaller.UpdateRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        AppUpdateInstaller.RejectLinks(directory);
        string file = Path.Combine(directory, "download.exe");
        using (var response = await _http.GetAsync(release.Manifest.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            using var input = await response.Content.ReadAsStreamAsync(ct);
            using var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            byte[] buffer = new byte[131072]; int n; long total = 0;
            while ((n = await input.ReadAsync(buffer, ct)) > 0)
            {
                total += n;
                if (total > release.Manifest.Size) throw new InvalidDataException("Сервер прислал больше данных, чем указано в манифесте.");
                await output.WriteAsync(buffer.AsMemory(0, n), ct);
                progress?.Report((double)total / release.Manifest.Size * 100);
            }
        }
        VerifyFile(file, release.Manifest);
        await File.WriteAllBytesAsync(Path.Combine(directory, "app-update.json"), release.ManifestBytes, ct);
        await File.WriteAllBytesAsync(Path.Combine(directory, "app-update.sig"), release.Signature, ct);
        return directory;
    }
    internal static AppUpdateManifest ReadVerifiedManifest(string directory)
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(directory, "app-update.json"));
        var preliminary = JsonSerializer.Deserialize<AppUpdateManifest>(bytes) ?? throw new InvalidDataException("Пустой манифест.");
        return VerifyManifest(bytes, File.ReadAllBytes(Path.Combine(directory, "app-update.sig")), PublicKey(), "v" + preliminary.Version);
    }
}
