using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using GxbrielZapret.Localization;
using GxbrielZapret.Models;
using GxbrielZapret.Services.Engine;

namespace GxbrielZapret.Mvvm;

/// <summary>
/// Two bound values -> Visibility: Visible when they are the SAME instance.
/// Used to flag the currently-running preset inside the preset list.
/// </summary>
public sealed class RefEqualsToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type t, object? p, CultureInfo c)
    {
        if (values.Length != 2 || values[0] is null || values[1] is null)
            return Visibility.Collapsed;
        if (ReferenceEquals(values[0], values[1]))
            return Visibility.Visible;
        if (values[0] is Preset a && values[1] is Preset b)
        {
            if ((!string.IsNullOrEmpty(a.Fingerprint) && a.Fingerprint == b.Fingerprint) ||
                string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase))
                return Visibility.Visible;
        }
        return Visibility.Collapsed;
    }
    public object[] ConvertBack(object? v, Type[] t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>Inverts a boolean.</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c) => !(v is true);
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => !(v is true);
}

/// <summary>bool -> Visibility, with optional "invert" parameter.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c)
    {
        bool b = v is true;
        if (p as string == "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) =>
        v is Visibility.Visible;
}

/// <summary>int -> bool: checks if value equals converter parameter (as int or string).</summary>
public sealed class IntEqualsConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c)
    {
        if (v is int val && int.TryParse(p?.ToString(), out int target))
            return val == target;
        return false;
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>int > 0 -> Visibility.Visible, else Collapsed.</summary>
public sealed class IntGreaterThanZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c) =>
        v is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>EngineState -> status dot colour.</summary>
public sealed class StateToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush RunningBrush = new(Color.FromRgb(0x43, 0xC5, 0x9E));
    private static readonly SolidColorBrush BusyBrush = new(Color.FromRgb(0xE9, 0xB4, 0x4C));
    private static readonly SolidColorBrush StoppedBrush = new(Color.FromRgb(0x6C, 0x6C, 0x77));

    public object Convert(object? v, Type t, object? p, CultureInfo c) => v switch
    {
        EngineState.Running => RunningBrush,
        EngineState.Starting or EngineState.Stopping => BusyBrush,
        _ => StoppedBrush,
    };
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>EngineState -> Russian label.</summary>
public sealed class StateToTextConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c) => v switch
    {
        EngineState.Running => Loc.T("Работает"),
        EngineState.Starting => Loc.T("Запуск…"),
        EngineState.Stopping => Loc.T("Остановка…"),
        _ => Loc.T("Остановлен"),
    };
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>DiagStatus -> short cell label.</summary>
public sealed class DiagStatusToTextConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c) => v switch
    {
        DiagStatus.Ok => "OK",
        DiagStatus.Fail => Loc.T("ОШИБКА"),
        DiagStatus.Timeout => Loc.T("таймаут"),
        DiagStatus.NotSupported => Loc.T("н/д"),
        DiagStatus.Running => "…",
        DiagStatus.Skip => "—",
        DiagStatus.Canceled => Loc.T("Отменено"),
        _ => "·",
    };
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>DiagStatus -> cell foreground colour.</summary>
public sealed class DiagStatusToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Ok = new(Color.FromRgb(0x34, 0xC7, 0x59));
    private static readonly SolidColorBrush Bad = new(Color.FromRgb(0xFF, 0x45, 0x3A));
    private static readonly SolidColorBrush Warn = new(Color.FromRgb(0xFF, 0x9F, 0x0A));
    private static readonly SolidColorBrush Muted = new(Color.FromRgb(0x8E, 0x8E, 0x93));

    public object Convert(object? v, Type t, object? p, CultureInfo c) => v switch
    {
        DiagStatus.Ok => Ok,
        DiagStatus.Fail => Bad,
        DiagStatus.Timeout => Warn,
        _ => Muted,
    };
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>EngineState -> soft background brush for status pills.</summary>
public sealed class StateToSoftBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush RunningSoft = new(Color.FromArgb(0x24, 0x34, 0xC7, 0x59));
    private static readonly SolidColorBrush BusySoft = new(Color.FromArgb(0x24, 0xFF, 0x9F, 0x0A));
    private static readonly SolidColorBrush StoppedSoft = new(Color.FromArgb(0x18, 0x8E, 0x8E, 0x93));

    public object Convert(object? v, Type t, object? p, CultureInfo c) => v switch
    {
        EngineState.Running => RunningSoft,
        EngineState.Starting or EngineState.Stopping => BusySoft,
        _ => StoppedSoft,
    };
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>DiagStatus -> soft background brush for status pills.</summary>
public sealed class DiagStatusToSoftBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush OkSoft = new(Color.FromArgb(0x24, 0x34, 0xC7, 0x59));
    private static readonly SolidColorBrush BadSoft = new(Color.FromArgb(0x24, 0xFF, 0x45, 0x3A));
    private static readonly SolidColorBrush WarnSoft = new(Color.FromArgb(0x24, 0xFF, 0x9F, 0x0A));
    private static readonly SolidColorBrush MutedSoft = new(Color.FromArgb(0x18, 0x8E, 0x8E, 0x93));

    public object Convert(object? v, Type t, object? p, CultureInfo c) => v switch
    {
        DiagStatus.Ok => OkSoft,
        DiagStatus.Fail => BadSoft,
        DiagStatus.Timeout or DiagStatus.Running => WarnSoft,
        _ => MutedSoft,
    };
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Non-empty string -> Visibility.Visible, else Collapsed.</summary>
public sealed class StringNotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c) =>
        !string.IsNullOrWhiteSpace(v as string) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

