using DnevnikSdelok.Data;
using DnevnikSdelok.Models;
using DnevnikSdelok.Security;
using DnevnikSdelok.Services;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DnevnikSdelok;

public partial class MainWindow : Window
{
    private readonly JournalDatabase _database = new();
    private readonly DispatcherTimer _syncTimer = new() { Interval = TimeSpan.FromMinutes(15) };
    private readonly ObservableCollection<ExecutionRow> _rows = [];
    private ICollectionView? _executionView;
    private List<ApiConnection> _connections = [];
    private PerformanceSummary _summary = new(0, 0, 0, [], [], false);
    private List<PerformancePoint> _chartPoints = [];
    private CancellationTokenSource? _activeSync;
    private bool _isSyncing;
    private bool _isClosing;

    public MainWindow()
    {
        InitializeComponent();
        _syncTimer.Tick += SyncTimerTick;
        StatusText.ToolTip = _database.DatabasePath;
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        ReloadData();
        _syncTimer.Start();
        await SyncAllAsync(automatic: true);
    }

    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        _isClosing = true;
        _syncTimer.Stop();
        _activeSync?.Cancel();
    }

    private async void SyncTimerTick(object? sender, EventArgs e) =>
        await SyncAllAsync(automatic: true);

    private async void RefreshClick(object sender, RoutedEventArgs e) =>
        await SyncAllAsync(automatic: false);

    private async Task SyncAllAsync(bool automatic)
    {
        if (_isSyncing)
        {
            return;
        }

        _connections = _database.GetConnections();
        UpdateConnectionFilter();
        if (_connections.Count == 0)
        {
            SyncStatusValue.Text = "Не подключено";
            LastSyncValue.Text = "Добавьте read-only API-ключ";
            StatusText.Text = "Создайте отдельный ключ Vataga для спота и для фьючерсов.";
            ReloadData();
            return;
        }

        _isSyncing = true;
        RefreshButton.IsEnabled = false;
        _activeSync = new CancellationTokenSource();
        var cancellationToken = _activeSync.Token;
        var successfulConnections = 0;
        var failures = new List<string>();
        SyncStatusValue.Text = "Синхронизация";

        try
        {
            for (var index = 0; index < _connections.Count; index++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var connection = _connections[index];
                var marketName = connection.Market == "spot" ? "спота" : "фьючерсов";
                StatusText.Text = $"Загружаю историю {marketName}: {connection.Name} ({index + 1}/{_connections.Count})";
                try
                {
                    var apiKey = WindowsSecretProtection.Unprotect(connection.ProtectedApiKey);
                    var apiSecret = WindowsSecretProtection.Unprotect(connection.ProtectedApiSecret);
                    using var client = new BinanceApiClient(apiKey, apiSecret, connection.Market);
                    var progress = new Progress<int>(value =>
                    {
                        if (!_isClosing)
                        {
                            SyncStatusValue.Text = $"{value}%";
                            StatusText.Text = connection.Market == "spot"
                                ? $"Проверяю пары Binance Spot: {value}% — первый запуск может занять несколько минут."
                                : $"Загружаю исполнения Binance Futures: {value}%";
                        }
                    });
                    var executions = await client.FetchExecutionsAsync(
                        connection.Id,
                        connection.LastSyncedUtc,
                        _database.SaveExecutions,
                        progress,
                        cancellationToken);
                    var syncTime = DateTime.UtcNow;
                    _database.UpdateConnectionStatus(
                        connection.Id,
                        syncTime,
                        $"Загружено: {executions} исполнений");
                    successfulConnections++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    var error = $"{connection.DisplayName}: {exception.Message}";
                    failures.Add(error);
                    _database.UpdateConnectionStatus(connection.Id, null, $"Ошибка: {exception.Message}");
                }
            }
        }
        finally
        {
            _activeSync.Dispose();
            _activeSync = null;
            _isSyncing = false;
            RefreshButton.IsEnabled = true;
        }

        if (_isClosing)
        {
            return;
        }

        ReloadData();
        if (failures.Count > 0)
        {
            SyncStatusValue.Text = successfulConnections > 0 ? "Частично" : "Ошибка";
            LastSyncValue.Text = $"{failures.Count} подключений требуют внимания";
            StatusText.Text = string.Join(" | ", failures);
            StatusText.ToolTip = StatusText.Text;
            MessageBox.Show(
                this,
                string.Join(Environment.NewLine + Environment.NewLine, failures),
                "Не удалось обновить историю",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        else
        {
            SyncStatusValue.Text = "Актуально";
            var lastSync = _connections
                .Select(item => _database.GetConnection(item.Id)?.LastSyncedUtc)
                .Where(item => item.HasValue)
                .Max();
            LastSyncValue.Text = lastSync.HasValue
                ? lastSync.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm")
                : "Синхронизация завершена";
            StatusText.Text = automatic
                ? "Автообновление каждые 15 минут · данные и заметки хранятся локально."
                : "Синхронизация завершена.";
            StatusText.ToolTip = _database.DatabasePath;
        }
    }

    private void ReloadData()
    {
        _connections = _database.GetConnections();
        UpdateConnectionFilter();
        var names = _connections.ToDictionary(item => item.Id, item => item.DisplayName);
        var executions = _database.GetExecutions();
        _summary = PerformanceCalculator.Calculate(executions, names);
        _rows.Clear();
        foreach (var row in _summary.Rows)
        {
            _rows.Add(row);
        }

        _executionView = CollectionViewSource.GetDefaultView(_rows);
        _executionView.Filter = IsExecutionVisible;
        ExecutionsGrid.ItemsSource = _executionView;
        UpdateDashboard();
        UpdateRemoveButton();
    }

    private void UpdateConnectionFilter()
    {
        if (ConnectionFilter is null)
        {
            return;
        }
        var selectedId = (ConnectionFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        ConnectionFilter.Items.Clear();
        ConnectionFilter.Items.Add(new ComboBoxItem { Content = "Все подключения", Tag = "all" });
        foreach (var connection in _connections)
        {
            ConnectionFilter.Items.Add(new ComboBoxItem
            {
                Content = connection.DisplayName,
                Tag = connection.Id.ToString(CultureInfo.InvariantCulture)
            });
        }
        ConnectionFilter.SelectedItem = ConnectionFilter.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag?.ToString() == selectedId)
            ?? ConnectionFilter.Items[0];
    }

    private bool IsExecutionVisible(object item)
    {
        if (item is not ExecutionRow row)
        {
            return false;
        }

        if (ConnectionFilter.SelectedItem is ComboBoxItem connectionFilter &&
            connectionFilter.Tag?.ToString() is { } connectionTag &&
            connectionTag != "all" &&
            int.TryParse(connectionTag, CultureInfo.InvariantCulture, out var connectionId) &&
            row.ConnectionId != connectionId)
        {
            return false;
        }

        if (PeriodFilter.SelectedItem is ComboBoxItem periodFilter &&
            int.TryParse(periodFilter.Tag?.ToString(), CultureInfo.InvariantCulture, out var days) &&
            row.Execution.ExecutedAtUtc < DateTime.UtcNow.AddDays(-days))
        {
            return false;
        }

        var search = SearchBox?.Text.Trim();
        if (!string.IsNullOrEmpty(search) &&
            !row.Symbol.Contains(search, StringComparison.OrdinalIgnoreCase) &&
            !row.MarketLabel.Contains(search, StringComparison.OrdinalIgnoreCase) &&
            !row.SideLabel.Contains(search, StringComparison.OrdinalIgnoreCase) &&
            !row.ConnectionName.Contains(search, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private void UpdateDashboard()
    {
        var period = GetSelectedPeriod();
        var startUtc = period.HasValue ? DateTime.UtcNow.AddDays(-period.Value) : DateTime.MinValue;
        var selectedConnectionId = ConnectionFilter.SelectedItem is ComboBoxItem selectedConnection &&
                                   int.TryParse(selectedConnection.Tag?.ToString(), CultureInfo.InvariantCulture, out var parsedId)
            ? parsedId
            : (int?)null;
        var accountRows = selectedConnectionId.HasValue
            ? _rows.Where(item => item.ConnectionId == selectedConnectionId.Value).ToList()
            : _rows.ToList();
        var visibleRows = accountRows
            .Where(item => item.Execution.ExecutedAtUtc >= startUtc)
            .Where(item => item.Execution.QuoteAsset == "USDT")
            .ToList();
        var cumulativePnl = visibleRows.Sum(item => item.NetPnl ?? 0);
        PnlValue.Text = $"{cumulativePnl:N2} USDT";
        PnlValue.Foreground = cumulativePnl >= 0
            ? new SolidColorBrush(Color.FromRgb(114, 214, 160))
            : new SolidColorBrush(Color.FromRgb(255, 139, 128));
        ChartEndValue.Text = $"{cumulativePnl:N2} USDT";
        ExecutionCountValue.Text = accountRows.Count(item => item.Execution.ExecutedAtUtc >= startUtc)
            .ToString("N0", CultureInfo.GetCultureInfo("ru-RU"));
        var selectedSummary = selectedConnectionId.HasValue
            ? PerformanceCalculator.Calculate(
                accountRows.Select(item => item.Execution),
                _connections.ToDictionary(item => item.Id, item => item.DisplayName))
            : _summary;
        WinRateValue.Text = selectedSummary.ClosedTrades == 0
            ? "—"
            : $"{selectedSummary.WinningTrades * 100m / selectedSummary.ClosedTrades:N1}%";
        WinRateCaption.Text = selectedSummary.ClosedTrades == 0
            ? "закрытых позиций пока нет"
            : $"по {selectedSummary.ClosedTrades:N0} закрытым позициям · вся история";

        var connectedCount = _connections.Count;
        if (connectedCount == 0)
        {
            SyncStatusValue.Text = "Не подключено";
            LastSyncValue.Text = "Добавьте read-only API-ключ";
        }
        else if (!_isSyncing)
        {
            var latestSync = _connections
                .Select(item => _database.GetConnection(item.Id)?.LastSyncedUtc)
                .Where(item => item.HasValue)
                .Max();
            SyncStatusValue.Text = "Подключено";
            LastSyncValue.Text = latestSync.HasValue
                ? $"Последнее обновление {latestSync.Value.ToLocalTime():dd.MM HH:mm}"
                : $"{connectedCount} подключений · готово к загрузке";
        }

        EmptyState.Visibility = accountRows.Any(item => item.Execution.ExecutedAtUtc >= startUtc)
            ? Visibility.Collapsed
            : Visibility.Visible;
        _chartPoints = _summary.Points
            .Where(item => item.DateUtc >= startUtc && item.NetPnl.HasValue)
            .Where(item => !selectedConnectionId.HasValue || item.ConnectionId == selectedConnectionId.Value)
            .OrderBy(item => item.DateUtc)
            .ToList();
        DrawChart(_chartPoints);
        _executionView?.Refresh();
        UpdateRemoveButton();

        if (selectedSummary.HasUnconvertedFees)
        {
            StatusText.Text =
                "Некоторые комиссии списаны в токенах, кроме базовой/котируемой валюты (например BNB), и не пересчитаны в USDT.";
        }
    }

    private int? GetSelectedPeriod()
    {
        return PeriodFilter.SelectedItem is ComboBoxItem selected &&
               int.TryParse(selected.Tag?.ToString(), CultureInfo.InvariantCulture, out var days)
            ? days
            : null;
    }

    private void DrawChart(IReadOnlyList<PerformancePoint> points)
    {
        if (ProfitCanvas.ActualWidth < 40 || ProfitCanvas.ActualHeight < 40)
        {
            return;
        }

        ProfitCanvas.Children.Clear();
        var width = ProfitCanvas.ActualWidth;
        var height = ProfitCanvas.ActualHeight;
        const double left = 58;
        const double right = 12;
        const double top = 8;
        const double bottom = 22;
        var plotWidth = width - left - right;
        var plotHeight = height - top - bottom;
        var zero = new Line
        {
            X1 = left,
            X2 = left + plotWidth,
            Y1 = top + plotHeight / 2,
            Y2 = top + plotHeight / 2,
            Stroke = new SolidColorBrush(Color.FromRgb(53, 67, 79)),
            StrokeThickness = 1,
            StrokeDashArray = [3, 3]
        };
        ProfitCanvas.Children.Add(zero);

        if (points.Count == 0)
        {
            AddChartLabel("Здесь появится график после синхронизации USDT-сделок", 18, top + plotHeight / 2 - 9);
            return;
        }

        var cumulative = new List<decimal>(points.Count);
        var current = 0m;
        foreach (var point in points)
        {
            current += point.NetPnl!.Value;
            cumulative.Add(current);
        }
        var min = Math.Min(0, cumulative.Min());
        var max = Math.Max(0, cumulative.Max());
        if (max == min)
        {
            max += 1;
            min -= 1;
        }
        var range = max - min;
        for (var index = 0; index <= 4; index++)
        {
            var fraction = index / 4d;
            var y = top + fraction * plotHeight;
            var value = max - (decimal)fraction * range;
            var gridLine = new Line
            {
                X1 = left,
                X2 = left + plotWidth,
                Y1 = y,
                Y2 = y,
                Stroke = new SolidColorBrush(Color.FromRgb(32, 44, 55)),
                StrokeThickness = 1
            };
            ProfitCanvas.Children.Add(gridLine);
            AddChartLabel($"{value:N2}", 0, y - 9, 52);
        }

        var line = new Polyline
        {
            Stroke = new SolidColorBrush(Color.FromRgb(114, 214, 160)),
            StrokeThickness = 2.4,
            StrokeLineJoin = PenLineJoin.Round
        };
        for (var index = 0; index < cumulative.Count; index++)
        {
            var x = left + (cumulative.Count == 1 ? plotWidth : plotWidth * index / (cumulative.Count - 1));
            var fraction = (double)((max - cumulative[index]) / range);
            var y = top + fraction * plotHeight;
            line.Points.Add(new Point(x, y));
        }
        ProfitCanvas.Children.Add(line);
    }

    private void AddChartLabel(string text, double left, double top, double width = 300)
    {
        var label = new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.FromRgb(126, 141, 153)),
            FontSize = 10,
            Width = width,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Canvas.SetLeft(label, left);
        Canvas.SetTop(label, top);
        ProfitCanvas.Children.Add(label);
    }

    private void ChartSizeChanged(object sender, SizeChangedEventArgs e) => DrawChart(_chartPoints);

    private void FilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_executionView is null)
        {
            return;
        }
        _executionView.Refresh();
        UpdateDashboard();
    }

    private void SearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _executionView?.Refresh();
    }

    private async void AddConnectionClick(object sender, RoutedEventArgs e)
    {
        var dialog = new AddConnectionWindow(_database) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            ReloadData();
            await SyncAllAsync(automatic: false);
        }
    }

    private void ExportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Экспорт исполнений в CSV",
            Filter = "CSV-файл (*.csv)|*.csv",
            FileName = $"dnevnik-sdelok-{DateTime.Now:yyyy-MM-dd}.csv",
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            _database.ExportCsv(dialog.FileName);
            StatusText.Text = $"CSV сохранён: {dialog.FileName}";
            MessageBox.Show(this, "История исполнений экспортирована в CSV.", "Экспорт завершён",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Не удалось экспортировать историю: {exception.Message}",
                "Ошибка экспорта", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BackupClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Резервная копия локальной базы",
            Filter = "Файл базы данных SQLite (*.sqlite3)|*.sqlite3",
            FileName = $"dnevnik-sdelok-backup-{DateTime.Now:yyyy-MM-dd-HHmm}.sqlite3",
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            _database.BackupTo(dialog.FileName);
            MessageBox.Show(this,
                "Резервная копия сохранена. Храните её на другом диске или в синхронизируемой папке облака. " +
                "API-ключи зашифрованы для текущего пользователя Windows; на другом компьютере их может потребоваться ввести заново.",
                "Копия готова",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Не удалось создать резервную копию: {exception.Message}",
                "Ошибка резервного копирования", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateRemoveButton()
    {
        RemoveConnectionButton.Visibility =
            ConnectionFilter.SelectedItem is ComboBoxItem item && item.Tag?.ToString() != "all"
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void RemoveConnectionClick(object sender, RoutedEventArgs e)
    {
        if (ConnectionFilter.SelectedItem is not ComboBoxItem item ||
            !int.TryParse(item.Tag?.ToString(), CultureInfo.InvariantCulture, out var connectionId))
        {
            return;
        }

        var connection = _connections.FirstOrDefault(value => value.Id == connectionId);
        if (connection is null)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"Удалить «{connection.DisplayName}» вместе со всеми загруженными из этого подключения исполнениями?",
            "Удалить подключение",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _database.DeleteConnection(connectionId);
        ReloadData();
    }
}