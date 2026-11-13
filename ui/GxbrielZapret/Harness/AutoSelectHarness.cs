using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using System.Windows;
using GxbrielZapret.Services.Infrastructure;
using GxbrielZapret.ViewModels;

namespace GxbrielZapret.Harness;

/// <summary>
/// <c>GxbrielZapret.exe --autoselect-test [outFile]</c> runs the real auto-select flow without opening
/// the window. This is a developer harness: it uses the same MainViewModel command path as the UI, then
/// writes the status, verdicts, passed strategies and journal tail for repeatable strategy checks.
/// </summary>
internal static class AutoSelectHarness
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    internal static async Task RunAsync(string outFile)
    {
        AttachConsole(ATTACH_PARENT_PROCESS);

        var sb = new StringBuilder();
        string fullPath = Path.GetFullPath(outFile);
        string? dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await using var writer = new StreamWriter(fullPath, append: false, Encoding.UTF8) { AutoFlush = true };

        void Log(string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            Console.WriteLine(line);
            sb.AppendLine(line);
            writer.WriteLine(line);
        }

        try
        {
            AppPaths.EnsureCreated();
            Log("=== GXBRIEL_ZAPRET auto-select headless test ===");
            Log($"Project root: {AppPaths.ProjectRoot}");
            Log($"App data: {AppPaths.Root}");

            var vm = new MainViewModel();
            vm.Notify += (title, message) => Log($"TOAST {title}: {message}");
            vm.AutoCheckStarted += () => Log("Auto-select started");
            vm.AutoCheckFinished += () => Log("Auto-select finished");

            await vm.InitializeAsync();
            Log($"Initial status: {vm.RunStatusText}");
            Log($"Selected preset: {vm.SelectedPreset?.Name ?? "(none)"}");

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
            var runTask = vm.RunAutoSelectAsync(showWindow: false);
            string last = "";
            while (!runTask.IsCompleted)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                string pulse = $"{vm.AutoProgressText} · {vm.CurrentCandidateName} · {vm.AutoStatusText}";
                if (!string.Equals(pulse, last, StringComparison.Ordinal))
                {
                    Log("RUN " + pulse);
                    last = pulse;
                }

                if (timeout.IsCancellationRequested)
                {
                    Log("TIMEOUT: stopping auto-select after 8 minutes.");
                    await Application.Current.Dispatcher.InvokeAsync(
                        () => vm.StopAutoSelectCommand.Execute(null),
                        DispatcherPriority.Send);
                    break;
                }
            }

            await runTask;

            Log("");
            Log("================= RESULT =================");
            Log($"Status: {vm.AutoStatusText}");
            Log($"Progress: {vm.AutoProgressText}");
            Log($"Passed strategies: {vm.PassedStrategies.Count}");
            foreach (var score in vm.PassedStrategies.Take(20))
                Log($"PASS {score.Name}: {score.Ok}/{score.Total} · {score.Detail}");

            Log("");
            Log("Verdicts:");
            foreach (var verdict in vm.Verdicts)
                Log($"{verdict.Service}: {verdict.StatusText} · {verdict.Detail}");

            Log("");
            Log("Journal tail:");
            foreach (var line in vm.LogLines.TakeLast(120))
                Log(line);
        }
        catch (Exception ex)
        {
            Log("EXCEPTION: " + ex);
        }
        finally
        {
            try { await writer.FlushAsync(); } catch { /* best effort */ }

            Application.Current.Shutdown(0);
        }
    }
}
