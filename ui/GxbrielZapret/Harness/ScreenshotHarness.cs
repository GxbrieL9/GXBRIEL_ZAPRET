using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows;
using System.Windows.Controls;
using GxbrielZapret.Services.Platform;
using GxbrielZapret.Startup;
using GxbrielZapret.ViewModels;
using GxbrielZapret.Views;

namespace GxbrielZapret.Harness;

/// <summary>
/// <c>GxbrielZapret.exe --screenshot &lt;outDir&gt;</c> — render the app's screens to PNGs and exit, so the
/// documentation shots can be regenerated without a manual desktop capture.
///
/// <para>It drives the REAL window through the real view model, with the same renderer and the same
/// bundled fonts as production, so a shot cannot flatter the app. Needs no administrator, unlike the
/// engine itself — flip <c>app.manifest</c> to <c>asInvoker</c> for the run and put it back after.</para>
/// </summary>
internal static class ScreenshotHarness
{
    internal static async Task RunAsync(string outDir)
    {
        var app = Application.Current;
        try
        {
            outDir = Path.GetFullPath(outDir.Trim().Trim('\"', '\'').Trim());
            Directory.CreateDirectory(outDir);
            if (Environment.GetCommandLineArgs().Contains("--probe-progress-preview"))
            {
                var previewVm = new MainViewModel();
                previewVm.SetAutoSelectPopupHarnessState();
                var preview = new CheckWindow { DataContext = previewVm };
                app.MainWindow = preview;
                preview.Show();
                foreach (var size in new[] { (1120, 720), (880, 560) })
                {
                    preview.Width = size.Item1; preview.Height = size.Item2;
                    await SettleAndSnap(preview, Path.Combine(outDir, $"progress-{size.Item1}x{size.Item2}.png"));
                }
                preview.Close();
                return;
            }
            var window = new MainWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 60, Top = 40, Width = 1280, Height = 800,
            };
            app.MainWindow = window;
            window.Show();

            // Let Loaded + InitializeAsync (presets/version) + the entrance animation settle.
            await Task.Delay(4000);

            var vm = (MainViewModel)window.DataContext;

            // Layout review must not start the Telegram proxy or run strategy probes.
            if (Environment.GetCommandLineArgs().Contains("--visual-only", StringComparer.OrdinalIgnoreCase))
            {
                vm.IsSimpleMode = false;
                vm.SetSimulatedStateForHarness(Services.Engine.EngineState.Stopped);
                foreach (var size in new[] { (1280, 720), (880, 580), (1920, 1080) })
                {
                    window.Width = size.Item1;
                    window.Height = size.Item2;
                    foreach (int tab in new[] { 0, 1, 2, 3, 4, MainViewModel.SettingsTabIndex })
                    {
                        vm.SelectedTabIndex = tab;
                        await SettleAndSnap(window, Path.Combine(outDir, $"review-{tab}-{size.Item1}x{size.Item2}.png"));
                        if (tab == MainViewModel.SettingsTabIndex)
                        {
                            for (int section = 1; section < window.SettingsTabs.Items.Count; section++)
                            {
                                window.SettingsTabs.SelectedIndex = section;
                                await SettleAndSnap(window, Path.Combine(outDir, $"review-settings-{section}-{size.Item1}x{size.Item2}.png"));
                            }
                            window.SettingsTabs.SelectedIndex = 0;
                        }
                    }
                }
                window.Width = 1280;
                window.Height = 720;
                vm.SelectedTabIndex = 0;
                vm.IsSimpleMode = true;
                await SettleAndSnap(window, Path.Combine(outDir, "review-simple-stopped.png"));
                vm.SetSimulatedStateForHarness(Services.Engine.EngineState.Running);
                await SettleAndSnap(window, Path.Combine(outDir, "review-simple-running.png"));
                vm.SetSimulatedStateForHarness(Services.Engine.EngineState.Stopped);
                return;
            }


            // ================================================================
            // 10 REQUESTED MASTER VISUAL BASELINE CHECKPOINTS
            // ================================================================

            // 1. Главный экран — всё выключено (чистый старт приложения, обычный размер окна 1280x720)
            window.Width = 1280;
            window.Height = 720;
            vm.SelectedTabIndex = 0;
            vm.IsSimpleMode = false;
            vm.SetSimulatedStateForHarness(Services.Engine.EngineState.Stopped);
            vm.StopTelegramProxy();
            await SettleAndSnap(window, Path.Combine(outDir, "01_home_all_off.png"));

            // 2. Главный экран — обход включён (визуальное отличие OFF / ON / WORKING)
            var alt11Preset = vm.Presets.FirstOrDefault(p => p.Name.Contains("ALT11"))
                           ?? vm.Presets.FirstOrDefault(p => p.Name.Contains("ALT10"))
                           ?? vm.Presets.FirstOrDefault();
            if (alt11Preset != null) vm.SelectedPreset = alt11Preset;
            vm.SetSimulatedStateForHarness(Services.Engine.EngineState.Running);
            await SettleAndSnap(window, Path.Combine(outDir, "02_home_bypass_on.png"));

            // 3. Открытый выбор стратегии (панель со всеми основными пресетами)
            vm.SelectedTabIndex = 1;
            vm.ShowPresetArgs = false;
            await SettleAndSnap(window, Path.Combine(outDir, "03_strategies_catalog.png"));

            // 4. Выбрана конкретная стратегия (Flowseal ALT10/ALT11, отображение названия, provenance, статуса и параметров)
            vm.SelectedTabIndex = 1;
            if (alt11Preset != null)
            {
                vm.SelectedPreset = alt11Preset;
                alt11Preset.Status = "WORK";
                alt11Preset.Reason = "verified_active_pass";
            }
            vm.ShowPresetArgs = true;
            vm.SelectedPresetTestVerdict = Models.DiagStatus.Ok;
            vm.SelectedPresetTestStatus = "✓ РАБОТАЕТ · Пинг: 26 ms · Discord и YouTube открываются!";
            await SettleAndSnap(window, Path.Combine(outDir, "04_strategy_selected.png"));

            // 5. Telegram Engine / дополнительные сервисы (основной bypass + Telegram включены)
            vm.SelectedTabIndex = 0;
            vm.SetSimulatedStateForHarness(Services.Engine.EngineState.Running);
            vm.StartTelegramProxy();
            await Task.Delay(500);
            await SettleAndSnap(window, Path.Combine(outDir, "05_telegram_and_bypass_active.png"));

            // 6. Экран/панель диагностики (проверки YouTube / Discord / Telegram / DNS / winws2)
            vm.SelectedTabIndex = 3;
            if (vm.DiagRows.Count == 0) vm.RebuildDiagRows();
            await SettleAndSnap(window, Path.Combine(outDir, "06_diagnostics_panel.png"));

            // 7. Логи / консоль внутри GUI (информативный консольный журнал)
            vm.SelectedTabIndex = 4;
            if (vm.LogLines.Count < 5)
            {
                vm.LogLines.Clear();
                vm.LogLines.Add("[19:42:01] Инициализация движка winws2...");
                vm.LogLines.Add("[19:42:01] Загрузка Lua библиотек: zapret-lib.lua, zapret-antidpi.lua, zapret-auto.lua");
                vm.LogLines.Add("[19:42:01] Фильтр WinDivert: 80,443-65535 (TCP), 443,19294-19344,50000-65535 (UDP)");
                vm.LogLines.Add("[19:42:01] Загружен список доменов Discord (53 хоста)");
                vm.LogLines.Add("[19:42:01] Загружен список доменов YouTube (72 хоста)");
                vm.LogLines.Add("[19:42:02] WinDivert driver initialized: захват пакетов активен");
                vm.LogLines.Add("[19:42:02] === Запущен пресет «Комбо — Flowseal ALT11 (fake+ts → seqovl)» (PID 14280) ===");
                vm.LogLines.Add("[19:42:05] TLS ClientHello: gateway.discord.gg -> desync applied (fake+split2)");
                vm.LogLines.Add("[19:42:07] TLS ClientHello: rr1---sn-4g5ednks.googlevideo.com -> desync applied (disoob)");
                vm.LogLines.Add("[19:42:15] Telemetry: packet flow healthy (0 drops, 142 pps)");
            }
            await SettleAndSnap(window, Path.Combine(outDir, "07_logs_console.png"));

            // 7.5. Patcher (обход региональных ограничений Antigravity)
            vm.SelectedTabIndex = MainViewModel.PatcherTabIndex;
            try { await vm.RefreshPatcherStatusAsync(); } catch { }
            await SettleAndSnap(window, Path.Combine(outDir, "07_patcher.png"));

            // 8. Settings (автозапуск, запуск в tray, восстановление состояния, обновления и настройки)
            vm.SelectedTabIndex = MainViewModel.SettingsTabIndex;
            window.SettingsTabs.SelectedIndex = 0;
            await SettleAndSnap(window, Path.Combine(outDir, "08_settings.png"));

            // 9. Tray UX (скрин меню трея + приложение в трее)
            vm.SetSimulatedStateForHarness(Services.Engine.EngineState.Stopped);
            window.CaptureTrayMenu(Path.Combine(outDir, "09_tray_menu.png"));

            // 10. Проблемное состояние (ошибка запуска winws2 / WinDivert / runtime повреждён с деталями)
            vm.SelectedTabIndex = 0;
            vm.ShowErrorModal(
                "Не удалось запустить обход DPI",
                "Движок winws2 не смог запуститься. Драйвер WinDivert заблокирован другой программой или службой.",
                "winws2.exe: windivert_open failed (error 5: Access is denied / ERROR_SERVICE_ALREADY_RUNNING).\nУбедитесь, что сторонние DPI-блокировщики и антивирусы не удерживают фильтр пакетов.",
                "Открыть журнал",
                () => vm.SelectedTabIndex = 4);
            vm.ShowErrorDetails = true;
            await SettleAndSnap(window, Path.Combine(outDir, "10_error_state.png"));
            vm.ShowErrorDialog = false;

            // 11. Auto-select popup (separate checkpoint for the strategy-search UX).
            vm.SetAutoSelectPopupHarnessState();
            var checkWindow = new CheckWindow
            {
                DataContext = vm,
                Owner = window,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 80,
                Top = 60,
                Width = 1120,
                Height = 720,
            };
            checkWindow.Show();
            await SettleAndSnap(checkWindow, Path.Combine(outDir, "11_autoselect_popup.png"));
            checkWindow.Close();

            // ================================================================
            // ADDITIONAL RESOLUTION & COMPONENT CHECKPOINTS
            // ================================================================

            // Stage 1 Visual Checkpoint: Home Simple (1280x720)
            window.Width = 1280;
            window.Height = 720;
            vm.SelectedTabIndex = 0;
            vm.IsSimpleMode = true;
            vm.SetSimulatedStateForHarness(Services.Engine.EngineState.Stopped);
            await SettleAndSnap(window, Path.Combine(outDir, "home-simple-1280x720.png"));
            await SettleAndSnap(window, Path.Combine(outDir, "home-simple.png"));
            await SettleAndSnap(window, Path.Combine(outDir, "home-simple-off.png"));
            await SettleAndSnap(window, Path.Combine(outDir, "home-telegram-off.png"));

            // Stage 1 Visual Checkpoint: Home Simple ON
            vm.SetSimulatedStateForHarness(Services.Engine.EngineState.Running);
            await SettleAndSnap(window, Path.Combine(outDir, "home-simple-on.png"));
            vm.SetSimulatedStateForHarness(Services.Engine.EngineState.Stopped);

            // Stage 1 Visual Checkpoint: Home Advanced (1280x720)
            vm.IsSimpleMode = false;
            await SettleAndSnap(window, Path.Combine(outDir, "home-advanced-1280x720.png"));
            await SettleAndSnap(window, Path.Combine(outDir, "home-advanced.png"));

            // 3. Stage 1 Visual Checkpoint: Home Simple & Advanced (1920x1080)
            window.Width = 1920;
            window.Height = 1080;
            vm.IsSimpleMode = true;
            await SettleAndSnap(window, Path.Combine(outDir, "home-simple-1920x1080.png"));
            vm.IsSimpleMode = false;
            await SettleAndSnap(window, Path.Combine(outDir, "home-advanced-1920x1080.png"));
            await SettleAndSnap(window, Path.Combine(outDir, "home-1920x1080.png"));

            // 4. Stage 1 Visual Checkpoint: Telegram Running State
            window.Width = 1280;
            window.Height = 720;
            vm.StartTelegramProxy();
            await Task.Delay(500);
            await SettleAndSnap(window, Path.Combine(outDir, "home-telegram-on.png"));
            await SettleAndSnap(window, Path.Combine(outDir, "home-telegram-running.png"));

            // 5. Stage 1 Visual Checkpoint: CTA Cursor Glow tracking (Left, Center, Right)
            var heroBtn = FindVisualChild<Button>(window, b => b.Style == (Style)window.FindResource("HeroActionButton"));
            if (heroBtn != null)
            {
                Controls.CursorGlowBehavior.SetGlowPosition(heroBtn, 0.15, 0.5);
                await SettleAndSnap(window, Path.Combine(outDir, "cta-glow-left.png"));
                Controls.CursorGlowBehavior.SetGlowPosition(heroBtn, 0.5, 0.5);
                await SettleAndSnap(window, Path.Combine(outDir, "cta-glow-center.png"));
                Controls.CursorGlowBehavior.SetGlowPosition(heroBtn, 0.85, 0.5);
                await SettleAndSnap(window, Path.Combine(outDir, "cta-glow-right.png"));
            }

            // Capture all other tabs
            foreach (var (idx, file) in new[]
            {
                (1, "strategies.png"), (2, "hostlists.png"),
                (3, "diagnostics.png"), (4, "journal.png"),
                (MainViewModel.PatcherTabIndex, "patcher.png"),
                (MainViewModel.SettingsTabIndex, "settings.png"),
            })
            {
                vm.SelectedTabIndex = idx;
                await SettleAndSnap(window, Path.Combine(outDir, file));
            }

            vm.SelectedTabIndex = MainViewModel.SettingsTabIndex;
            window.SettingsTabs.SelectedIndex = 2;
            await SettleAndSnap(window, Path.Combine(outDir, "settings-system.png"));
            window.SettingsTabs.SelectedIndex = 3;
            await SettleAndSnap(window, Path.Combine(outDir, "settings-maintenance.png"));
            await SettleAndSnap(window, Path.Combine(outDir, "01-settings-live-capture.png"));

            // 02. Discovery Applications Catalog
            vm.ShowDiscoveryCatalog = true;
            await SettleAndSnap(window, Path.Combine(outDir, "02-discovery-catalog.png"));

            // 03. Add Target Chooser Modal ("Как добавить приложение?")
            vm.ShowDiscoveryCatalog = false;
            vm.ShowAddTargetChooser = true;
            await SettleAndSnap(window, Path.Combine(outDir, "03-discovery-add-chooser.png"));

            // 04. Select Running Process Modal
            vm.ShowAddTargetChooser = false;
            vm.ShowProcessPicker = true;
            if (vm.ActiveProcesses.Count == 0)
            {
                vm.ActiveProcesses.Add(new Models.ProcessSnapshotItem
                {
                    Pid = 12484,
                    ProcessName = "DeadByDaylight-Win64-Shipping",
                    Description = "Dead by Daylight",
                    WindowTitle = "DeadByDaylight  ",
                    ExecutablePath = @"D:\Games\DeadByDaylight\DeadByDaylight\Binaries\Win64\DeadByDaylight-Win64-Shipping.exe"
                });
                vm.ActiveProcesses.Add(new Models.ProcessSnapshotItem
                {
                    Pid = 18372,
                    ProcessName = "FortniteClient-Win64-Shipping",
                    Description = "Fortnite",
                    WindowTitle = "Fortnite  ",
                    ExecutablePath = @"D:\Games\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe"
                });
                vm.ActiveProcesses.Add(new Models.ProcessSnapshotItem
                {
                    Pid = 9812,
                    ProcessName = "Discord",
                    Description = "Discord",
                    WindowTitle = "Discord",
                    ExecutablePath = @"C:\Users\AppData\Local\Discord\app-1.0.9160\Discord.exe"
                });
                vm.SelectedProcessItem = vm.ActiveProcesses.FirstOrDefault();
            }
            await SettleAndSnap(window, Path.Combine(outDir, "04-discovery-process-picker.png"));

            // 05. Custom Application in Catalog
            vm.ShowProcessPicker = false;
            var demoCustomTarget = new Models.DiscoveryTarget
            {
                Id = "custom_demo_dbd",
                DisplayName = "Dead by Daylight (Custom)",
                Category = Models.TargetCategory.Custom,
                Enabled = true,
                IsBuiltIn = false,
                Executables = new List<string> { "DeadByDaylight-Win64-Shipping.exe" },
                ProcessNamePatterns = new List<string> { "DeadByDaylight" },
                TrackChildProcesses = true,
                IsRunning = true,
                RunningProcessCount = 2
            };
            if (!vm.DiscoveryTargets.Any(t => t.Id == demoCustomTarget.Id))
            {
                vm.DiscoveryTargets.Add(demoCustomTarget);
            }
            vm.DiscoverySearchText = "Custom";
            vm.ShowDiscoveryCatalog = true;
            await SettleAndSnap(window, Path.Combine(outDir, "05-discovery-custom-app-in-catalog.png"));
            vm.DiscoverySearchText = "";
            vm.ShowDiscoveryCatalog = false;

            // 06. Active capture state
            vm.SelectedTabIndex = MainViewModel.SettingsTabIndex;
            window.SettingsTabs.SelectedIndex = 3;
            vm.IsBuildingIpset = true;
            vm.IpsetProgressValue = 42;
            vm.IpsetProgressStage = "Живой захват 60 сек";
            vm.IpsetStatus = "Живой захват сети: осталось 38 сек | Найдено эндпоинтов: 19, уникальных IP: 14";
            await SettleAndSnap(window, Path.Combine(outDir, "06-discovery-active-capture.png"));

            // 07. Capture result state
            vm.IsBuildingIpset = false;
            vm.IpsetProgressValue = 100;
            vm.IpsetProgressStage = "Готово";
            vm.IpsetStatus = "Живой захват сети завершён (60 сек): перехвачено 24 эндпоинта и 17 публичных IP-адресов. Новых кандидатов: 11.";
            await SettleAndSnap(window, Path.Combine(outDir, "07-discovery-capture-result.png"));

            // 08. Discovery Target Candidates Dialog
            demoCustomTarget.DiscoveredCandidates.Add(new Models.DiscoveredItem
            {
                Value = "latest-live.deadbydaylight.com",
                IsIp = false,
                RemotePort = 443,
                ProcessName = "DeadByDaylight-Win64-Shipping.exe",
                HitCount = 14,
                LastSeen = DateTime.UtcNow,
                IsSelected = true
            });
            demoCustomTarget.DiscoveredCandidates.Add(new Models.DiscoveredItem
            {
                Value = "cdn.live.deadbydaylight.com",
                IsIp = false,
                RemotePort = 443,
                ProcessName = "DeadByDaylight-Win64-Shipping.exe",
                HitCount = 28,
                LastSeen = DateTime.UtcNow,
                IsSelected = true
            });
            demoCustomTarget.DiscoveredCandidates.Add(new Models.DiscoveredItem
            {
                Value = "185.199.108.153",
                IsIp = true,
                RemotePort = 7777,
                ProcessName = "DeadByDaylight-Win64-Shipping.exe",
                HitCount = 5,
                LastSeen = DateTime.UtcNow,
                IsSelected = true
            });
            demoCustomTarget.NotifyCandidatesChanged();
            vm.OpenTargetCandidatesCommand.Execute(demoCustomTarget);
            await SettleAndSnap(window, Path.Combine(outDir, "08-discovery-target-candidates.png"));
            vm.ShowTargetCandidates = false;

            // Modals and dialogs get their own shots: they render ON TOP of the window, so a broken
            // style in one of them cannot be spotted in any of the tab screenshots above.
            // Walkthrough in both states: the held confirm button (first launch) and, after the
            // countdown drains, the unlocked one — proves the timer actually releases the button.
            vm.SelectedTabIndex = 0;
            vm.OpenWelcome(withCountdown: true);
            await SettleAndSnap(window, Path.Combine(outDir, "welcome.png"));
            await Task.Delay(7000);
            await SettleAndSnap(window, Path.Combine(outDir, "welcome-ready.png"));
            vm.ShowWelcome = false;

            vm.ShowHowItWorks = true;
            await SettleAndSnap(window, Path.Combine(outDir, "howitworks.png"));
            vm.ShowHowItWorks = false;

            vm.ShowErrorModal(
                "Не удалось запустить движок обхода",
                "Драйвер WinDivert занят другим процессом или не был выгружен после предыдущего сеанса.",
                "winws2.exe exit code 1: windivert_open failed (ERROR_SERVICE_ALREADY_RUNNING / 0x00000420)",
                "Перейти в диагностику",
                () => { });
            await SettleAndSnap(window, Path.Combine(outDir, "error-dialog.png"));
            vm.ShowErrorDialog = false;

            // Both branches of the environment check: findings (long "Что делать" text) and all-clear.
            await SnapDialog(window, ConflictScanService.ScanEnvironment(false, false),
                Path.Combine(outDir, "envcheck.png"));
            await SnapDialog(window, Array.Empty<EnvFinding>(),
                Path.Combine(outDir, "envcheck-clean.png"));

            window.Close();
        }
        catch (Exception ex) { CrashLog.Write(ex); }
        finally { app.Shutdown(0); }
    }

    /// <summary>Render the environment-check dialog non-modally (harness only) and capture it.</summary>
    private static async Task SnapDialog(Window owner, IReadOnlyList<EnvFinding> findings, string path)
    {
        var dlg = ConflictDialog.CreateForHarness(findings);
        dlg.Owner = owner;
        dlg.Show();
        await SettleAndSnap(dlg, path);
        dlg.Close();
    }

    private static async Task SettleAndSnap(Window w, string path)
    {
        w.UpdateLayout();
        await Task.Delay(700); // let the tab-switch fade + layout settle before capturing
        int width = (int)Math.Ceiling(w.ActualWidth);
        int height = (int)Math.Ceiling(w.ActualHeight);
        if (width <= 0 || height <= 0) return;
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(w);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static T? FindVisualChild<T>(DependencyObject parent, Func<T, bool>? predicate = null) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed && (predicate == null || predicate(typed)))
                return typed;
            var found = FindVisualChild(child, predicate);
            if (found != null) return found;
        }
        return null;
    }
}
