using DnevnikSdelok.Data;
using DnevnikSdelok.Models;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DnevnikSdelok;

public partial class TradeDetailsPanel : UserControl
{
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private readonly DispatcherTimer _chartDebounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private JournalDatabase? _database;
    private DashboardPreferences? _preferences;
    private ExecutionRow? _row;
    private IReadOnlyList<ExecutionRow> _relatedExecutions = [];
    private bool _initializing;
    private bool _webViewReady;
    private bool _chartPageReady;
    private bool _chartInitializing;
    private bool _intervalInitialized;
    private bool _isLoaded;

    public event Action<ExecutionRow>? CollapseRequested;

    public TradeDetailsPanel()
    {
        InitializeComponent();
        IntervalSelector.SelectedIndex = 5;
        _saveTimer.Tick += SaveTimerTick;
        _saveTimer.Stop();
        _chartDebounce.Tick += ChartDebounceTick;
        _chartDebounce.Stop();
        Loaded += PanelLoaded;
        Unloaded += PanelUnloaded;
        ChartWebView.PreviewKeyDown += ChartPreviewKeyDown;
    }

    public void Initialize(
        ExecutionRow row,
        IReadOnlyList<ExecutionRow> relatedExecutions,
        JournalDatabase database,
        DashboardPreferences preferences)
    {
        _row = row;
        _relatedExecutions = relatedExecutions;
        _database = database;
        _preferences = preferences;
        _initializing = true;
        TradeTitle.Text = $"{row.Symbol} · {row.MarketLabel} · {row.SideLabel}";
        var pnl = row.PnlDisplay;
        var percent = row.ProfitPercentDisplay;
        TradeSummary.Text =
            $"{row.ExecutedAtLocal:dd.MM.yyyy HH:mm:ss} · " +
            $"{row.Price.ToString("0.####", CultureInfo.GetCultureInfo("ru-RU"))} {row.Execution.QuoteAsset} · " +
            $"P&L {pnl} ({percent})";
        DescriptionBox.Text = row.JournalDescription;
        ConclusionBox.Text = row.JournalConclusion;
        VideoUrlBox.Text = row.VideoUrl;
        ExecutionsGrid.ItemsSource = relatedExecutions
            .OrderBy(item => item.Execution.ExecutedAtUtc)
            .ToList();
        LoadScreenshot();
        _initializing = false;

        if (_isLoaded)
        {
            _ = EnsureChartAsync();
        }
    }

    private async void PanelLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        if (!_intervalInitialized)
        {
            IntervalSelector.SelectedIndex = 5;
            _intervalInitialized = true;
        }
        if (_row is not null)
        {
            await EnsureChartAsync();
        }
    }

    private void PanelUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        _chartDebounce.Stop();
        if (_saveTimer.IsEnabled)
        {
            SaveJournalEntry();
        }
        _saveTimer.Stop();
    }

    private async Task EnsureChartAsync()
    {
        if (!_isLoaded || _row is null || _preferences is null)
        {
            return;
        }

        if (_webViewReady)
        {
            if (_chartPageReady)
            {
                await LoadChartAsync();
            }
            return;
        }

        if (_chartInitializing)
        {
            return;
        }

        _chartInitializing = true;
        try
        {
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DnevnikSdelok",
                "WebView2");
            Directory.CreateDirectory(userData);
            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userData);
            await ChartWebView.EnsureCoreWebView2Async(environment);
            var core = ChartWebView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.NavigationStarting += NavigationStarting;
            core.NavigationCompleted += NavigationCompleted;
            core.WebMessageReceived += ChartWebMessageReceived;
            core.NewWindowRequested += (_, args) => args.Handled = true;
            var chartDirectory = Path.Combine(AppContext.BaseDirectory, "ChartAssets");
            if (!File.Exists(Path.Combine(chartDirectory, "chart.html")))
            {
                throw new FileNotFoundException("Не найдена локальная страница для запуска виджета TradingView.");
            }
            core.SetVirtualHostNameToFolderMapping(
                "chart.local",
                chartDirectory,
                CoreWebView2HostResourceAccessKind.DenyCors);
            _webViewReady = true;
            core.Navigate("https://chart.local/chart.html");
        }
        catch (Exception exception)
        {
            ChartStatus.Text = $"Не удалось запустить встроенный график: {exception.Message}";
        }
        finally
        {
            _chartInitializing = false;
        }
    }

    private void NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) ||
            uri.Host != "chart.local" ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            e.Cancel = true;
            ChartStatus.Text = $"Заблокирована навигация графика: {e.Uri}";
            return;
        }

        ChartStatus.Text = "Загружается встроенный график…";
    }

    private void NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            ChartStatus.Text = "Подготовлена страница встроенного графика.";
        }
        else
        {
            ChartStatus.Text = $"Не удалось открыть встроенный график: {e.WebErrorStatus}";
        }
    }

    private void ChartWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var message = e.TryGetWebMessageAsString();
        if (message == "escape")
        {
            RequestCollapse();
            return;
        }

        if (message == "chart-ready")
        {
            _chartPageReady = true;
            _ = LoadChartAsync();
            return;
        }

        if (message.StartsWith("chart-loaded:", StringComparison.Ordinal))
        {
            var parts = message.Split(':');
            var interval = (IntervalSelector.SelectedItem as ComboBoxItem)?.Content?.ToString();
            ChartStatus.Text = $"{_row?.Symbol} · {interval} · свечей: {parts[3]} · отметок: {parts[2]}";
            return;
        }

        if (message.StartsWith("chart-error:", StringComparison.Ordinal))
        {
            ChartStatus.Text = message["chart-error:".Length..];
            return;
        }
    }

    private void ChartPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape || _row is null)
        {
            return;
        }

        e.Handled = true;
        RequestCollapse();
    }

    private void RequestCollapse()
    {
        if (_row is null)
        {
            return;
        }

        var row = _row;
        Dispatcher.BeginInvoke(new Action(() => CollapseRequested?.Invoke(row)));
    }

    private void IntervalSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_webViewReady)
        {
            _chartDebounce.Stop();
            _chartDebounce.Start();
        }
    }

    private async void ChartDebounceTick(object? sender, EventArgs e)
    {
        _chartDebounce.Stop();
        await LoadChartAsync();
    }

    private async Task LoadChartAsync()
    {
        if (!_isLoaded || !_webViewReady || !_chartPageReady ||
            ChartWebView.CoreWebView2 is not { } core ||
            _row is null || _preferences is null ||
            IntervalSelector.SelectedItem is not ComboBoxItem { Tag: string interval })
        {
            return;
        }

        var selectedEntryTime = _row.IsPositionEntry
            ? _row.Execution.ExecutedAtUtc
            : _row.EntryTimeUtc;
        var priceLines = new List<object>();
        if (selectedEntryTime.HasValue)
        {
            var entry = _relatedExecutions.FirstOrDefault(item =>
                item.IsPositionEntry &&
                item.Execution.ExecutedAtUtc == selectedEntryTime.Value);
            if (entry is not null)
            {
                priceLines.Add(new { kind = "entry", price = (double)entry.Price });
            }

            var exit = _row.IsPositionExit
                ? _row
                : _relatedExecutions.FirstOrDefault(item =>
                    item.IsPositionExit && item.EntryTimeUtc == selectedEntryTime);
            if (exit is not null)
            {
                priceLines.Add(new { kind = "exit", price = (double)exit.Price });
            }
        }

        var configuration = new
        {
            symbol = _row.Symbol,
            market = _row.Execution.Market,
            interval,
            selectedTime = new DateTimeOffset(
                DateTime.SpecifyKind(_row.Execution.ExecutedAtUtc, DateTimeKind.Utc)).ToUnixTimeSeconds(),
            bullColor = _preferences.ChartBullColor,
            bearColor = _preferences.ChartBearColor,
            executions = _relatedExecutions.Select(item => new
            {
                time = new DateTimeOffset(
                    DateTime.SpecifyKind(item.Execution.ExecutedAtUtc, DateTimeKind.Utc)).ToUnixTimeSeconds(),
                side = item.Execution.Side,
                isEntry = item.IsPositionEntry,
                isExit = item.IsPositionExit
            }),
            priceLines
        };
        var json = JsonSerializer.Serialize(configuration);
        ChartStatus.Text = $"Загружаю {_row.Symbol} · {interval}…";
        try
        {
            await core.ExecuteScriptAsync($"window.setTradeChart({json})");
        }
        catch (Exception exception)
        {
            ChartStatus.Text = $"Не удалось обновить график: {exception.Message}";
        }
    }

    private void JournalTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initializing || _row is null)
        {
            return;
        }

        _row.JournalDescription = DescriptionBox.Text;
        _row.JournalConclusion = ConclusionBox.Text;
        _row.VideoUrl = VideoUrlBox.Text.Trim();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveTimerTick(object? sender, EventArgs e)
    {
        _saveTimer.Stop();
        SaveJournalEntry();
    }

    private void SaveJournalEntry()
    {
        if (_row is null || _database is null)
        {
            return;
        }

        try
        {
            _database.SaveExecutionNote(_row);
            SaveStatus.Text = $"Сохранено {DateTime.Now:HH:mm:ss}";
            SaveStatus.Foreground = Brush(_preferences?.AccentColor ?? "#00FF41");
        }
        catch (Exception exception)
        {
            SaveStatus.Text = $"Ошибка сохранения: {exception.Message}";
            SaveStatus.Foreground = Brush("#00AA2B");
        }
    }

    private void AddScreenshotClick(object sender, RoutedEventArgs e)
    {
        if (_row is null || _database is null)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Добавить скриншот к сделке",
            Filter = "Изображения PNG или JPG (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        try
        {
            _row.ScreenshotRelativePath = _database.CopyTradeImage(dialog.FileName);
            LoadScreenshot();
            SaveJournalEntry();
        }
        catch (Exception exception)
        {
            MessageBox.Show(Window.GetWindow(this),
                $"Не удалось добавить изображение: {exception.Message}",
                "Ошибка вложения", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void SaveChartScreenshotClick(object sender, RoutedEventArgs e)
    {
        if (_row is null || ChartWebView.CoreWebView2 is not { } core)
        {
            MessageBox.Show(Window.GetWindow(this),
                "Сначала дождитесь загрузки графика.",
                "График ещё не готов", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Сохранить снимок графика",
            Filter = "Изображение PNG (*.png)|*.png",
            FileName = $"{_row.Symbol}-{DateTime.Now:yyyy-MM-dd-HHmmss}.png",
            AddExtension = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        try
        {
            await using var image = new FileStream(
                dialog.FileName,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None);
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image);
            ChartStatus.Text = $"Снимок сохранён: {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception exception)
        {
            ChartStatus.Text = $"Не удалось сохранить снимок: {exception.Message}";
            MessageBox.Show(Window.GetWindow(this),
                $"Не удалось сохранить снимок графика: {exception.Message}",
                "Ошибка сохранения", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadScreenshot()
    {
        if (_row is null || _database is null)
        {
            return;
        }

        var path = _database.GetTradeImagePath(_row.ScreenshotRelativePath);
        if (path is null)
        {
            ScreenshotImage.Source = null;
            ScreenshotEmptyText.Visibility = Visibility.Visible;
            return;
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.DecodePixelHeight = 160;
        image.EndInit();
        image.Freeze();
        ScreenshotImage.Source = image;
        ScreenshotEmptyText.Visibility = Visibility.Collapsed;
    }

    private void OpenVideoClick(object sender, RoutedEventArgs e)
    {
        var text = VideoUrlBox.Text.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http"))
        {
            MessageBox.Show(Window.GetWindow(this),
                "Введите корректную ссылку, начинающуюся с http:// или https://.",
                "Проверьте ссылку", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            MessageBox.Show(Window.GetWindow(this),
                $"Не удалось открыть ссылку: {exception.Message}",
                "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CollapseClick(object sender, RoutedEventArgs e)
    {
        _saveTimer.Stop();
        SaveJournalEntry();
        if (_row is not null)
        {
            CollapseRequested?.Invoke(_row);
        }
    }

    public void Release()
    {
        _saveTimer.Stop();
        SaveJournalEntry();
    }

    private static SolidColorBrush Brush(string color) =>
        new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
}
