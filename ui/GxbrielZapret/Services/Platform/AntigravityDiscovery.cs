using System.IO;

namespace GxbrielZapret.Services.Platform;

internal static class AntigravityDiscovery
{
    internal static IReadOnlyList<string> FindInstallations(IReadOnlyList<string>? searchRoots = null)
    {
        var roots = searchRoots ?? new[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\Programs",
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }
            .SelectMany(dir => new[] { "Antigravity", "Antigravity IDE" }.Select(name => Path.Combine(dir, name))).ToArray();
        return roots.Where(r => File.Exists(Path.Combine(r, "Antigravity.exe")) || File.Exists(Path.Combine(r, "Antigravity IDE.exe")))
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static string? InstallRootFor(string path)
    {
        for (string? dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path); dir is not null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "Antigravity.exe")) || File.Exists(Path.Combine(dir, "Antigravity IDE.exe"))) return dir;
        return null;
    }

    internal static Dictionary<string, string> Find(string? manualRoot, IReadOnlyList<string>? searchRoots = null, string? sharedCliRoot = null)
    {
        bool manual = !string.IsNullOrWhiteSpace(manualRoot);
        var roots = new List<string>();
        if (manual)
        {
            string path = Path.GetFullPath(manualRoot!);
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException("Выбранный каталог Antigravity не существует.");
            roots.Add(path);
        }
        else if (searchRoots is not null) roots.AddRange(searchRoots);
        else
        {
            roots.AddRange(FindInstallations());
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agy", "bin"));
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "bin"));
        }
        var result = new Dictionary<string, string>();
        // One desktop installation per report. Never fill a missing component from another product/version.
        string? desktopRoot = manual ? roots[0] : roots.FirstOrDefault(r =>
            File.Exists(Path.Combine(r, "Antigravity.exe")) || File.Exists(Path.Combine(r, "Antigravity IDE.exe")));
        void Add(string key, IEnumerable<string> candidates)
        {
            string? path = candidates.FirstOrDefault(File.Exists);
            if (path is not null) result.TryAdd(key, Path.GetFullPath(path));
        }
        foreach (string root in roots)
        {
            if (string.Equals(root, desktopRoot, StringComparison.OrdinalIgnoreCase))
            {
                Add("LanguageServer", new[] { "resources/bin/language_server.exe", "resources/app/extensions/antigravity/bin/language_server_windows_x64.exe", "bin/language_server.exe", "language_server.exe" }.Select(p => Path.Combine(root, p)));
                Add("Ide", new[] { "resources/app/out/main.js", "app/out/main.js", "out/main.js", "main.js", "resources/app.asar" }.Select(p => Path.Combine(root, p)));
                Add("VsCode", new[] { "resources/app/extensions/agy/vscode/extension.js", "resources/app/extensions/antigravity/extension.js" }
                    .Select(p => Path.Combine(root, p)));
            }
            Add("Agy", new[] { "agy.exe", "bin/agy.exe", "antigravity.exe" }.Select(p => Path.Combine(root, p)).Where(p =>
                !Path.GetFileName(p).Equals("antigravity.exe", StringComparison.OrdinalIgnoreCase) || new DirectoryInfo(root).Name.Equals("bin", StringComparison.OrdinalIgnoreCase)));
            Add("VsCode", new[] { Path.Combine(root, "extension.js") });
        }
        // The Hub installer stores its backend outside the desktop installation (cc() in extension 1.11).
        // Associate that known shared CLI only with a selected desktop that actually bundles agy.
        if (desktopRoot is not null && File.Exists(Path.Combine(desktopRoot, "resources/app/extensions/agy/vscode/extension.js")))
            Add("Agy", [Path.Combine(sharedCliRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "bin"), "agy.exe")]);
        if (!manual)
        {
            foreach (string dir in new[] { ".vscode", ".vscode-insiders" }.Select(n => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), n, "extensions")))
            {
                if (!Directory.Exists(dir)) continue;
                try { Add("VsCode", Directory.EnumerateDirectories(dir, "google.google-antigravity-*").OrderByDescending(Directory.GetLastWriteTimeUtc).Select(d => Path.Combine(d, "extension.js"))); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { System.Diagnostics.Trace.WriteLine(ex.Message); }
            }
        }
        return result;
    }
}
