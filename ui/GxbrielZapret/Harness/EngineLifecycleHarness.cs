using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using GxbrielZapret.Models;
using GxbrielZapret.Services.Engine;
using GxbrielZapret.Services.Infrastructure;
using GxbrielZapret.Services.Strategies;

namespace GxbrielZapret.Harness;

internal static class EngineLifecycleHarness
{
    internal static async Task RunAsync(string output)
    {
        int exit = 0;
        var steps = new List<object>();
        var logs = new ConcurrentQueue<string>();
        var report = new Dictionary<string, object?> { ["StartedAtUtc"] = DateTime.UtcNow, ["Steps"] = steps, ["NetworkAvailabilityTested"] = false };
        try
        {
            foreach (string name in new[] { "winws", "winws2" })
            {
                var processes = Process.GetProcessesByName(name);
                try { if (processes.Any(p => !p.HasExited)) throw new InvalidOperationException("Stop the current engine before the isolated lifecycle test. Existing processes were not terminated."); }
                finally { foreach (var process in processes) process.Dispose(); }
            }
            HostlistService.EnsureDefaultsSeeded();
            var candidates = ReferenceStrategyCatalog.All.Take(2).Select(p => p.ToPreset()).ToArray();
            using var engine = new EngineService();
            engine.LogLine += line => { logs.Enqueue(line); while (logs.Count > 200) logs.TryDequeue(out _); };
            for (int i = 0; i < candidates.Length; i++)
            {
                await Task.Run(() => engine.Start(candidates[i], null));
                Require(engine.State == EngineState.Running && engine.ActiveProcessId.HasValue, "Start was not confirmed.");
                steps.Add(new { Step = "start", Strategy = candidates[i].Name, Pid = engine.ActiveProcessId });
                await Task.Run(engine.Stop);
                Require(engine.State == EngineState.Stopped && engine.ActiveProcessId is null, "Stop did not finish.");
                steps.Add(new { Step = "stop", Strategy = candidates[i].Name });
            }
            bool rejected = false;
            try { await Task.Run(() => engine.Start(new Preset { Name = "Invalid args fixture", Args = ["--gx-invalid-option-for-lifecycle-test"] }, null)); }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected && engine.State == EngineState.Stopped && engine.ActiveProcessId is null, "Invalid startup was reported as active.");
            steps.Add(new { Step = "invalid-start-rejected" });
            await Task.Run(() => engine.Start(candidates[0], null));
            int pid = engine.ActiveProcessId ?? throw new InvalidOperationException("No owned PID.");
            // This PID was created above, not discovered by a global process-name search.
            using (var ownProcess = Process.GetProcessById(pid)) ownProcess.Kill(entireProcessTree: true);
            var deadline = Stopwatch.StartNew();
            while (engine.State != EngineState.Stopped && deadline.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(50);
            Require(engine.State == EngineState.Stopped && engine.ActiveProcessId is null, "Unexpected exit left a stale active state.");
            steps.Add(new { Step = "unexpected-exit-handled", Pid = pid });
            await Task.Run(() => engine.Start(candidates[0], null));
            Require(await Task.Run(() => engine.ReapplyActiveStrategy("lifecycle explicit reapply")), "Explicit reapply failed.");
            Require(engine.State == EngineState.Running && engine.ActiveProcessId.HasValue, "Reapply did not retain an active engine.");
            steps.Add(new { Step = "explicit-reapply", Pid = engine.ActiveProcessId });
            var reapply = Task.Run(() => engine.ReapplyActiveStrategy("lifecycle concurrent stop"));
            var stop = Task.Run(engine.Stop);
            await Task.WhenAll(reapply, stop);
            Require(engine.State == EngineState.Stopped && engine.ActiveProcessId is null, "Automatic reapply overrode manual stop.");
            steps.Add(new { Step = "concurrent-reapply-and-stop", Reapplied = reapply.Result });
            report["Succeeded"] = true;
        }
        catch (Exception ex) { report["Error"] = ex.ToString(); report["Succeeded"] = false; exit = 1; }
        finally
        {
            report["Logs"] = logs.ToArray();
            report["FinishedAtUtc"] = DateTime.UtcNow;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
                await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally { Application.Current.Shutdown(exit); }
        }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
