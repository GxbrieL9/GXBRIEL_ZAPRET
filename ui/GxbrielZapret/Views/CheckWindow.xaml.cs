using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GxbrielZapret.Localization;
using GxbrielZapret.ViewModels;

namespace GxbrielZapret.Views;

/// <summary>
/// Review popup shown while the auto-selector / generator runs: live per-candidate progress on the
/// left, strategies that passed on the right. It does NOT auto-close when the run finishes — it stays
/// open so the user can review results and apply a strategy. Hiding the window keeps the run alive.
/// </summary>
public partial class CheckWindow : Window
{
    private MainViewModel? _viewModel;
    public CheckWindow() => InitializeComponent();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Honor the app-wide UI zoom so the popup matches the main window on high-DPI / 4K screens.
        var wa = SystemParameters.WorkArea;
        if (DataContext is MainViewModel vm)
        {
            if (!ReferenceEquals(_viewModel, vm))
            {
                if (_viewModel is not null) _viewModel.PropertyChanged -= OnProgressChanged;
                _viewModel = vm;
                vm.PropertyChanged += OnProgressChanged;
            }
            double scale = Math.Min(vm.UiScale, Math.Min((wa.Width - 24) / 880, (wa.Height - 24) / 560));
            ContentRoot.LayoutTransform = new ScaleTransform(scale, scale);
            MinWidth = Math.Min(880 * scale, wa.Width - 24);
            MinHeight = Math.Min(560 * scale, wa.Height - 24);
            Width = Math.Min(1120 * scale, wa.Width - 24);
            Height = Math.Min(720 * scale, wa.Height - 24);
        }
        Left = Math.Clamp(Left, wa.Left, Math.Max(wa.Left, wa.Right - Width));
        Top = Math.Clamp(Top, wa.Top, Math.Max(wa.Top, wa.Bottom - Height));
        UpdateProgressTitle();
        if (Owner is not null)
        {
            Left = Owner.Left + (Owner.ActualWidth - Width) / 2;
            Top = Owner.Top + (Owner.ActualHeight - Height) / 2;
        }
    }

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source)
        {
            for (var element = source; element is not null; element = element is Visual
                ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
                if (element is System.Windows.Controls.Primitives.ButtonBase) return;
        }
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnWindowStateChanged(object? sender, EventArgs e)
        => UpdateProgressTitle();

    private void OnProgressChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.AutoProgressText) or nameof(MainViewModel.IsAutoRunning))
            UpdateProgressTitle();
    }

    private void UpdateProgressTitle() => Title = "GXBRIEL_ZAPRET — " +
        (_viewModel?.IsAutoRunning == true ? _viewModel.AutoProgressText : Loc.T("Результаты проверки"));

    protected override void OnClosing(CancelEventArgs e)
    {
        if (DataContext is MainViewModel { IsAutoRunning: true })
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnProgressChanged;
        base.OnClosed(e);
    }
}
