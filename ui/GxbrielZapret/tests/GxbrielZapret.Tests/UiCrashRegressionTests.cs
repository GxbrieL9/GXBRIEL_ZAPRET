using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GxbrielZapret.Services.Platform;
using GxbrielZapret.Services.Strategies;
using GxbrielZapret.Models;
using GxbrielZapret.ViewModels;
using GxbrielZapret.Views;
using Xunit;

namespace GxbrielZapret.Tests;

public sealed class UiCrashRegressionTests
{
    [Theory]
    [InlineData("GXBRIEL_ZAPRET Autostart")]
    [InlineData("GXBRIEL_ZAPRET_Autostart")]
    [InlineData("GxbrielZapret Autostart")]
    [InlineData("\\GXBRIEL_ZAPRET Autostart")]
    public void CleanupNeverEndsGuiAutostartTask(string name) =>
        Assert.False(BypassCleanupService.CanStopScheduledTask(name));

    [Fact]
    public void RealWpfViewsRenderWhenTabsSettingsAndScanStateChange()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            string? previous = Environment.GetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT");
            string root = Path.Combine(Path.GetTempPath(), "gx-ui-regression-" + Guid.NewGuid().ToString("N"));
            try
            {
                Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT", root);
                var app = new App();
                app.InitializeComponent();
                var window = new MainWindow();
                var vm = (MainViewModel)window.DataContext;
                bool originalMinimize = vm.MinimizeToTray;
                vm.MinimizeToTray = !originalMinimize;
                using (File.Open(GxbrielZapret.Services.Infrastructure.AppPaths.SettingsFile,
                           FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    vm.MinimizeToTray = originalMinimize;
                    Assert.True(vm.HasUnsavedSettings);
                    string? qaOutput = Environment.GetEnvironmentVariable("GXBRIEL_ZAPRET_QA_OUTPUT_DIR");
                    if (!string.IsNullOrWhiteSpace(qaOutput)) Directory.CreateDirectory(qaOutput);
                    Render((FrameworkElement)window.Content, new Size(880, 580),
                        string.IsNullOrWhiteSpace(qaOutput) ? null : Path.Combine(qaOutput, "settings-save-failure-880x580.png"));
                    Assert.Equal(Visibility.Visible, window.SettingsSaveWarningBanner.Visibility);
                    Assert.True(window.SettingsSaveWarningBanner.ActualHeight > 0);
                    Assert.True(vm.RetrySaveSettingsCommand.CanExecute(null));
                }
                vm.RetrySaveSettingsCommand.Execute(null);
                Render((FrameworkElement)window.Content);
                Assert.False(vm.HasUnsavedSettings);
                Assert.Equal(Visibility.Collapsed, window.SettingsSaveWarningBanner.Visibility);
                var recovery = (GxbrielZapret.Services.Infrastructure.UnexpectedEngineRecovery)
                    typeof(MainViewModel).GetField("_unexpectedRecovery",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(vm)!;
                vm.Settings.DesiredBypassEnabled = true;
                var pendingRecovery = recovery.NotifyUnexpectedExitAsync();
                Assert.True(vm.IsRecoveringEngine);
                Assert.True(vm.CanStop);
                Assert.False(vm.CanStart);
                Assert.Equal(GxbrielZapret.Localization.Loc.T("Отменить восстановление"), vm.HeroButtonText);
                Assert.True(vm.ToggleCommand.CanExecute(null));
                Render((FrameworkElement)window.Content, new Size(880, 580));
                vm.ToggleCommand.Execute(null);
                Assert.True(pendingRecovery.Wait(TimeSpan.FromSeconds(2)));
                Assert.False(vm.IsRecoveringEngine);
                Assert.False(vm.Settings.DesiredBypassEnabled);
                var weakDiscord = new DiagRow { Group = "Discord", Name = "HTTPS only", Host = "discord.com", Http = DiagStatus.Ok };
                var weakYouTube = new DiagRow { Group = "YouTube", Name = "HTTPS only", Host = "www.youtube.com", Http = DiagStatus.Ok };
                vm.DiagRows.Add(weakDiscord);
                vm.DiagRows.Add(weakYouTube);
                vm.RefreshHomeServiceReachability();
                Assert.NotEqual(DiagStatus.Ok, vm.DiscordReachabilityStatus);
                Assert.NotEqual(DiagStatus.Ok, vm.YouTubeReachabilityStatus);
                vm.DiagRows.Remove(weakDiscord);
                vm.DiagRows.Remove(weakYouTube);
                foreach (var definition in ReferenceStrategyCatalog.All)
                    vm.Presets.Add(definition.ToPreset());
                vm.ApplyPresetsFilter();

                vm.Settings.ActivePresetName = "actually-running-profile";
                vm.SelectedPreset = vm.Presets[1];
                Assert.Equal("actually-running-profile", vm.Settings.ActivePresetName);
                Assert.Equal(vm.Presets[1].Name, vm.Settings.SelectedPresetName);

                vm.Presets[0].Status = "WORK";
                vm.Presets[0].ProbeResults = AutoScope.Discord.GoalHosts().ToDictionary(h => h, _ => true);
                vm.Presets[1].Status = "WORK";
                vm.Presets[1].ProbeResults = AutoScope.YouTube.GoalHosts().ToDictionary(h => h, _ => true);
                vm.SelectedAutoScope = AutoScope.YouTube;
                Assert.Same(vm.Presets[1], vm.RecommendedPreset);
                vm.FilterMode = MainViewModel.PresetFilterMode.Work;
                Assert.Single(vm.PresetsView.Cast<Preset>());
                vm.SelectedAutoScope = AutoScope.Both;
                Assert.Equal(MainViewModel.PresetFilterMode.Work, vm.FilterMode);
                Assert.False(vm.HasVisiblePresets);
                Assert.Null(vm.SelectedPreset);
                Assert.Equal(47, vm.UntestedPresetCount);
                Assert.False(vm.Presets[0].IsVerifiedForDisplayScope);
                Assert.Equal("Другие цели", vm.Presets[0].ScopeStatusLabel);
                vm.FilterMode = MainViewModel.PresetFilterMode.Catalog;
                vm.StrategySearchText = vm.Presets[0].ReferenceId!;
                Assert.Single(vm.PresetsView.Cast<Preset>());
                var favorite = vm.PresetsView.Cast<Preset>().Single();
                vm.ToggleFavoritePresetCommand.Execute(favorite);
                Assert.True(favorite.IsFavorite);
                vm.StrategySearchText = "";
                vm.ShowFavoritePresetsOnly = true;
                Assert.Same(favorite, vm.PresetsView.Cast<Preset>().Single());
                vm.SelectedTabIndex = 0;
                vm.SelectedTabIndex = 1;
                Assert.True(vm.ShowFavoritePresetsOnly);
                vm.StrategySearchText = "not-in-catalog-123";
                Assert.False(vm.HasVisiblePresets);
                vm.ClearStrategySearchCommand.Execute(null);
                Assert.Equal(47, vm.VisiblePresetCount);
                vm.LogLines.Add("blockcheck2 native report");
                vm.LogLines.Add("unrelated event");
                vm.LogSearchText = "BLOCKCHECK2";
                Assert.Single(vm.LogView.Cast<string>());
                vm.LogSearchText = "missing-123";
                Assert.False(vm.HasVisibleLogLines);
                vm.LogSearchText = "";
                vm.FollowLog = false;
                Assert.False(vm.Settings.FollowLog);
                // No Show/Run: do not execute startup maintenance, networking, tray or scheduled tasks.
                for (int tab = 0; tab < window.MainTabs.Items.Count; tab++)
                {
                    vm.SelectedTabIndex = tab;
                    Render((FrameworkElement)window.Content);
                }
                vm.SelectedTabIndex = MainViewModel.SettingsTabIndex;
                for (int tab = 0; tab < window.SettingsTabs.Items.Count; tab++)
                {
                    window.SettingsTabs.SelectedIndex = tab;
                    vm.NotificationsEnabled = !vm.NotificationsEnabled;
                    vm.ParseDiscordApps = !vm.ParseDiscordApps;
                    vm.UiScale = tab % 2 == 0 ? 1.25 : 1;
                    Render((FrameworkElement)window.Content);
                }
                vm.SetAutoSelectPopupHarnessState();
                const string scanSummary = "HTTPS 9/9 · YouTube: получено 128 КБ видеоданных; длительное воспроизведение не проверено. Discord: API + WebSocket Hello получены; вход в аккаунт и голос не проверены.";
                vm.PassedStrategies.Add(new AutoScore("Проверенная стратегия", 9, 0, 9, VerificationSummary: scanSummary));
                var check = new CheckWindow { DataContext = vm };
                Render((FrameworkElement)check.Content);
                vm.StopAutoSelectCommand.Execute(null);

                vm.UiScale = 1;
                string? output = Environment.GetEnvironmentVariable("GXBRIEL_ZAPRET_UI_AUDIT_DIR");
                if (!string.IsNullOrWhiteSpace(output)) Directory.CreateDirectory(output);
                var boundProfile = new Preset
                {
                    Name = "blockcheck2 — Discord + YouTube — test-network",
                    Fingerprint = "test-profile", Provenance = "UPSTREAM_BLOCKCHECK2", Status = "WORK",
                    BoundNetworkFingerprint = "test-network", BoundScope = AutoScope.Both,
                    VerifiedFlowSummary = "YouTube: получено 128 КБ видеоданных; воспроизведение не проверено · Discord: API + WebSocket Hello · Голос не проверен.",
                    LastTestedAt = DateTime.UtcNow
                };
                vm.Presets.Add(boundProfile);
                vm.SelectedPreset = boundProfile;
                Assert.False(vm.SelectedPresetEditable);
                Assert.False(vm.DeletePresetCommand.CanExecute(null));
                Assert.False(vm.SavePresetCommand.CanExecute(null));
                Assert.Contains("128 КБ", vm.LastApplicationFlowSummary);
                vm.SelectedTabIndex = 0;
                Render((FrameworkElement)window.Content, new Size(1280, 720), output is null ? null : Path.Combine(output, "network-profile-home-1280x720.png"));
                vm.Presets.Remove(boundProfile);
                vm.SelectedPreset = vm.Presets[0];
                var patcher = new AntigravityStatusResult { Summary = "Файлы проверены. Ответ модели не проверен." };
                patcher.LanguageServer.Path = Path.Combine(root, new string('x', 140), "resources", "bin", "language_server.exe");
                patcher.LanguageServer.State = "Patched";
                patcher.LanguageServer.Details = "Сигнатура патча обнаружена. Ответ модели не проверен.";
                patcher.LanguageServer.HasBackup = true;
                patcher.Ide.Path = Path.Combine(root, "portable", "resources", "app", "out", "main.js");
                patcher.Ide.State = "Unpatched";
                patcher.Agy.Path = Path.Combine(root, "agy.exe");
                patcher.Agy.State = "Unknown";
                patcher.Agy.Details = "Несколько совпадений сигнатуры: автоматическая запись запрещена.";
                vm.ApplyPatcherResult(patcher);
                Assert.True(vm.PatchComponentCommand.CanExecute("Ide"));
                Assert.False(vm.PatchComponentCommand.CanExecute("Agy"));
                Assert.True(vm.RestoreComponentCommand.CanExecute("LanguageServer"));
                vm.ShowPatcherMonitorResult(new(null, "Файлы Antigravity изменились. Ожидаю завершения обновления…", false, true));
                Assert.False(vm.PatchComponentCommand.CanExecute("Ide"));
                Assert.False(vm.RestoreComponentCommand.CanExecute("LanguageServer"));
                Assert.True(vm.HasPatcherUpdateMessage);
                vm.ShowPatcherMonitorResult(new(patcher, "После изменения файлов патч больше не подтверждён: Antigravity IDE. Совместимые компоненты можно пропатчить повторно. Ответ модели не проверен.", false));
                Assert.True(vm.PatchComponentCommand.CanExecute("Ide"));
                Assert.DoesNotContain("разблокирован", vm.PatcherBadgeToolTip);
                foreach (var size in new[] { new Size(880, 580), new Size(1280, 720), new Size(1920, 1080) })
                {
                    for (int tab = 0; tab < window.MainTabs.Items.Count; tab++)
                    {
                        vm.SelectedTabIndex = tab;
                        Render((FrameworkElement)window.Content, size, output is null ? null : Path.Combine(output, $"tab-{tab}-{size.Width}x{size.Height}.png"));
                        if (tab == MainViewModel.PatcherTabIndex)
                        {
                            Assert.Equal(4, vm.PatcherComponents.Count);
                            var rowActions = FindVisualChildren<Button>(window.MainTabs).Where(b => b.Command == vm.PatchComponentCommand || b.Command == vm.RestoreComponentCommand).ToArray();
                            Assert.Equal(8, rowActions.Length);
                            Assert.All(rowActions, b =>
                            {
                                Assert.True(ToolTipService.GetShowOnDisabled(b));
                                Assert.False(string.IsNullOrWhiteSpace(b.ToolTip as string));
                            });
                            Assert.DoesNotContain("Разблокирован", vm.PatcherLs.StateDisplay);
                            var actions = window.PatcherActions;
                            var bounds = actions.Children.OfType<Button>().Where(b => b.Visibility != Visibility.Collapsed)
                                .Select(b => b.TransformToAncestor(actions).TransformBounds(new Rect(new Point(), b.RenderSize))).ToList();
                            for (int i = 0; i < bounds.Count; i++)
                            {
                                Assert.True(bounds[i].Right <= actions.ActualWidth + 1, "A patcher action overflows its toolbar.");
                                for (int j = i + 1; j < bounds.Count; j++)
                                    Assert.False(bounds[i].IntersectsWith(bounds[j]), "Patcher actions overlap.");
                            }
                            var scroll = FindVisualChild<ScrollViewer>(window.MainTabs);
                            Assert.NotNull(scroll);
                            if (size.Height <= 720) Assert.True(scroll.ScrollableHeight > 0, "Patcher content must be scrollable on compact windows.");
                            if (scroll.ScrollableHeight > 0)
                            {
                                scroll.ScrollToEnd(); scroll.UpdateLayout();
                                Assert.True(scroll.VerticalOffset > 0);
                                scroll.ScrollToHome(); scroll.UpdateLayout();
                            }
                            var installed = new AntigravityStatusResult { Summary = "Найдено компонентов: 2. Ответ модели не проверен." };
                            installed.Ide.Path = patcher.Ide.Path; installed.Ide.State = "Patched";
                            installed.LanguageServer.Path = patcher.LanguageServer.Path; installed.LanguageServer.State = "Patched";
                            vm.ShowPatcherMonitorResult(new(installed, "", false));
                            Assert.False(vm.PatchAllComponentsCommand.CanExecute(null));
                            Assert.False(vm.RestoreAllComponentsCommand.CanExecute(null));
                            Assert.Contains("уже пропатчены", vm.PatcherPatchAllReason);
                            Assert.Contains("Нет проверенных", vm.PatcherRestoreAllReason);
                            Render((FrameworkElement)window.Content, size, output is null ? null : Path.Combine(output, $"patcher-already-applied-{size.Width}x{size.Height}.png"));
                            installed.LanguageServer.State = "ExternalPartial";
                            installed.LanguageServer.Details = "Найдены маркеры альтернативного патча. Совместное применение патча GX запрещено; ответ модели не проверен.";
                            installed.Ide.OperationError = "Закройте Antigravity перед изменением файлов. Состояние файла повторно проверено.";
                            installed.Summary = AntigravityProxyDiagnostic.Describe("http://127.0.0.1:53129", "http://localhost:8080", null);
                            vm.ShowPatcherMonitorResult(new(installed, "", false));
                            Assert.False(vm.PatchComponentCommand.CanExecute("LanguageServer"));
                            Assert.Contains("альтернативный", vm.PatcherPatchAllReason);
                            Assert.Contains("Другой патч: 1", vm.PatcherActionSummary);
                            Render((FrameworkElement)window.Content, size, output is null ? null : Path.Combine(output, $"patcher-alternative-blocked-{size.Width}x{size.Height}.png"));
                            installed.SelectionRequired = true;
                            installed.Summary = "Найдено несколько установок Antigravity. Выберите нужный каталог кнопкой папки перед применением или откатом патча. Автопатч приостановлен. " +
                                "Установки: C:\\Users\\fixture\\AppData\\Local\\Programs\\Antigravity; C:\\Users\\fixture\\AppData\\Local\\Programs\\Antigravity IDE.";
                            vm.ShowPatcherMonitorResult(new(installed, "Выберите каталог Antigravity: найдено несколько установок. Автопатч приостановлен.", false));
                            Assert.Null(vm.PatcherLauncherPath);
                            Assert.False(vm.LaunchAntigravityCommand.CanExecute(null));
                            Assert.False(vm.PatchComponentCommand.CanExecute("Ide"));
                            Assert.False(vm.RestoreComponentCommand.CanExecute("LanguageServer"));
                            Assert.Contains("несколько установок", vm.PatcherPatchAllReason);
                            Render((FrameworkElement)window.Content, size, output is null ? null : Path.Combine(output, $"patcher-selection-required-{size.Width}x{size.Height}.png"));
                            installed.Agy.Path = Path.Combine(root, "standalone-cli", "agy.exe");
                            installed.Agy.State = "Unpatched";
                            installed.Agy.HasBackup = true;
                            installed.Agy.Details = "Совместимый патч CLI доступен независимо от выбора IDE.";
                            vm.ApplyPatcherResult(installed);
                            Assert.True(vm.PatchComponentCommand.CanExecute("Agy"));
                            Assert.True(vm.RestoreComponentCommand.CanExecute("Agy"));
                            Assert.False(vm.PatchComponentCommand.CanExecute("Ide"));
                            Assert.False(vm.PatchAllComponentsCommand.CanExecute(null));
                            Render((FrameworkElement)window.Content, size, output is null ? null : Path.Combine(output, $"patcher-independent-cli-{size.Width}x{size.Height}.png"));
                            vm.ShowPatcherMonitorResult(new(patcher, "После изменения файлов патч больше не подтверждён: Antigravity IDE. Совместимые компоненты можно пропатчить повторно.", false));
                        }
                        if (tab == 0)
                        {
                            var tools = window.HomeTools;
                            var toolBounds = tools.Children.OfType<Border>()
                                .Select(b => b.TransformToAncestor(tools).TransformBounds(new Rect(new Point(), b.RenderSize))).ToList();
                            Assert.Equal(3, toolBounds.Count);
                            for (int i = 0; i < toolBounds.Count; i++)
                            {
                                Assert.True(toolBounds[i].Right <= tools.ActualWidth + 1, "A home tool overflows its panel.");
                                for (int j = i + 1; j < toolBounds.Count; j++)
                                    Assert.False(toolBounds[i].IntersectsWith(toolBounds[j]), "Home tools overlap.");
                            }
                        }
                        if (tab == 1)
                        {
                            var actions = window.StrategyActions;
                            var bounds = actions.Children.OfType<Button>().Where(b => b.Visibility == Visibility.Visible)
                                .Select(b => b.TransformToAncestor(actions).TransformBounds(new Rect(new Point(), b.RenderSize))).ToList();
                            for (int i = 0; i < bounds.Count; i++)
                            {
                                Assert.True(bounds[i].Right <= actions.ActualWidth + 1, "A strategy action overflows its toolbar.");
                                for (int j = i + 1; j < bounds.Count; j++)
                                    Assert.False(bounds[i].IntersectsWith(bounds[j]), "Strategy actions overlap.");
                            }
                            var scroll = FindVisualChild<ScrollViewer>(window.StrategiesList);
                            Assert.NotNull(scroll);
                            Assert.True(scroll.ScrollableHeight > 0, "The strategy catalog should be scrollable.");
                            scroll.ScrollToEnd();
                            scroll.UpdateLayout();
                            Assert.True(scroll.VerticalOffset > 0, "Scrolling the strategy catalog did not move it.");
                            scroll.ScrollToHome();
                            scroll.UpdateLayout();
                        }
                    }
                }
                check.ShowActivated = false;
                check.Show();
                check.Width = 880;
                check.Height = 560;
                check.UpdateLayout();
                foreach (var border in FindVisualChildren<Border>(check))
                {
                    border.BeginAnimation(UIElement.OpacityProperty, null);
                    border.Opacity = 1;
                }
                Render((FrameworkElement)check.Content, new Size(880, 560), output is null ? null : Path.Combine(output, "check-880x560.png"));
                var scanDetails = FindVisualChildren<TextBlock>(check).Single(b => b.Text == scanSummary);
                Assert.Equal(TextWrapping.Wrap, scanDetails.TextWrapping);
                Assert.True(scanDetails.ActualHeight > 30, "Full application verification details must wrap in the scan result card.");
                vm.AutoProgressText = "20/39";
                Assert.Contains("20/39", check.Title);
                check.Close();
                Assert.False(check.IsVisible);
                Assert.True(vm.IsAutoRunning);
                check.Show();
                Assert.True(check.IsVisible);
                check.DataContext = null;
                check.Close();
                vm.Shutdown();
            }
            catch (Exception ex) { failure = ex; }
            finally { Environment.SetEnvironmentVariable("GXBRIEL_ZAPRET_APPDATA_ROOT", previous); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "WPF rendering timed out.");
        Assert.Null(failure);
    }

    private static void Render(FrameworkElement content, Size? dimensions = null, string? path = null)
    {
        var size = dimensions ?? new Size(1280, 800);
        content.Measure(size);
        content.Arrange(new Rect(new Point(), size));
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        if (path is not null)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(path);
            encoder.Save(file);
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindVisualChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
        }
    }
}
