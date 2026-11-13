using System.IO;
using System.IO.Compression;

namespace GxbrielZapret.Services.Infrastructure;

internal static class ThirdPartyNotices
{
    internal static string NoticePath => Path.Combine(AppPaths.AppDataRoot, "licenses", "THIRD_PARTY.md");
    internal static void Ensure()
    {
        string root = Path.GetDirectoryName(NoticePath)!;
        AppUpdateInstaller.RejectLinks(root);
        Directory.CreateDirectory(root);
        using var stream = typeof(ThirdPartyNotices).Assembly.GetManifestResourceStream("GXBRIEL_ZAPRET.third-party-notices.zip")!;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries.Where(e => e.Name.Length > 0))
        {
            string target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!target.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Некорректный путь уведомления о лицензии.");
            AppUpdateInstaller.RejectLinks(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
        }
    }
}
