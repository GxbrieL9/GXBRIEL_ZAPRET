using System.IO;
using System.Media;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace GxbrielZapret.Views;

public enum NotificationType
{
    Success,
    Info,
    Warning,
    Error,
    ActionRequired
}

/// <summary>
/// Anti-spam queue: handles deduplication, burst coalescing/grouping, and queue rate-limiting.
/// Thread-safe and independent of UI rendering (fully testable).
/// </summary>
public sealed class NotificationQueue
{
    public enum PostAction
    {
        ShowImmediately,
        DropDuplicate,
        GroupBurst
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, DateTime> _history = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _dedupWindow;
    private int _burstCount;
    private DateTime _lastEventTime = DateTime.MinValue;

    public NotificationQueue(TimeSpan? dedupWindow = null)
    {
        _dedupWindow = dedupWindow ?? TimeSpan.FromSeconds(2.0);
    }

    public PostAction Enqueue(string title, string message, out string finalTitle, out string finalMessage)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            string key = $"{title}::{message}";

            // 1. Deduplication check
            if (_history.TryGetValue(key, out var prev) && (now - prev) < _dedupWindow)
            {
                finalTitle = title;
                finalMessage = message;
                return PostAction.DropDuplicate;
            }

            _history[key] = now;

            // Prune history
            if (_history.Count > 100)
            {
                var stale = _history.Where(kv => (now - kv.Value) > TimeSpan.FromSeconds(10)).Select(kv => kv.Key).ToList();
                foreach (var k in stale) _history.Remove(k);
            }

            // 2. Burst rate check (< 600ms apart)
            if ((now - _lastEventTime) < TimeSpan.FromMilliseconds(600))
            {
                _burstCount++;
                if (_burstCount > 3)
                {
                    finalTitle = "GXBRIEL_ZAPRET";
                    finalMessage = $"События сгруппированы ({_burstCount} действий)";
                    return PostAction.GroupBurst;
                }
            }
            else
            {
                _burstCount = 1;
            }

            _lastEventTime = now;
            finalTitle = title;
            finalMessage = message;
            return PostAction.ShowImmediately;
        }
    }
}

/// <summary>
/// Lightweight in-house toast notifications: a small topmost popup in the bottom-right corner of the
/// work area — visible even when the main window is hidden in the tray (unlike an in-window overlay).
/// Replaces the Windows balloon tips with a quieter, on-brand toast plus a soft synthesized sound.
/// Toasts stack upward and auto-dismiss. All calls must be made on the UI thread.
/// </summary>
public sealed class ToastNotifier
{
    private readonly List<ToastWindow> _open = new();
    private readonly NotificationQueue _queue = new();
    private static byte[]? _blipWav;
    private static SoundPlayer? _player;   // kept alive so async Play() isn't cut off by GC

    public static NotificationType DetectNotificationType(string title, string message)
    {
        string text = $"{title} {message}".ToLowerInvariant();
        if (text.Contains("ошибк") || text.Contains("упал") || text.Contains("не удалось") || text.Contains("заблокирован") || text.Contains("удален") || text.Contains("error") || text.Contains("fail"))
            return NotificationType.Error;
        if (text.Contains("требуется") || text.Contains("действие") || text.Contains("обновит") || text.Contains("переподбор") || text.Contains("action"))
            return NotificationType.ActionRequired;
        if (text.Contains("внимани") || text.Contains("предупрежден") || text.Contains("занят") || text.Contains("недоступн") || text.Contains("warn"))
            return NotificationType.Warning;
        if (text.Contains("успешн") || text.Contains("включён") || text.Contains("включен") || text.Contains("готов") || text.Contains("сохранен") || text.Contains("подтвержден") || text.Contains("восстановлен") || text.Contains("ok") || text.Contains("разблокирован") || text.Contains("success"))
            return NotificationType.Success;
        return NotificationType.Info;
    }

    /// <summary>Show a toast in the bottom-right corner. <paramref name="sound"/> plays the soft blip.</summary>
    public void Show(string title, string message, bool sound, NotificationType? type = null)
    {
        var action = _queue.Enqueue(title, message, out string finalTitle, out string finalMessage);
        if (action == NotificationQueue.PostAction.DropDuplicate)
            return;

        var resolvedType = type ?? DetectNotificationType(finalTitle, finalMessage);

        // Cap the stack so a burst of events can't wallpaper the screen — drop the oldest.
        if (_open.Count >= 3) { try { _open[0].Close(); } catch { /* removed via Closed */ } }

        var toast = new ToastWindow(finalTitle, finalMessage, resolvedType);
        toast.Closed += (_, _) => { _open.Remove(toast); Reflow(); };
        toast.ContentRendered += (_, _) => Reflow();   // real ActualHeight is known only after layout
        _open.Add(toast);
        toast.Show();
        Reflow();
        if (sound) PlayBlip();
    }

    /// <summary>Re-stack open toasts up from the bottom-right corner (newest lowest).</summary>
    private void Reflow()
    {
        var wa = SystemParameters.WorkArea;
        double y = wa.Bottom - 14;
        for (int i = _open.Count - 1; i >= 0; i--)
        {
            var t = _open[i];
            double h = t.ActualHeight > 1 ? t.ActualHeight : 76;
            t.Left = wa.Right - t.Width - 14;
            y -= h;
            t.Top = y;
            y -= 10;   // gap between toasts
        }
    }

    private static void PlayBlip()
    {
        try
        {
            _blipWav ??= BuildBlipWav();
            _player = new SoundPlayer(new MemoryStream(_blipWav));
            _player.Play();   // async on a worker thread
        }
        catch { /* sound is best-effort */ }
    }

    private static byte[] BuildBlipWav()
    {
        const int sr = 44100;
        const double f0 = 587.33;          // D5 — clear of the 2–5 kHz band where the ear is harshest
        int n = (int)(sr * 0.26);
        int fade = (int)(sr * 0.02);       // tail fade, so the buffer ends at silence and not on a step
        var samples = new short[n];
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)sr;
            double attack = t < 0.007 ? 0.5 - 0.5 * Math.Cos(Math.PI * t / 0.007) : 1.0;
            double env = attack * Math.Exp(-t / 0.075) * Math.Min(1.0, (n - i) / (double)fade);
            double tone = Math.Sin(2 * Math.PI * f0 * t)
                        + 0.16 * Math.Sin(4 * Math.PI * f0 * t) * Math.Exp(-t / 0.030)
                        + 0.05 * Math.Sin(6 * Math.PI * f0 * t) * Math.Exp(-t / 0.018);
            samples[i] = (short)(tone * env * 0.11 * short.MaxValue);
        }
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataBytes = n * 2;
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataBytes);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16); w.Write((short)1); w.Write((short)1);          // PCM, mono
        w.Write(sr); w.Write(sr * 2); w.Write((short)2); w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(dataBytes);
        foreach (var s in samples) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }
}

/// <summary>A single auto-dismissing toast popup window (borderless, transparent, click-to-close).</summary>
internal sealed class ToastWindow : Window
{
    private readonly TranslateTransform _slide = new(0, 12);
    private bool _closing;

    public ToastWindow(string title, string message, NotificationType type = NotificationType.Info)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;            // never steal focus from the foreground app
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = 340;
        Opacity = 0;

        Color stripeColor = type switch
        {
            NotificationType.Success => Color.FromRgb(0x10, 0xB9, 0x81),
            NotificationType.Warning => Color.FromRgb(0xF5, 0x9E, 0x0B),
            NotificationType.Error => Color.FromRgb(0xEF, 0x44, 0x44),
            NotificationType.ActionRequired => Color.FromRgb(0x8B, 0x5C, 0xF6),
            _ => Color.FromRgb(0x38, 0xBD, 0xF8)
        };
        Brush accent = new SolidColorBrush(stripeColor);
        Brush surface = Res("SurfaceBrush", Color.FromRgb(0x0E, 0x0E, 0x12));
        Brush border = Res("BorderBrush2", Color.FromRgb(0x24, 0x24, 0x2D));
        Brush fg = Res("FgBrush", Color.FromRgb(0xF4, 0xF4, 0xF6));
        Brush muted = Res("FgMutedBrush", Color.FromRgb(0xA1, 0xA1, 0xAC));

        var stripe = new Border { Width = 4, Background = accent, CornerRadius = new CornerRadius(2) };
        var titleTb = new TextBlock { Text = title, Foreground = fg, FontWeight = FontWeights.SemiBold, FontSize = 13 };
        var msgTb = new TextBlock
        {
            Text = message, Foreground = muted, FontSize = 12,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0),
        };
        var text = new StackPanel { Margin = new Thickness(12, 0, 4, 0) };
        text.Children.Add(titleTb);
        text.Children.Add(msgTb);

        var row = new DockPanel();
        DockPanel.SetDock(stripe, Dock.Left);
        row.Children.Add(stripe);
        row.Children.Add(text);

        Content = new Border
        {
            Background = surface,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 11, 14, 12),
            Child = row,
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.45, Color = Colors.Black },
            RenderTransform = _slide,
        };

        MouseLeftButtonUp += (_, _) => Dismiss();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        var dur = TimeSpan.FromMilliseconds(220);
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur));
        _slide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(12, 0, dur) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4.5) };
        timer.Tick += (_, _) => { timer.Stop(); Dismiss(); };
        timer.Start();
    }

    /// <summary>Fade out, then close (idempotent).</summary>
    public void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(200));
        fade.Completed += (_, _) => { try { Close(); } catch { /* already closing */ } };
        BeginAnimation(OpacityProperty, fade);
    }

    private static Brush Res(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}
