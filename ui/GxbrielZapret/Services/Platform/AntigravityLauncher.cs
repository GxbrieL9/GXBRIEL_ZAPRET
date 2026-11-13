using System.Diagnostics;
using System.IO;

namespace GxbrielZapret.Services.Platform;

internal static class AntigravityLauncher
{
    internal static ProcessStartInfo BuildStartInfo(string executable, bool directConnection)
    {
        string path = Path.GetFullPath(executable);
        if (!File.Exists(path)) throw new FileNotFoundException("Antigravity не найден.", path);
        // Use a fixed command and an environment-carried path. Durable NUL handles prevent Electron
        // console EPIPE when GX exits. No temporary shell script or inherited output pipe is needed.
        if (path.IndexOfAny(['"', '\r', '\n']) >= 0) throw new ArgumentException("Недопустимый путь запуска.");
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(path),
            Arguments = "/d /v:off /s /c \"\"%GX_ANTIGRAVITY_EXECUTABLE%\" 1>NUL 2>NUL\""
        };
        info.Environment["GX_ANTIGRAVITY_EXECUTABLE"] = path;
        if (directConnection) info.Environment.Remove("AG_LS_PROXY");
        if (info.Environment.TryGetValue("GODEBUG", out string? goDebug) && goDebug is not null)
        {
            string cleaned = string.Join(',', goDebug.Split(',').Where(part =>
                !part.Split('=', 2)[0].Trim().Equals("tlskyber", StringComparison.Ordinal)));
            if (cleaned.Length == 0) info.Environment.Remove("GODEBUG");
            else info.Environment["GODEBUG"] = cleaned;
        }
        return info;
    }
}
