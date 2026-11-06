using DnevnikSdelok.Data;
using DnevnikSdelok.Models;
using DnevnikSdelok.Security;
using DnevnikSdelok.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private CancellationTokenSource? _orderLookupCancellation;
    private bool _initializing;
    private bool _webViewReady;
    private bool _chartPageReady;
    private bool _chartInitializing;
    private bool _intervalInitialized;
    private bool _isLoaded;
    private bool _isPositionOpen;

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
        _orderLookupCancellation?.Cancel();
        _orderLookupCancellation?.Dispose();
        _orderLookupCancellation = new CancellationTokenSource();
        _row = row;
        _relatedExecutions = relatedExecutions;
        _database = database;
        _preferences = preferences;
        IntervalSelector.SelectedIndex = SelectIntervalIndex(row, relatedExecutions);
        _intervalInitialized = true;
        _initializing = true;
        TradeTitle.Text = $"{row.Symbol} · {row.MarketLabel} · {row.SideLabel}";
        var pnl = row.PnlDisplay;
        var percent = row.ProfitPercentDisplay;
        TradeSummary.Text =
            $"{row.ConvertUtcToDisplayTime(row.Execution.ExecutedAtUtc):dd.MM.yyyy HH:mm:ss} · " +
            $"{row.Price.ToString("0.####", CultureInfo.GetCultureInfo("ru-RU"))} {row.Execution.QuoteAsset} · " +
            $"P&L {pnl} ({percent})";
        DescriptionBox.Text = row.JournalDescription;
        ConclusionBox.Text = row.JournalConclusion;
        VideoUrlBox.Text = row.VideoUrl;
        ExecutionsGrid.ItemsSource = relatedExecutions
            .OrderBy(item => item.Execution.ExecutedAtUtc)
            .ThenBy(item => long.TryParse(item.Execution.ExchangeTradeId, out var tradeId) ? tradeId : 0)
            .ToList();
        LoadScreenshot();
        _initializing = false;

        if (_isLoaded)
        {
            _ = EnsureChartAsync();
            _ = LoadOrderTypesAsync(relatedExecutions, _orderLookupCancellation.Token);
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
            if (_orderLookupCancellation is { IsCancellationRequested: false } cancellation)
            {
                _ = LoadOrderTypesAsync(_relatedExecutions, cancellation.Token);
            }
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
        _orderLookupCancellation?.Cancel();
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

        if (message == "toggle-grid")
        {
            if (_preferences is null || _database is null)
            {
                return;
            }

            _preferences.ShowChartGrid = !_preferences.ShowChartGrid;
            try
            {
                _database.SaveDashboardPreferences(_preferences);
                _ = LoadChartAsync();
            }
            catch (Exception exception)
            {
                ChartStatus.Text = $"Не удалось сохранить настройку сетки: {exception.Message}";
            }
            return;
        }

        if (message.StartsWith("chart-loaded:", StringComparison.Ordinal))
        {
            var parts = message.Split(':');
            var interval = (IntervalSelector.SelectedItem as ComboBoxItem)?.Content?.ToString();
            ChartStatus.Text =
                $"{_row?.Symbol} · {interval} · свечей: {parts[3]} · отметок: {parts[2]}" +
                (_isPositionOpen ? " · подключение live-цены…" : "");
            return;
        }

        if (message == "chart-live:connected")
        {
            if (!ChartStatus.Text.Contains("live-цена", StringComparison.Ordinal))
            {
                ChartStatus.Text += " · live-цена";
            }
            return;
        }

        if (message == "chart-live:error")
        {
            ChartStatus.Text += " · ошибка подключения live-цены, повторная попытка…";
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

    private static int SelectIntervalIndex(
        ExecutionRow row,
        IReadOnlyList<ExecutionRow> executions)
    {
        var firstExecution = executions.Count == 0
            ? row.Execution.ExecutedAtUtc
            : executions.Min(item => item.Execution.ExecutedAtUtc);
        var lastExecution = executions.Count == 0
            ? row.Execution.ExecutedAtUtc
            : executions.Max(item => item.Execution.ExecutedAtUtc);
        var duration = Math.Max(
            (lastExecution - firstExecution).TotalSeconds,
            row.TimeInDeal?.TotalSeconds ?? 0);

        return duration switch
        {
            <= 12 => 0,
            <= 60 => 1,
            <= 180 => 2,
            <= 360 => 3,
            <= 720 => 4,
            <= 3600 => 5,
            <= 10800 => 6,
            <= 43200 => 7,
            <= 259200 => 8,
            _ => 9
        };
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

        var priceLines = new List<object>();
        var orderedExecutions = _relatedExecutions
            .OrderBy(item => item.Execution.ExecutedAtUtc)
            .ThenBy(item => long.TryParse(item.Execution.ExchangeTradeId, out var tradeId) ? tradeId : 0)
            .ToList();
        var entries = orderedExecutions.Where(item => item.IsPositionEntry).ToList();
        var exits = orderedExecutions.Where(item => item.IsPositionExit).ToList();
        var averageEntryPrice = WeightedAveragePrice(entries);
        var averageExitPrice = WeightedAveragePrice(exits);
        if (averageEntryPrice.HasValue)
        {
            priceLines.Add(new
            {
                kind = "entry",
                price = (double)averageEntryPrice.Value,
                time = ToUnixSeconds(entries[0].Execution.ExecutedAtUtc)
            });
        }
        if (averageExitPrice.HasValue)
        {
            priceLines.Add(new
            {
                kind = "exit",
                price = (double)averageExitPrice.Value,
                time = ToUnixSeconds(exits[^1].Execution.ExecutedAtUtc)
            });
        }

        var firstEntry = entries.FirstOrDefault();
        var direction = firstEntry is not null &&
                        (firstEntry.Execution.PositionSide == "SHORT" ||
                         firstEntry.Execution.PositionSide != "LONG" &&
                         firstEntry.Execution.Side == "SELL")
            ? -1
            : 1;
        var netPnl = orderedExecutions.Sum(item => item.NetPnl ?? 0);
        var returnPercent = averageEntryPrice is > 0 && averageExitPrice.HasValue
            ? (averageExitPrice.Value - averageEntryPrice.Value) / averageEntryPrice.Value * direction * 100m
            : (decimal?)null;
        var firstEntryId = firstEntry?.Execution.ExchangeTradeId;
        var entrySide = firstEntry?.Execution.Side;
        var signedOpenQuantity = orderedExecutions.Sum(item =>
        {
            var execution = item.Execution;
            var sign = execution.Side == entrySide ? 1m : -1m;
            var quantity = execution.Quantity;
            if (execution.Market == "spot" && execution.CommissionAsset == execution.BaseAsset)
            {
                quantity += execution.Side == "SELL"
                    ? execution.Commission
                    : -execution.Commission;
            }
            return sign * quantity;
        });
        var isFullyClosed = exits.Count > 0 && Math.Abs(signedOpenQuantity) < 0.000000000001m;
        var isOpen = !isFullyClosed;
        _isPositionOpen = isOpen;
        var finalExitId = isFullyClosed ? exits[^1].Execution.ExchangeTradeId : null;
        var chartTimeZone = TimeZoneInfo.FindSystemTimeZoneById(_preferences.TimeZoneId);
        var timeZoneForChart = TimeZoneInfo.TryConvertWindowsIdToIanaId(
            chartTimeZone.Id,
            out var ianaTimeZoneId)
            ? ianaTimeZoneId
            : chartTimeZone.Id;
        var configuration = new
        {
            symbol = _row.Symbol,
            market = _row.Execution.Market,
            interval,
            selectedTime = new DateTimeOffset(
                DateTime.SpecifyKind(_row.Execution.ExecutedAtUtc, DateTimeKind.Utc))
                .ToUnixTimeMilliseconds() / 1000d,
            focusStartTime = orderedExecutions.Count > 0
                ? ToUnixSeconds(orderedExecutions[0].Execution.ExecutedAtUtc)
                : ToUnixSeconds(_row.Execution.ExecutedAtUtc),
            focusEndTime = isOpen
                ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d
                : orderedExecutions.Count > 0
                    ? ToUnixSeconds(orderedExecutions[^1].Execution.ExecutedAtUtc)
                    : ToUnixSeconds(_row.Execution.ExecutedAtUtc),
            isOpen,
            bullColor = _preferences.ChartBullColor,
            bearColor = _preferences.ChartBearColor,
            showGrid = _preferences.ShowChartGrid,
            gridColor = _preferences.ChartGridColor,
            timeZone = timeZoneForChart,
            executions = orderedExecutions.Select(item => new
            {
                time = new DateTimeOffset(
                    DateTime.SpecifyKind(item.Execution.ExecutedAtUtc, DateTimeKind.Utc))
                    .ToUnixTimeMilliseconds() / 1000d,
                price = (double)item.Price,
                side = item.Execution.Side,
                isMainEntry = item.Execution.ExchangeTradeId == firstEntryId,
                isMainExit = item.Execution.ExchangeTradeId == finalExitId
            }),
            positionSummary = new
            {
                percent = returnPercent.HasValue ? (double)returnPercent.Value : (double?)null,
                pnl = (double)netPnl,
                quoteAsset = _row.Execution.QuoteAsset,
                averageEntryPrice = averageEntryPrice.HasValue
                    ? (double)averageEntryPrice.Value
                    : (double?)null,
                averageExitPrice = averageExitPrice.HasValue
                    ? (double)averageExitPrice.Value
                    : (double?)null
            },
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

    private static decimal? WeightedAveragePrice(IReadOnlyCollection<ExecutionRow> executions)
    {
        var totalQuantity = executions.Sum(item => item.Quantity);
        return totalQuantity > 0
            ? executions.Sum(item => item.Price * item.Quantity) / totalQuantity
            : null;
    }

    private static double ToUnixSeconds(DateTime utcTime) =>
        new DateTimeOffset(DateTime.SpecifyKind(utcTime, DateTimeKind.Utc))
            .ToUnixTimeMilliseconds() / 1000d;

    private async Task LoadOrderTypesAsync(
        IReadOnlyList<ExecutionRow> executions,
        CancellationToken cancellationToken)
    {
        var row = _row;
        var database = _database;
        if (row is null || database is null || executions.Count == 0 ||
            executions.All(item => !string.IsNullOrEmpty(item.Execution.OrderType)))
        {
            return;
        }

        try
        {
            var connection = database.GetConnection(row.ConnectionId)
                ?? throw new InvalidOperationException("Подключение API для сделки не найдено.");
            var apiKey = WindowsSecretProtection.Unprotect(connection.ProtectedApiKey);
            var apiSecret = WindowsSecretProtection.Unprotect(connection.ProtectedApiSecret);
            using var client = new BinanceApiClient(apiKey, apiSecret, row.Execution.Market);
            if (executions.Any(item => string.IsNullOrEmpty(item.Execution.ExchangeOrderId)))
            {
                var metadata = await client.GetExecutionOrderMetadataAsync(
                    row.Symbol,
                    executions.Min(item => item.Execution.ExecutedAtUtc),
                    executions.Max(item => item.Execution.ExecutedAtUtc),
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var item in executions)
                {
                    if (!metadata.TryGetValue(item.Execution.ExchangeTradeId, out var itemMetadata))
                    {
                        continue;
                    }

                    item.Execution.ExchangeOrderId = itemMetadata.ExchangeOrderId;
                    item.Execution.IsMaker = itemMetadata.IsMaker;
                    database.UpdateExecutionOrderMetadata(
                        item.ConnectionId,
                        item.Symbol,
                        item.Execution.ExchangeTradeId,
                        itemMetadata.ExchangeOrderId,
                        itemMetadata.IsMaker);
                }
            }

            var unresolvedTrades = executions
                .Where(item => string.IsNullOrEmpty(item.Execution.ExchangeOrderId))
                .Select(item => item.Execution.ExchangeTradeId)
                .ToList();
            if (unresolvedTrades.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Binance не вернула ID ордера для исполнений: {string.Join(", ", unresolvedTrades)}.");
            }

            foreach (var orderGroup in executions
                         .Where(item => string.IsNullOrEmpty(item.Execution.OrderType))
                         .GroupBy(item => item.Execution.ExchangeOrderId, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var orderType = await client.GetOrderTypeAsync(
                    row.Symbol,
                    orderGroup.Key,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var item in orderGroup)
                {
                    item.Execution.OrderType = orderType;
                }
                database.UpdateExecutionOrderType(
                    row.ConnectionId,
                    row.Symbol,
                    orderGroup.Key,
                    orderType);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await Dispatcher.InvokeAsync(() =>
            {
                ExecutionsGrid.Items.Refresh();
                SaveStatus.Text = "Типы ордеров получены с Binance.";
                SaveStatus.Foreground = Brush(_preferences?.AccentColor ?? "#00FF41");
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                SaveStatus.Text = $"Не удалось определить типы ордеров: {exception.Message}";
                SaveStatus.Foreground = Brush("#FF3158");
                ExecutionsGrid.Items.Refresh();
            });
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
            Multiselect = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        try
        {
            foreach (var fileName in dialog.FileNames)
            {
                var copiedPath = _database.CopyTradeImage(fileName);
                _row.ScreenshotRelativePaths.Add(copiedPath);
            }
            _row.ScreenshotRelativePath = _row.ScreenshotRelativePaths.FirstOrDefault() ?? "";
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

        ScreenshotGallery.Children.Clear();
        List<string> relativePaths;
        if (_row.ScreenshotRelativePaths.Count > 0)
        {
            relativePaths = _row.ScreenshotRelativePaths;
        }
        else if (string.IsNullOrWhiteSpace(_row.ScreenshotRelativePath))
        {
            relativePaths = [];
        }
        else
        {
            relativePaths = [_row.ScreenshotRelativePath];
        }
        _row.ScreenshotRelativePaths = relativePaths;
        if (relativePaths.Count == 0)
        {
            ScreenshotGallery.Children.Add(new TextBlock
            {
                Text = "Изображения хранятся локально. Нажмите «Добавить PNG/JPG».",
                Foreground = Brush(_preferences?.MutedForeground ?? "#00AA2B"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            });
            return;
        }

        var unavailableImageFound = false;
        foreach (var relativePath in relativePaths)
        {
            var path = _database.GetTradeImagePath(relativePath);
            if (path is null)
            {
                unavailableImageFound = true;
                continue;
            }

            var thumbnail = new Image
            {
                Source = LoadImage(path, 160),
                Stretch = Stretch.Uniform,
                MaxWidth = 160,
                MaxHeight = 82,
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(4)
            };
            thumbnail.MouseLeftButtonUp += (_, _) => OpenImageViewer(path);
            var frame = new Border
            {
                Width = 170,
                Height = 92,
                Margin = new Thickness(3),
                BorderThickness = new Thickness(1),
                BorderBrush = Brush(_preferences?.ChartGridColor ?? "#064D1B"),
                CornerRadius = new CornerRadius(4),
                Child = thumbnail,
                ToolTip = "Открыть на весь экран · масштаб Ctrl + колесо мыши"
            };
            ScreenshotGallery.Children.Add(frame);
        }

        if (unavailableImageFound)
        {
            ScreenshotGallery.Children.Add(new TextBlock
            {
                Text = "Некоторые изображения недоступны: файл отсутствует или сохранённый путь находится вне папки вложений.",
                Foreground = Brush(_preferences?.MutedForeground ?? "#00AA2B"),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            });
        }
    }

    private static BitmapImage LoadImage(string path, int? decodeHeight)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path, UriKind.Absolute);
        if (decodeHeight.HasValue)
        {
            image.DecodePixelHeight = decodeHeight.Value;
        }
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void OpenImageViewer(string imagePath)
    {
        var image = new Image
        {
            Source = LoadImage(imagePath, null),
            Stretch = Stretch.Uniform,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1)
        };
        var viewer = new Window
        {
            WindowStyle = WindowStyle.None,
            WindowState = WindowState.Maximized,
            Background = Brush("#000000"),
            Content = image,
            Owner = Window.GetWindow(this),
            Cursor = System.Windows.Input.Cursors.Arrow
        };
        viewer.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == System.Windows.Input.Key.Escape)
            {
                viewer.Close();
                args.Handled = true;
            }
        };
        viewer.PreviewMouseWheel += (_, args) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control ||
                image.RenderTransform is not ScaleTransform scale)
            {
                return;
            }

            var factor = args.Delta > 0 ? 1.15 : 1 / 1.15;
            scale.ScaleX = Math.Clamp(scale.ScaleX * factor, 0.1, 12);
            scale.ScaleY = Math.Clamp(scale.ScaleY * factor, 0.1, 12);
            args.Handled = true;
        };
        viewer.ShowDialog();
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

    public void Release()
    {
        _saveTimer.Stop();
        _orderLookupCancellation?.Cancel();
        SaveJournalEntry();
        if (_webViewReady && ChartWebView.CoreWebView2 is { } core)
        {
            try
            {
                core.PostWebMessageAsString("chart-stop-live");
            }
            catch (Exception exception)
            {
                ChartStatus.Text = $"Не удалось остановить live-обновление: {exception.Message}";
            }
        }
    }

    private static SolidColorBrush Brush(string color) =>
        new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
}
