using System.Windows;
using System.Windows.Input;
using GxbrielZapret.Localization;
using GxbrielZapret.Services.Platform;

namespace GxbrielZapret.Views;

/// <summary>
/// App-styled advisory listing what in the environment can stop the bypass from working
/// (see <see cref="ConflictScanService"/>). Every row carries the fix, not just the diagnosis.
/// Shown automatically at startup when conflicts are found, and on demand from
/// Настройки → «Проверить окружение» — where it also renders the all-clear state.
/// Purely informational — it never blocks the launch.
/// </summary>
public partial class ConflictDialog : Window
{
    private IReadOnlyList<EnvFinding> _items;

    private ConflictDialog(IReadOnlyList<EnvFinding> items)
    {
        InitializeComponent();
        _items = items;

        if (items.Count == 0)
        {
            HeadText.Text = Loc.T("Всё в порядке");
            SubText.Text = Loc.T("Проверка не нашла в системе ничего, что мешало бы обходу.");
            items = new[]
            {
                new EnvFinding(EnvSeverity.Ok, Loc.T("Помех не найдено"),
                    Loc.T("Конфликтующих программ и VPN-туннелей не видно, движок на месте. Если обход всё " +
                    "равно не работает — причина в стратегии, а не в окружении: попробуйте «Подобрать " +
                    "стратегию» на главной странице."),
                    ""),
            };
        }
        else
        {
            HeadText.Text = Loc.T("Проверка окружения");
            SubText.Text = Loc.T("Вот что может помешать обходу. У каждого пункта — что это значит и что с этим делать.");
        }

        List.ItemsSource = items;
        FixButton.Visibility = items.Any(i => string.Equals(i.FixKey, "dpi", StringComparison.OrdinalIgnoreCase))
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>
    /// Harness hook: build the dialog WITHOUT showing it modally, so the screenshot harness can render
    /// it (App.xaml.cs <c>--screenshot</c>). A styling bug in here is invisible in the tab screenshots,
    /// which is exactly how the unreadable "Что делать" line shipped once — hence this.
    /// </summary>
    internal static Window CreateForHarness(IReadOnlyList<EnvFinding> items) => new ConflictDialog(items);

    /// <summary>Shows the advisory modally (no-op semantics — nothing is returned).</summary>
    public static void Show(IReadOnlyList<EnvFinding> items)
    {
        var owner = Application.Current?.MainWindow;
        var dlg = new ConflictDialog(items);
        if (owner is not null && owner.IsLoaded) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dlg.ShowDialog();
    }

    private void OnOk(object sender, RoutedEventArgs e) => Close();

    private async void OnFix(object sender, RoutedEventArgs e)
    {
        FixButton.IsEnabled = false;
        FixButton.Content = Loc.T("Закрываем…");
        int stopped = await Task.Run(ConflictScanService.StopConflictingDpiTools);
        var remaining = await Task.Run(ConflictScanService.ScanConflicts);

        if (remaining.Any(i => string.Equals(i.FixKey, "dpi", StringComparison.OrdinalIgnoreCase)))
        {
            _items = remaining;
            HeadText.Text = Loc.T("Остались конфликты");
            SubText.Text = stopped > 0
                ? Loc.T("Часть процессов закрыта, но некоторые обходы ещё активны. Возможно, их автозапуск сразу вернул процесс.")
                : Loc.T("Не удалось закрыть найденные обходы. Запустите приложение от имени администратора или закройте их через трей.");
            List.ItemsSource = _items;
            FixButton.Content = Loc.T("Повторить закрытие");
            FixButton.IsEnabled = true;
            return;
        }

        _items = Array.Empty<EnvFinding>();
        HeadText.Text = Loc.T("Конфликты закрыты");
        SubText.Text = stopped > 0
            ? Loc.T("Сторонние DPI-обходы остановлены. Теперь можно включать GXBRIEL_ZAPRET.")
            : Loc.T("Активных сторонних DPI-обходов больше не видно.");
        List.ItemsSource = new[]
        {
            new EnvFinding(EnvSeverity.Ok, Loc.T("Помех не найдено"),
                Loc.T("Конфликтующие DPI-обходы не обнаружены. Если VPN включён отдельно, отключите его вручную."), "")
        };
        FixButton.Visibility = Visibility.Collapsed;
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
