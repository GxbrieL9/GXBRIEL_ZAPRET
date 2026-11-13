using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using GxbrielZapret.Services.Engine;
using GxbrielZapret.Services.Infrastructure;

namespace GxbrielZapret.Services.Strategies;

public sealed record Blockcheck2Environment(string Root, string Bash, string Script, string Curl, string Winws, string Mdig);

public static class Blockcheck2Runtime
{
    private static readonly object InstallLock = new();
    public static Blockcheck2Environment EnsureInstalled()
    {
        lock (InstallLock)
        {
            if (!File.Exists(AppPaths.WinwsExe) && !AppPaths.TryRestoreBundledEngine(out string? error))
                throw new IOException(error ?? "Не удалось подготовить движок blockcheck2.");
            EngineValidator.EnsureEngineVerified();
            string root = Path.Combine(AppPaths.EngineDir, "blockcheck2");
            using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("GXBRIEL_ZAPRET.engine.blockcheck2-runtime.zip")
                ?? throw new FileNotFoundException("В сборке отсутствует окружение blockcheck2.");
            string hash = Convert.ToHexString(SHA256.HashData(payload));
            payload.Position = 0;
            string marker = Path.Combine(root, "payload.sha256");
            string bash = Path.Combine(root, "cygwin", "bin", "bash.exe");
            using var zip = new ZipArchive(payload, ZipArchiveMode.Read);
            if (!File.Exists(marker) || File.ReadAllText(marker) != hash ||
                zip.Entries.Any(entry => !File.Exists(Path.Combine(root, entry.FullName)) || new FileInfo(Path.Combine(root, entry.FullName)).Length != entry.Length))
            {
                Directory.CreateDirectory(root);
                string prefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
                foreach (var entry in zip.Entries)
                {
                    string target = Path.GetFullPath(Path.Combine(root, entry.FullName));
                    if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Некорректный путь в окружении blockcheck2.");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                }
                File.WriteAllText(marker, hash);
            }

            // All Cygwin executables share one DLL and process table so upstream kill/wait works.
            string bin = Path.GetDirectoryName(bash)!;
            foreach (string name in new[] { "winws2.exe", "mdig.exe", "WinDivert.dll", "WinDivert64.sys" })
                CopyIfChanged(Path.Combine(AppPaths.EngineDir, name), Path.Combine(bin, name));
            if (!File.Exists(Path.Combine(bin, "cygwin1.dll"))) CopyIfChanged(Path.Combine(AppPaths.EngineDir, "cygwin1.dll"), Path.Combine(bin, "cygwin1.dll"));
            foreach (string source in Directory.EnumerateFiles(AppPaths.LuaDir, "*", SearchOption.AllDirectories))
                CopyIfChanged(source, Path.Combine(root, "lua", Path.GetRelativePath(AppPaths.LuaDir, source)));
            Directory.CreateDirectory(Path.Combine(root, "cygwin", "tmp"));
            Directory.CreateDirectory(Path.Combine(root, "cygwin", "home", "gxbriel"));
            return new(root, bash, Path.Combine(root, "blockcheck2.sh"),
                Path.Combine(root, "cygwin", "usr", "local", "bin", "curl.exe"),
                Path.Combine(bin, "winws2.exe"), Path.Combine(bin, "mdig.exe"));
        }
    }

    private static void CopyIfChanged(string source, string target)
    {
        if (!File.Exists(source)) throw new FileNotFoundException("Не найден файл окружения blockcheck2.", source);
        if (File.Exists(target))
        {
            using var original = File.OpenRead(source);
            using var installed = File.OpenRead(target);
            if (SHA256.HashData(original).AsSpan().SequenceEqual(SHA256.HashData(installed))) return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, overwrite: true);
    }
}
