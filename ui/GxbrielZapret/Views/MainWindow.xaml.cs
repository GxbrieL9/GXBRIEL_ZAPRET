using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using GxbrielZapret.Localization;
using GxbrielZapret.Services.Engine;
using GxbrielZapret.Startup;
using GxbrielZapret.Services.Platform;
using GxbrielZapret.ViewModels;

namespace GxbrielZapret.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private CheckWindow? _checkWindow;
    private Forms.NotifyIcon? _tray;
    private Forms.ContextMenuStrip? _trayMenu;
    private Forms.ToolStripMenuItem? _trayStatus;
    private Forms.ToolStripMenuItem? _trayToggle;
    private Forms.ToolStripMenuItem? _trayToggleTg;
    private Drawing.Icon? _iconIdle;
    private Drawing.Icon? _iconRunning;
    private bool _reallyClose;
    private bool _trayStartupHandled;
    private EngineState _lastState = EngineState.Stopped;
    private readonly ToastNotifier _toasts = new();
    private bool _logScrollPending;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        _vm.LogLines.CollectionChanged += OnLogChanged;
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.Notify += (title, msg) => ShowToast(title, msg);
        _vm.ErrorDialogAction = () => OnCheckEnvironment(this, new RoutedEventArgs());
        _vm.AutoCheckStarted += OnAutoCheckStarted;
        _vm.AutoCheckFinished += OnAutoCheckFinished;
        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnWindowStateChanged;
        SourceInitialized += OnSourceInitialized; // constrain borderless maximize to work area
        PreviewKeyDown += OnWindowPreviewKeyDown;

        // OS logoff/shutdown: tear down even though minimize-to-tray would normally
        // cancel a close, so winws2 is never left running.
        Application.Current.SessionEnding += (_, _) => { _reallyClose = true; CleanupAndShutdown(); };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        SetupTray();
        UpdateMaxButtonGlyph();

        bool startInTray =
            (Environment.GetCommandLineArgs().Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase))
            || _vm.Settings.StartMinimized) && !_trayStartupHandled;
        if (startInTray && _vm.MinimizeToTray)
            HideToTray();

        // Startup-only conflict check: another DPI-bypass tool (shares the WinDivert driver) or a VPN
        // (re-routes traffic past the engine) commonly stops the bypass from working. Advisory dialog —
        // it never blocks the launch. Skipped when starting hidden in the tray or under the screenshot harness.
        bool screenshotHarness = Environment.GetCommandLineArgs()
            .Any(a => a.Equals("--screenshot", StringComparison.OrdinalIgnoreCase));
        if (!screenshotHarness && !(startInTray && _vm.MinimizeToTray))
        {
            var conflicts = await Task.Run(ConflictScanService.ScanConflicts);
            if (conflicts.Count > 0) ConflictDialog.Show(conflicts);
        }

        if (!_vm.IsInitialized)
        {
            try
            {
                await _vm.InitializeAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, Loc.T("Ошибка инициализации"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // First run: open the walkthrough once, after init so it sits over a populated window. Skipped
        // under the screenshot harness (it would cover every tab shot) and when starting into the tray.
        if (!screenshotHarness && !(startInTray && _vm.MinimizeToTray) && _vm.NeedsWelcome)
            _vm.OpenWelcome(withCountdown: true);
    }

    internal void InitializeForTrayStartup()
    {
        _trayStartupHandled = true;
        ShowInTaskbar = false;
        SetupTray();
        _ = Dispatcher.BeginInvoke(async () =>
        {
            try { await _vm.InitializeAsync(); }
            catch (Exception ex) { CrashLog.Write(ex); }
        });
    }

    /// <summary>
    /// Настройки → «Проверить окружение»: the full check (conflicts + engine state), on demand.
    /// Unlike the startup advisory this ALWAYS shows the dialog — including the all-clear state — so
    /// pressing the button always produces a visible answer.
    /// </summary>
    private async void OnCheckEnvironment(object sender, RoutedEventArgs e)
    {
        if (_vm.IsScanningEnvironment) return;
        _vm.IsScanningEnvironment = true;
        _vm.StatusMessage = Loc.T("Выполняется проверка окружения Windows…");
        try
        {
            bool installed = _vm.IsEngineInstalled;
            bool complete = _vm.IsEngineComplete;
            var findings = await Task.Run(() => ConflictScanService.ScanEnvironment(installed, complete));
            if (findings.Count == 0 || findings.All(f => f.Severity == EnvSeverity.Ok))
            {
                ShowToast(Loc.T("Проверка окружения"), Loc.T("Конфликтов не обнаружено. Всё в порядке!"));
            }
            else
            {
                ShowToast(Loc.T("Проверка окружения"), Loc.T("Обнаружены замечания или конфликты ({0}).", findings.Count));
            }
            ConflictDialog.Show(findings);
        }
        finally
        {
            _vm.IsScanningEnvironment = false;
        }
    }

    private void StrategiesList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_vm.ApplyStrategyCommand.CanExecute(null))
            _vm.ApplyStrategyCommand.Execute(null);
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add) ScrollLogToLatest();
    }

    private void ScrollLogToLatest()
    {
        if (!_vm.FollowLog || _logScrollPending) return;

        _logScrollPending = true;
        Dispatcher.BeginInvoke(() =>
        {
            _logScrollPending = false;
            if (_vm.FollowLog && LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
        }, DispatcherPriority.Background);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.State) or nameof(MainViewModel.RunningPreset) or nameof(MainViewModel.IsTelegramProxyRunning))
            UpdateTrayForState(_vm.State);
        else if (e.PropertyName == nameof(MainViewModel.UiScale))
            ApplyUiScale(_vm.UiScale);
        else if (e.PropertyName is nameof(MainViewModel.FollowLog) or nameof(MainViewModel.LogSearchText))
            ScrollLogToLatest();
    }

    private void OnLogMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (e.Delta > 0) _vm.FollowLog = false;
    }

    // ---- app-wide UI scale (DPI-independent zoom) --------------------------

    /// <summary>Scales the whole UI via a LayoutTransform on the content root, on top of the OS DPI
    /// scaling. The borderless drag region and the window's min/target size are grown with it so the
    /// enlarged content isn't clipped and dragging still lines up with the visible caption bar.</summary>
    private void ApplyUiScale(double scale)
    {
        scale = Math.Clamp(scale, 1.0, 2.5);
        ContentRoot.LayoutTransform = scale == 1.0 ? Transform.Identity : new ScaleTransform(scale, scale);

        var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
        if (chrome is not null) chrome.CaptionHeight = 46 * scale;

        var wa = SystemParameters.WorkArea;
        MinWidth = Math.Min(880 * scale, Math.Max(760, wa.Width - 24));
        MinHeight = Math.Min(580 * scale, Math.Max(500, wa.Height - 24));

        if (WindowState == WindowState.Normal)
        {
            Width = Math.Min(1280 * scale, wa.Width - 16);
            Height = Math.Min(720 * scale, wa.Height - 16);
            Left = wa.Left + (wa.Width - Width) / 2;
            Top = wa.Top + (wa.Height - Height) / 2;
        }
    }

    private void OnWindowPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape) return;
        if (_vm.ShowErrorDialog) { _vm.ShowErrorDialog = false; e.Handled = true; }
        else if (_vm.ShowTargetPopup) { _vm.ShowTargetPopup = false; e.Handled = true; }
        else if (_vm.ShowHowItWorks) { _vm.ShowHowItWorks = false; e.Handled = true; }
        else if (_vm.ShowWelcome) { _vm.CloseWelcomeCommand.Execute(null); e.Handled = true; }
    }

    // Fade + slight slide-up of the tab content when the user switches tabs.
    private void OnTabsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged also bubbles from inner ListBoxes — only react to the TabControl itself.
        if (e.OriginalSource is not TabControl) return;
        if (MainTabs.Template?.FindName("PART_SelectedContentHost", MainTabs) is not ContentPresenter host)
            return;

        var dur = TimeSpan.FromMilliseconds(220);
        host.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur));
        // Use a fresh (unfrozen) transform — the one baked into the template is sealed/frozen.
        var tt = new TranslateTransform();
        host.RenderTransform = tt;
        tt.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, dur) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    // ---- window caption ----------------------------------------------------

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestore(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        // WM_GETMINMAXINFO now clamps the maximized window to the monitor work area,
        // so there is no overflow to compensate — just drop the border when maximized.
        bool maximized = WindowState == WindowState.Maximized;
        ApplyDwmCornerPreference(new System.Windows.Interop.WindowInteropHelper(this).Handle, round: !maximized);
        RootBorder.Margin = new Thickness(0);
        RootBorder.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(20);
        RootBorder.BorderThickness = maximized ? new Thickness(0) : new Thickness(1);
        UpdateMaxButtonGlyph();
    }

    private void UpdateMaxButtonGlyph()
    {
        string key = WindowState == WindowState.Maximized ? "Icon.Restore" : "Icon.Maximize";
        if (TryFindResource(key) is Geometry g && MaxButtonPath is not null)
            MaxButtonPath.Data = g;
    }

    // ---- auto-select popup -------------------------------------------------

    private void OnAutoCheckStarted()
    {
        // A review popup left open from a previous run is reused — just bring it forward.
        if (_checkWindow is not null)
        {
            _checkWindow.Show();
            _checkWindow.WindowState = WindowState.Normal;
            _checkWindow.Activate();
            return;
        }
        _checkWindow = new CheckWindow { Owner = this, DataContext = _vm };
        _checkWindow.Closed += (_, _) => _checkWindow = null;
        _checkWindow.Show();
    }

    private void OnShowStrategyCheck(object sender, RoutedEventArgs e) => OnAutoCheckStarted();

    private void OnAutoCheckFinished() => ShowToast(Loc.T("Проверка стратегий завершена"), _vm.AutoStatusText);

    // ---- tray --------------------------------------------------------------

    private sealed class DarkMenuColorTable : Forms.ProfessionalColorTable
    {
        public override Drawing.Color MenuBorder => Drawing.Color.FromArgb(48, 44, 58);
        public override Drawing.Color MenuItemBorder => Drawing.Color.Transparent;
        public override Drawing.Color ToolStripDropDownBackground => Drawing.Color.FromArgb(20, 19, 25);
        public override Drawing.Color ImageMarginGradientBegin => Drawing.Color.FromArgb(20, 19, 25);
        public override Drawing.Color ImageMarginGradientMiddle => Drawing.Color.FromArgb(20, 19, 25);
        public override Drawing.Color ImageMarginGradientEnd => Drawing.Color.FromArgb(20, 19, 25);
        public override Drawing.Color MenuItemSelected => Drawing.Color.FromArgb(38, 35, 48);
        public override Drawing.Color MenuItemSelectedGradientBegin => Drawing.Color.FromArgb(38, 35, 48);
        public override Drawing.Color MenuItemSelectedGradientEnd => Drawing.Color.FromArgb(38, 35, 48);
        public override Drawing.Color MenuItemPressedGradientBegin => Drawing.Color.FromArgb(46, 42, 60);
        public override Drawing.Color MenuItemPressedGradientEnd => Drawing.Color.FromArgb(46, 42, 60);
        public override Drawing.Color SeparatorDark => Drawing.Color.FromArgb(42, 38, 52);
        public override Drawing.Color SeparatorLight => Drawing.Color.Transparent;
    }

    private sealed record TrayStatusInfo(bool BypassRunning, string BypassText, bool TgRunning, string TgText);

    private sealed class DarkMenuRenderer : Forms.ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkMenuColorTable()) { }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            if (e.Item.Tag is TrayStatusInfo info)
            {
                var g = e.Graphics;
                g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;

                int x = e.TextRectangle.X + 2;
                int y = e.TextRectangle.Y;
                int h = e.TextRectangle.Height;

                var font = e.TextFont ?? e.Item.Font;

                // Color constants strictly matching the GUI semantic palette
                using var greenDotBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(56, 184, 139)); // #38B88B
                using var grayDotBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(113, 112, 123));  // #71707B
                using var textBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(224, 220, 230));
                using var sepBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(80, 75, 90));

                // 1. Bypass status dot
                int dotSize = 7;
                int dotY = y + (h - dotSize) / 2;
                g.FillEllipse(info.BypassRunning ? greenDotBrush : grayDotBrush, x, dotY, dotSize, dotSize);
                x += dotSize + 6;

                // 2. Bypass label & status
                string bpText = $"Обход: {info.BypassText}";
                g.DrawString(bpText, font, textBrush, x, y);
                x += (int)g.MeasureString(bpText, font).Width;

                // 3. Separator
                string sep = "   |   ";
                g.DrawString(sep, font, sepBrush, x, y);
                x += (int)g.MeasureString(sep, font).Width;

                // 4. Telegram status dot
                g.FillEllipse(info.TgRunning ? greenDotBrush : grayDotBrush, x, dotY, dotSize, dotSize);
                x += dotSize + 6;

                // 5. Telegram label & status
                string tgText = $"TG: {info.TgText}";
                g.DrawString(tgText, font, textBrush, x, y);
                return;
            }

            e.TextColor = e.Item.Enabled ? Drawing.Color.FromArgb(234, 231, 238) : Drawing.Color.FromArgb(140, 134, 150);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            if (e.Item.Selected && e.Item.Enabled)
            {
                using var brush = new Drawing.SolidBrush(Drawing.Color.FromArgb(38, 35, 48));
                e.Graphics.FillRectangle(brush, new Drawing.Rectangle(3, 1, e.Item.Width - 6, e.Item.Height - 2));
            }
            else
            {
                base.OnRenderMenuItemBackground(e);
            }
        }
    }

    private void SetupTray()
    {
        if (_tray is not null) return;
        _iconIdle = LoadIcon("app.ico");
        _iconRunning = LoadIcon("app-on.ico");

        _trayMenu = new Forms.ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            BackColor = Drawing.Color.FromArgb(20, 19, 25),
            ForeColor = Drawing.Color.FromArgb(234, 231, 238),
            ShowImageMargin = false,
            Font = new Drawing.Font("Segoe UI", 9.5f)
        };
        _trayStatus = new Forms.ToolStripMenuItem("GXBRIEL_ZAPRET") { Enabled = false, Tag = "status", Font = new Drawing.Font("Segoe UI", 9.5f, Drawing.FontStyle.Bold) };
        _trayMenu.Items.Add(_trayStatus);
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add(Loc.T("Открыть"), null, (_, _) => ShowFromTray());
        _trayToggle = new Forms.ToolStripMenuItem(Loc.T("Запустить обход"), null, (_, _) => _vm.ToggleCommand.Execute(null));
        _trayMenu.Items.Add(_trayToggle);
        _trayToggleTg = new Forms.ToolStripMenuItem(Loc.T("Запустить Telegram"), null, (_, _) => _vm.EnableTelegramCommand.Execute(null));
        _trayMenu.Items.Add(_trayToggleTg);
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add(Loc.T("Запустить Antigravity"), null, (_, _) => _vm.LaunchAntigravityCommand.Execute(null));
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add(Loc.T("Выход"), null, (_, _) => ExitApp());

        _tray = new Forms.NotifyIcon
        {
            Icon = _iconIdle ?? Drawing.SystemIcons.Application,
            Visible = true,
            Text = Loc.T("GXBRIEL_ZAPRET — остановлен"),
            ContextMenuStrip = _trayMenu,
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();
        UpdateTrayForState(_vm.State);
    }

    private void UpdateTrayForState(EngineState state)
    {
        if (_tray is null) return;

        bool running = state == EngineState.Running;
        _tray.Icon = running ? (_iconRunning ?? _iconIdle) : _iconIdle;
        string status = state switch
        {
            EngineState.Running => Loc.T("работает"),
            EngineState.Starting => Loc.T("запуск…"),
            EngineState.Stopping => Loc.T("остановка…"),
            _ => Loc.T("остановлен"),
        };
        string? preset = _vm.RunningPreset?.Name;
        string text = $"GXBRIEL_ZAPRET — {status}";
        if (running && !string.IsNullOrEmpty(preset)) text += $"\n{preset}";
        // NotifyIcon tooltip is capped at 127 chars.
        _tray.Text = text.Length > 127 ? text[..127] : text;
        if (_trayToggle is not null)
            _trayToggle.Text = running ? Loc.T("Остановить обход") : Loc.T("Запустить обход");

        if (_trayToggleTg is not null)
            _trayToggleTg.Text = _vm.IsTelegramProxyRunning ? Loc.T("Остановить Telegram") : Loc.T("Запустить Telegram");
        if (_trayStatus is not null)
        {
            string tgStatus = _vm.IsTelegramProxyRunning ? Loc.T("ВКЛ") : Loc.T("ВЫКЛ");
            _trayStatus.Tag = new TrayStatusInfo(running, status, _vm.IsTelegramProxyRunning, tgStatus);
            _trayStatus.Text = $"   Обход: {status}   |     TG: {tgStatus} ";
        }

        // Toasts are emitted by the ViewModel via Notify; the tray only reflects state.
        _lastState = state;
    }

    internal void CaptureTrayMenu(string path)
    {
        if (_trayMenu is null) SetupTray();
        if (_trayMenu is null) return;

        UpdateTrayForState(_vm.State);
        _trayMenu.Show(0, 0);
        _trayMenu.Update();
        int menuW = Math.Max(_trayMenu.Width, 260);
        int menuH = Math.Max(_trayMenu.Height, 170);

        int canvasW = menuW + 80;
        int canvasH = menuH + 70;

        using var bmp = new Drawing.Bitmap(canvasW, canvasH);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(Drawing.Color.FromArgb(11, 10, 14));

            // Windows taskbar simulation at the bottom
            using var taskbarBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(16, 15, 20));
            g.FillRectangle(taskbarBrush, 0, canvasH - 44, canvasW, 44);

            using var pen = new Drawing.Pen(Drawing.Color.FromArgb(32, 30, 40));
            g.DrawLine(pen, 0, canvasH - 44, canvasW, canvasH - 44);

            // Draw tray icon
            if (_iconIdle != null)
            {
                g.DrawIcon(_iconIdle, canvasW - 40, canvasH - 36);
            }

            // Draw context menu on the simulated desktop
            _trayMenu.DrawToBitmap(bmp, new Drawing.Rectangle(20, 15, menuW, menuH));
        }

        _trayMenu.Hide();
        bmp.Save(path, Drawing.Imaging.ImageFormat.Png);
    }

    private DispatcherTimer? _inAppToastTimer;

    /// <summary>Show one of our corner toasts, honouring the notifications + sound settings. Safe to
    /// call from any thread (marshals to the UI thread). Replaces the old Windows tray balloon tips.</summary>
    private void ShowToast(string title, string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ShowToast(title, message));
            return;
        }

        _vm.StatusMessage = message;

        // In-app floating toast (shown when window is visible and notifications enabled)
        if (_vm.Settings.NotificationsEnabled)
        {
            try { ShowInAppToast(title, message); } catch { /* toast is best-effort */ }
            try { _toasts.Show(title, message, _vm.Settings.NotificationSound); } catch { /* toast is best-effort */ }
        }
    }

    private void ShowInAppToast(string title, string message)
    {
        if (InAppToastCard is null) return;
        var type = ToastNotifier.DetectNotificationType(title, message);
        InAppToastTitle.Text = title;
        InAppToastMessage.Text = message;

        switch (type)
        {
            case NotificationType.Success:
                InAppToastCard.BorderBrush = (Brush)FindResource("AccentBrush");
                InAppToastIconBorder.Background = (Brush)FindResource("AccentSoftBrush");
                InAppToastIcon.Data = (Geometry)FindResource("Icon.Check");
                InAppToastIcon.Fill = (Brush)FindResource("AccentBrush");
                break;
            case NotificationType.Warning:
            case NotificationType.ActionRequired:
                InAppToastCard.BorderBrush = (Brush)FindResource("WarnBrush");
                InAppToastIconBorder.Background = (Brush)FindResource("WarnSoftBrush");
                InAppToastIcon.Data = (Geometry)FindResource("Icon.AlertTriangle");
                InAppToastIcon.Fill = (Brush)FindResource("WarnBrush");
                break;
            case NotificationType.Error:
                InAppToastCard.BorderBrush = (Brush)FindResource("DangerBrush");
                InAppToastIconBorder.Background = (Brush)FindResource("DangerSoftBrush");
                InAppToastIcon.Data = (Geometry)FindResource("Icon.AlertCircle");
                InAppToastIcon.Fill = (Brush)FindResource("DangerBrush");
                break;
            default:
                InAppToastCard.BorderBrush = (Brush)FindResource("BorderBrush2");
                InAppToastIconBorder.Background = (Brush)FindResource("SurfaceAltBrush");
                InAppToastIcon.Data = (Geometry)FindResource("Icon.Activity");
                InAppToastIcon.Fill = (Brush)FindResource("FgBrush");
                break;
        }

        InAppToastCard.Visibility = Visibility.Visible;
        InAppToastCard.Opacity = 1.0;

        _inAppToastTimer?.Stop();
        _inAppToastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4.5) };
        _inAppToastTimer.Tick += (_, _) =>
        {
            _inAppToastTimer?.Stop();
            DismissInAppToast();
        };
        _inAppToastTimer.Start();
    }

    private void DismissInAppToast()
    {
        if (InAppToastCard is null || InAppToastCard.Visibility != Visibility.Visible) return;
        _inAppToastTimer?.Stop();
        var anim = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(200));
        anim.Completed += (_, _) =>
        {
            InAppToastCard.Visibility = Visibility.Collapsed;
        };
        InAppToastCard.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private void OnCloseInAppToast(object sender, RoutedEventArgs e)
    {
        DismissInAppToast();
    }

    private static Drawing.Icon? LoadIcon(string name)
    {
        try
        {
            var uri = new Uri($"pack://application:,,,/Assets/{name}");
            var info = Application.GetResourceStream(uri);
            if (info is null) return null;
            using var s = info.Stream;
            return new Drawing.Icon(s);
        }
        catch { return null; }
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
    }

    private void ShowFromTray()
    {
        App.MainWindowShowCount++;
        Show();
        Visibility = Visibility.Visible;
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>
    /// Bring this window back to the user — used when a SECOND launch is folded into this instance
    /// (see <see cref="App"/>'s single-instance claim). Unlike the tray path, the request comes from
    /// another process, so Windows' foreground lock applies to us and <c>Activate()</c> alone can
    /// leave the window buried; a brief Topmost flip reliably raises it.
    /// </summary>
    internal void SurfaceWindow()
    {
        ShowFromTray();
        Topmost = true;
        Topmost = false;
    }

    // ---- close / exit ------------------------------------------------------

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_reallyClose) { CleanupAndShutdown(); return; }

        if (_vm.MinimizeToTray)
        {
            e.Cancel = true;
            HideToTray();
        }
        else
        {
            CleanupAndShutdown();
        }
    }

    internal void ExitApp()
    {
        _reallyClose = true;
        Close();
    }

    private bool _cleanedUp;
    private void CleanupAndShutdown()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;

        if (_checkWindow is not null)
        {
            _checkWindow.DataContext = null;
            _checkWindow.Close();
        }
        try { _vm.Shutdown(); } catch { }
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        _trayMenu?.Dispose(); _trayMenu = null;
        _iconIdle?.Dispose();
        _iconRunning?.Dispose();
        Application.Current.Shutdown();
    }
}
