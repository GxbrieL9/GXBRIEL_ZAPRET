using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace GxbrielZapret.Controls;

/// <summary>
/// Attached behavior for interactive cursor-following radial glow on Hero CTA buttons and action cards.
/// Provides normalized coordinates (0.0 - 1.0) and updates any child RadialGradientBrush directly for 60fps responsiveness.
/// </summary>
public static class CursorGlowBehavior
{
    private sealed class GlowState
    {
        public double CurrentX = 0.5;
        public double CurrentY = 0.5;
        public double TargetX = 0.5;
        public double TargetY = 0.5;
        public bool IsAnimating;
    }

    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached(
            "State",
            typeof(GlowState),
            typeof(CursorGlowBehavior),
            new PropertyMetadata(null));

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(CursorGlowBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty GlowXProperty =
        DependencyProperty.RegisterAttached(
            "GlowX",
            typeof(double),
            typeof(CursorGlowBehavior),
            new PropertyMetadata(0.5));

    public static readonly DependencyProperty GlowYProperty =
        DependencyProperty.RegisterAttached(
            "GlowY",
            typeof(double),
            typeof(CursorGlowBehavior),
            new PropertyMetadata(0.5));

    public static readonly DependencyProperty IsHoveredProperty =
        DependencyProperty.RegisterAttached(
            "IsHovered",
            typeof(bool),
            typeof(CursorGlowBehavior),
            new PropertyMetadata(false));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    public static double GetGlowX(DependencyObject obj) => (double)obj.GetValue(GlowXProperty);
    public static void SetGlowX(DependencyObject obj, double value) => obj.SetValue(GlowXProperty, value);

    public static double GetGlowY(DependencyObject obj) => (double)obj.GetValue(GlowYProperty);
    public static void SetGlowY(DependencyObject obj, double value) => obj.SetValue(GlowYProperty, value);

    public static bool GetIsHovered(DependencyObject obj) => (bool)obj.GetValue(IsHoveredProperty);
    public static void SetIsHovered(DependencyObject obj, bool value) => obj.SetValue(IsHoveredProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;

        if ((bool)e.NewValue)
        {
            fe.MouseMove += OnMouseMove;
            fe.MouseEnter += OnMouseEnter;
            fe.MouseLeave += OnMouseLeave;
        }
        else
        {
            fe.MouseMove -= OnMouseMove;
            fe.MouseEnter -= OnMouseEnter;
            fe.MouseLeave -= OnMouseLeave;
        }
    }

    private static void OnMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            SetIsHovered(fe, true);
            UpdateGlowPosition(fe, e);
        }
    }

    private static void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            SetIsHovered(fe, false);
            SetTargetGlowPosition(fe, 0.5, 0.5);
        }
    }

    private static void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            UpdateGlowPosition(fe, e);
        }
    }

    private static void UpdateGlowPosition(FrameworkElement fe, MouseEventArgs e)
    {
        if (fe.ActualWidth <= 0 || fe.ActualHeight <= 0) return;
        Point pos = e.GetPosition(fe);
        double nx = Math.Clamp(pos.X / fe.ActualWidth, 0.0, 1.0);
        double ny = Math.Clamp(pos.Y / fe.ActualHeight, 0.0, 1.0);
        SetTargetGlowPosition(fe, nx, ny);
    }

    public static void SetGlowPosition(FrameworkElement fe, double x, double y)
    {
        double nx = Math.Clamp(x, 0.0, 1.0);
        double ny = Math.Clamp(y, 0.0, 1.0);
        SetIsHovered(fe, true);
        SetTargetGlowPosition(fe, nx, ny);
    }

    private static void SetTargetGlowPosition(FrameworkElement fe, double x, double y)
    {
        var state = GetOrCreateState(fe);
        state.TargetX = x;
        state.TargetY = y;

        if (!state.IsAnimating)
        {
            state.IsAnimating = true;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    private static GlowState GetOrCreateState(FrameworkElement fe)
    {
        if (fe.GetValue(StateProperty) is GlowState existing)
        {
            return existing;
        }

        var state = new GlowState
        {
            CurrentX = GetGlowX(fe),
            CurrentY = GetGlowY(fe),
            TargetX = GetGlowX(fe),
            TargetY = GetGlowY(fe)
        };
        fe.SetValue(StateProperty, state);
        fe.Unloaded += OnElementUnloaded;
        return state;
    }

    private static void OnElementUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            fe.ClearValue(StateProperty);
            fe.Unloaded -= OnElementUnloaded;
        }
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        bool anyAnimating = false;

        foreach (var fe in EnumerateOpenWindows().SelectMany(FindGlowElements))
        {
            if (fe.GetValue(StateProperty) is not GlowState state || !state.IsAnimating)
            {
                continue;
            }

            anyAnimating = true;
            state.CurrentX = Lerp(state.CurrentX, state.TargetX, 0.22);
            state.CurrentY = Lerp(state.CurrentY, state.TargetY, 0.22);

            if (Math.Abs(state.CurrentX - state.TargetX) < 0.002 &&
                Math.Abs(state.CurrentY - state.TargetY) < 0.002)
            {
                state.CurrentX = state.TargetX;
                state.CurrentY = state.TargetY;
                state.IsAnimating = false;
            }

            SetGlowX(fe, state.CurrentX);
            SetGlowY(fe, state.CurrentY);
            ApplyRadialBrushPoint(fe, state.CurrentX, state.CurrentY);
        }

        if (!anyAnimating)
        {
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    private static IEnumerable<Window> EnumerateOpenWindows()
    {
        foreach (Window window in Application.Current.Windows)
        {
            yield return window;
        }
    }

    private static IEnumerable<FrameworkElement> FindGlowElements(DependencyObject root)
    {
        if (root is FrameworkElement fe && fe.GetValue(StateProperty) is GlowState)
        {
            yield return fe;
        }

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            foreach (var child in FindGlowElements(VisualTreeHelper.GetChild(root, i)))
            {
                yield return child;
            }
        }
    }

    private static double Lerp(double from, double to, double amount)
    {
        return from + (to - from) * amount;
    }

    private static void ApplyRadialBrushPoint(FrameworkElement fe, double x, double y)
    {
        var pt = new Point(x, y);

        // 1. Direct Background check
        if (fe is Control ctrl && ctrl.Background is RadialGradientBrush rbg)
        {
            if (rbg.IsFrozen)
            {
                var clone = rbg.Clone();
                clone.Center = pt;
                clone.GradientOrigin = pt;
                ctrl.Background = clone;
            }
            else
            {
                rbg.Center = pt;
                rbg.GradientOrigin = pt;
            }
            return;
        }

        // 2. Named template child "glowOverlay"
        if (fe is Control templateOwner && templateOwner.Template != null)
        {
            if (templateOwner.Template.FindName("glowOverlay", templateOwner) is Border glowBorder &&
                glowBorder.Background is RadialGradientBrush glowRgb)
            {
                if (glowRgb.IsFrozen)
                {
                    var clone = glowRgb.Clone();
                    clone.Center = pt;
                    clone.GradientOrigin = pt;
                    glowBorder.Background = clone;
                }
                else
                {
                    glowRgb.Center = pt;
                    glowRgb.GradientOrigin = pt;
                }
                return;
            }
        }
    }
}
