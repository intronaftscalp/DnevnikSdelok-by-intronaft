using DnevnikSdelok.Data;
using DnevnikSdelok.Models;
using DnevnikSdelok.Security;
using DnevnikSdelok.Services;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Runtime.InteropServices;

namespace DnevnikSdelok;

public partial class MainWindow : Window
{
    private readonly JournalDatabase _database;
    private readonly DispatcherTimer _syncTimer = new() { Interval = TimeSpan.FromMinutes(15) };
    private readonly ObservableCollection<ExecutionRow> _rows = [];
    private readonly Dictionary<string, (TextBlock Value, TextBlock Caption)> _statWidgets = [];
    private readonly Dictionary<DataGridColumn, string> _columnIds = [];
    private ICollectionView? _executionView;
    private List<ApiConnection> _connections = [];
    private DashboardPreferences _preferences;
    private PerformanceSummary _summary = new(0, 0, 0, [], [], false);
    private List<PerformancePoint> _chartPoints = [];
    private Canvas? _mainChart;
    private DataGrid? _tradesGrid;
    private Border? _tradesWidgetCard;
    private TradeDetailsPanel? _expandedTradeDetails;
    private TextBlock? _tradesEmptyState;
    private ComboBox? _connectionFilter;
    private ComboBox? _periodFilter;
    private TextBox? _searchBox;
    private CancellationTokenSource? _activeSync;
    private ExecutionRow? _expandedExecution;
    private double? _tradesCardHeightBeforeExpansion;
    private DashboardSettingsWindow? _settingsWindow;
    private Point? _widgetDragStart;
    private bool _isSyncing;
    private bool _isClosing;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int value,
        int valueSize);

    public MainWindow()
        : this(new JournalDatabase())
    {
    }

    public MainWindow(JournalDatabase database)
    {
        _database = database;
        InitializeComponent();
        _preferences = _database.GetDashboardPreferences();
        _database.SaveDashboardPreferences(_preferences);
        _syncTimer.Tick += SyncTimerTick;
        StatusText.ToolTip = _database.DatabasePath;
        SourceInitialized += (_, _) => ApplyDarkTitleBar();
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        ApplyTheme();
        BuildDashboardWidgets();
        HeaderBand.Visibility = _preferences.ShowHeader ? Visibility.Visible : Visibility.Collapsed;
        ReloadData();
        if (Environment.GetCommandLineArgs().Contains("--offline", StringComparer.Ordinal))
        {
            SetSyncStatus("Офлайн", "Синхронизация отключена для проверки интерфейса");
            StatusText.Text = "Офлайн-запуск: соединения и API-ключи не используются.";
            return;
        }
        _syncTimer.Start();
        await SyncAllAsync(automatic: true);
    }

    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        _isClosing = true;
        _syncTimer.Stop();
        _activeSync?.Cancel();
    }

    private void ApplyDarkTitleBar()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var darkMode = 1;
        var result = DwmSetWindowAttribute(handle, 20, ref darkMode, sizeof(int));
        if (result != 0)
        {
            _ = DwmSetWindowAttribute(handle, 19, ref darkMode, sizeof(int));
        }
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
        if (_connections.Count == 0)
        {
            SetSyncStatus("Не подключено", "Добавьте read-only API-ключ");
            StatusText.Text = "Создайте отдельный ключ Vataga для спота и для фьючерсов.";
            return;
        }

        _isSyncing = true;
        _settingsWindow?.SetRefreshing(true);
        _activeSync = new CancellationTokenSource();
        var cancellationToken = _activeSync.Token;
        var successfulConnections = 0;
        var failures = new List<string>();
        var balanceHistoryWarnings = new List<string>();
        SetSyncStatus("Синхронизация", "Подключаемся к Binance");

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
                    var currentBalance = await client.GetCurrentBalanceAsync(cancellationToken);
                    var initialBalanceCapturedAtUtc = DateTime.UtcNow;
                    var initialBalanceSnapshot = new BalanceSnapshot(
                        connection.Id,
                        initialBalanceCapturedAtUtc,
                        currentBalance);
                    if (connection.Market == "spot")
                    {
                        _database.SaveBalanceSnapshots([initialBalanceSnapshot]);
                    }
                    _database.UpdateConnectionStatus(
                        connection.Id,
                        null,
                        "Баланс обновлён",
                        currentBalance);
                    var progress = new Progress<int>(value =>
                    {
                        if (!_isClosing)
                        {
                            SetSyncStatus($"{value}%", connection.DisplayName);
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
                    var balanceCapturedAtUtc = initialBalanceCapturedAtUtc;
                    var finalBalanceSnapshot = initialBalanceSnapshot;
                    var hasFinalBalanceSnapshot = false;
                    try
                    {
                        currentBalance = await client.GetCurrentBalanceAsync(cancellationToken);
                        balanceCapturedAtUtc = DateTime.UtcNow;
                        finalBalanceSnapshot = new BalanceSnapshot(
                            connection.Id,
                            balanceCapturedAtUtc,
                            currentBalance);
                        hasFinalBalanceSnapshot = true;
                        if (connection.Market == "spot")
                        {
                            _database.SaveBalanceSnapshots([finalBalanceSnapshot]);
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        balanceHistoryWarnings.Add(
                            $"{connection.DisplayName}: не удалось снять баланс после синхронизации — {exception.Message}");
                    }
                    try
                    {
                        var historyFetchedThroughUtc =
                            _database.GetBalanceHistoryFetchedThroughUtc(connection.Id);
                        var shouldRefreshHistory = connection.Market == "futures" ||
                                                   !historyFetchedThroughUtc.HasValue ||
                                                   balanceCapturedAtUtc - historyFetchedThroughUtc.Value >=
                                                   TimeSpan.FromHours(20);
                        if (shouldRefreshHistory && connection.Market == "spot")
                        {
                            var historyFromUtc = historyFetchedThroughUtc.HasValue
                                ? Max(
                                    DateTime.UtcNow.AddDays(-30),
                                    historyFetchedThroughUtc.Value.AddDays(-2))
                                : DateTime.UtcNow.AddDays(-30);
                            var snapshots = await client.GetSpotBalanceSnapshotsAsync(
                                connection.Id,
                                historyFromUtc,
                                balanceCapturedAtUtc,
                                cancellationToken);
                            _database.SaveBalanceSnapshots(snapshots);
                            _database.UpdateBalanceHistoryFetchedThroughUtc(
                                connection.Id,
                                balanceCapturedAtUtc);
                        }
                        else if (shouldRefreshHistory)
                        {
                            var historyFromUtc = historyFetchedThroughUtc.HasValue
                                ? Max(
                                    DateTime.UtcNow.AddDays(-90),
                                    historyFetchedThroughUtc.Value.AddHours(-1))
                                : DateTime.UtcNow.AddDays(-90);
                            var changes = await client.GetFuturesBalanceChangesAsync(
                                connection.Id,
                                historyFromUtc,
                                balanceCapturedAtUtc,
                                cancellationToken);
                            _database.SaveBalanceChanges(changes);
                            _database.SaveBalanceSnapshots(
                                hasFinalBalanceSnapshot
                                    ? [initialBalanceSnapshot, finalBalanceSnapshot]
                                    : [initialBalanceSnapshot]);
                            _database.UpdateBalanceHistoryFetchedThroughUtc(
                                connection.Id,
                                balanceCapturedAtUtc);
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        balanceHistoryWarnings.Add(
                            $"{connection.DisplayName}: не удалось получить историю депозита — {exception.Message}");
                    }
                    _database.UpdateConnectionStatus(
                        connection.Id,
                        DateTime.UtcNow,
                        $"Загружено: {executions} исполнений",
                        currentBalance);
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
            if (!_isClosing)
            {
                _settingsWindow?.SetRefreshing(false);
            }
        }

        if (_isClosing)
        {
            return;
        }

        ReloadData();
        if (failures.Count > 0 || balanceHistoryWarnings.Count > 0)
        {
            var warnings = failures.Concat(balanceHistoryWarnings).ToList();
            SetSyncStatus(
                failures.Count == 0 ? "История неполная" : successfulConnections > 0 ? "Частично" : "Ошибка",
                $"{failures.Count} ошибок синхронизации, {balanceHistoryWarnings.Count} предупреждений по депозиту");
            StatusText.Text = string.Join(" | ", warnings);
            StatusText.ToolTip = StatusText.Text;
            if (!automatic)
            {
                MessageBox.Show(
                    this,
                    string.Join(Environment.NewLine + Environment.NewLine, warnings),
                    failures.Count > 0 ? "Обновление завершено с ошибками" : "История депозита загружена не полностью",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        else
        {
            var lastSync = _connections
                .Select(item => _database.GetConnection(item.Id)?.LastSyncedUtc)
                .Where(item => item.HasValue)
                .Max();
            SetSyncStatus("Актуально", lastSync.HasValue
                ? lastSync.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm")
                : "Синхронизация завершена");
            StatusText.Text = automatic
                ? "Автообновление каждые 15 минут · данные и настройки хранятся локально."
                : "Синхронизация завершена.";
            StatusText.ToolTip = _database.DatabasePath;
        }
    }

    private void SetSyncStatus(string value, string caption)
    {
        if (_statWidgets.TryGetValue("sync", out var widget))
        {
            widget.Value.Text = value;
            widget.Caption.Text = caption;
        }
    }

    private void ReloadData()
    {
        CollapseExpandedTrade();
        _connections = _database.GetConnections();
        var names = _connections.ToDictionary(item => item.Id, item => item.DisplayName);
        var executions = _database.GetExecutions();
        var balanceSnapshots = _connections
            .SelectMany(item => _database.GetBalanceSnapshots(item.Id))
            .ToList();
        var balanceChanges = _connections
            .SelectMany(item => _database.GetBalanceChanges(item.Id))
            .ToList();
        var balanceAtEntry = DepositExposureCalculator.Calculate(
            executions,
            _connections,
            balanceSnapshots,
            balanceChanges);
        _summary = PerformanceCalculator.Calculate(executions, names, balanceAtEntry);
        var notes = _database.GetExecutionNotes();
        _expandedExecution = null;
        _rows.Clear();
        foreach (var row in _summary.Rows)
        {
            if (notes.TryGetValue(
                    (row.ConnectionId, row.Symbol, row.Execution.ExchangeTradeId),
                    out var note))
            {
                row.Category = note.Category;
                row.EntryReason = note.EntryReason;
                row.ExitReason = note.ExitReason;
                row.AdditionalInfo = note.AdditionalInfo;
                row.CryptoScreener = note.CryptoScreener;
                row.Rating = note.Rating;
                row.JournalDescription = note.JournalDescription;
                row.JournalConclusion = note.JournalConclusion;
                row.VideoUrl = note.VideoUrl;
                row.ScreenshotRelativePath = note.ScreenshotRelativePath;
                row.ChartDrawingsJson = note.ChartDrawingsJson;
            }
            _rows.Add(row);
        }

        if (_tradesGrid is not null)
        {
            _executionView = CollectionViewSource.GetDefaultView(_rows);
            _executionView.Filter = IsExecutionVisible;
            _tradesGrid.ItemsSource = _executionView;
        }
        UpdateDashboardValues();
        UpdateRemoveButton();
    }

    private void BuildDashboardWidgets()
    {
        _tradesWidgetCard = null;
        _expandedTradeDetails = null;
        _tradesCardHeightBeforeExpansion = null;
        DashboardWidgets.Children.Clear();
        _statWidgets.Clear();
        _mainChart = null;
        _tradesGrid = null;
        _tradesEmptyState = null;
        _columnIds.Clear();
        _connectionFilter = null;
        _periodFilter = null;
        _searchBox = null;

        var availableWidth = Math.Max(900, DashboardScrollViewer.ActualWidth);
        foreach (var widget in _preferences.Widgets
                     .Where(item => item.Visible)
                     .OrderBy(item => item.OrderIndex))
        {
            var card = CreateWidgetCard(widget, availableWidth);
            DashboardWidgets.Children.Add(card);
        }

        if (_tradesGrid is not null)
        {
            _executionView = CollectionViewSource.GetDefaultView(_rows);
            _executionView.Filter = IsExecutionVisible;
            _tradesGrid.ItemsSource = _executionView;
        }
        UpdateDashboardValues();
    }

    private Border CreateWidgetCard(WidgetPreference widget, double availableWidth)
    {
        var minimumWidth = widget.Id == "trades" ? 980 : 240;
        var maximumWidth = Math.Max(minimumWidth, availableWidth - 24);
        var width = Math.Min(widget.CustomWidth ?? WidgetWidth(widget.Width, availableWidth), maximumWidth);
        var contentHeight = widget.CustomHeight is > 0
            ? Math.Clamp(widget.CustomHeight.Value - 48, 100, 1150)
            : widget.Height switch
        {
            "medium" => 210,
            "large" => widget.Id == "trades" ? 470 : 330,
            _ => 120
        };
        var cardHeight = widget.CustomHeight ?? contentHeight + 48;
        var card = new Border
        {
            Width = width,
            MinWidth = minimumWidth,
            Height = cardHeight,
            Margin = new Thickness(0, 0, 12, 12),
            Padding = new Thickness(15, 11, 15, 14),
            Background = Brush(widget.Background),
            BorderBrush = Brush(_preferences.ChartGridColor),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Tag = widget.Id,
            AllowDrop = true
        };
        card.Drop += WidgetDrop;
        card.DragOver += DashboardDragOver;
        if (widget.Id == "trades")
        {
            _tradesWidgetCard = card;
            card.SizeChanged += (_, _) => UpdateExpandedDetailsWidth();
        }

        var panel = new DockPanel();
        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 10) };
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        var moveEarlierButton = new Button
        {
            Content = "↑",
            Padding = new Thickness(5, 2, 5, 2),
            Width = 27,
            Height = 26,
            Margin = new Thickness(0, 0, 4, 0),
            ToolTip = "Переместить виджет раньше",
            Background = Brush(widget.Background),
            Foreground = Brush(widget.Foreground),
            BorderBrush = Brush(_preferences.ChartGridColor),
            IsEnabled = !widget.Pinned
        };
        moveEarlierButton.Click += (_, _) => MoveWidget(widget, -1);
        var moveLaterButton = new Button
        {
            Content = "↓",
            Padding = new Thickness(5, 2, 5, 2),
            Width = 27,
            Height = 26,
            Margin = new Thickness(0, 0, 7, 0),
            ToolTip = "Переместить виджет позже",
            Background = Brush(widget.Background),
            Foreground = Brush(widget.Foreground),
            BorderBrush = Brush(_preferences.ChartGridColor),
            IsEnabled = !widget.Pinned
        };
        moveLaterButton.Click += (_, _) => MoveWidget(widget, 1);
        tools.Children.Add(moveEarlierButton);
        tools.Children.Add(moveLaterButton);
        var magnetButton = new Button
        {
            Content = widget.Magnetized ? "Сетка ✓" : "Сетка",
            Padding = new Thickness(7, 2, 7, 2),
            MinWidth = 55,
            Height = 26,
            ToolTip = "Привязка размера к сетке",
            Background = Brush(widget.Background),
            Foreground = Brush(widget.Foreground),
            BorderBrush = Brush(_preferences.ChartGridColor)
        };
        magnetButton.Click += (_, _) => UpdateWidget(widget, item => item.Magnetized = !item.Magnetized);
        var pinButton = new Button
        {
            Content = widget.Pinned ? "Закреплено" : "Закрепить",
            Padding = new Thickness(7, 2, 7, 2),
            MinWidth = 72,
            Height = 26,
            Margin = new Thickness(0, 0, 5, 0),
            ToolTip = "Закреплённый виджет нельзя перетаскивать",
            Background = Brush(widget.Background),
            Foreground = widget.Pinned ? Brush(widget.Accent) : Brush(widget.Foreground),
            BorderBrush = Brush(_preferences.ChartGridColor)
        };
        pinButton.Click += (_, _) => UpdateWidget(widget, item => item.Pinned = !item.Pinned);
        tools.Children.Add(magnetButton);
        tools.Children.Add(pinButton);
        DockPanel.SetDock(tools, Dock.Right);
        header.Children.Add(tools);

        var dragHandle = new Button
        {
            Content = widget.Pinned ? "●" : "⠿",
            Padding = new Thickness(5, 2, 8, 2),
            Height = 26,
            Tag = widget.Id,
            IsEnabled = !widget.Pinned,
            ToolTip = widget.Pinned ? "Сначала открепите виджет" : "Перетащите, чтобы расположить виджет",
            Background = Brushes.Transparent,
            Foreground = Brush(_preferences.MutedForeground),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.SizeAll
        };
        dragHandle.PreviewMouseLeftButtonDown += (_, args) =>
        {
            if (!widget.Pinned)
            {
                _widgetDragStart = args.GetPosition(DashboardWidgets);
            }
        };
        dragHandle.PreviewMouseLeftButtonUp += (_, _) => _widgetDragStart = null;
        dragHandle.PreviewMouseMove += DragHandleMouseMove;
        header.Children.Add(dragHandle);
        header.Children.Add(new TextBlock
        {
            Text = widget.Title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(widget.Foreground),
            VerticalAlignment = VerticalAlignment.Center
        });
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);
        panel.Children.Add(CreateWidgetContent(widget, contentHeight));
        var cardLayout = new Grid();
        cardLayout.Children.Add(panel);
        var resizeGrip = new Thumb
        {
            Width = 18,
            Height = 18,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Cursor = Cursors.SizeNWSE,
            Background = Brushes.Transparent,
            ToolTip = "Потяните за угол, чтобы изменить размер"
        };
        resizeGrip.DragDelta += (_, args) =>
        {
            var minimumWidgetWidth = widget.Id == "trades" ? 840 : 240;
            var maximumWidgetWidth = Math.Max(minimumWidgetWidth, DashboardScrollViewer.ActualWidth - 24);
            var newWidth = Math.Clamp(
                card.Width + args.HorizontalChange,
                minimumWidgetWidth,
                maximumWidgetWidth);
            var newHeight = Math.Clamp(card.Height + args.VerticalChange, 160, 1400);
            if (widget.Magnetized)
            {
                newWidth = Math.Round(newWidth / 40) * 40;
                newHeight = Math.Round(newHeight / 40) * 40;
            }
            card.Width = newWidth;
            card.Height = newHeight;
        };
        resizeGrip.DragCompleted += (_, _) =>
        {
            widget.CustomWidth = card.Width;
            widget.CustomHeight = card.Height;
            try
            {
                _database.SaveDashboardPreferences(_preferences);
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, $"Не удалось сохранить размер виджета: {exception.Message}",
                    "Ошибка настроек", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
        cardLayout.Children.Add(resizeGrip);
        card.Child = cardLayout;
        return card;
    }

    private static double WidgetWidth(string size, double availableWidth) => size switch
    {
        "small" => 290,
        "medium" => 450,
        "wide" => Math.Max(580, Math.Min(availableWidth - 24, 950)),
        "full" => Math.Max(600, availableWidth - 24),
        _ => 350
    };

    private FrameworkElement CreateWidgetContent(WidgetPreference widget, double height)
    {
        return widget.Id switch
        {
            "pnl" => CreateStatWidget(widget, "0,00 USDT", "Реализованная прибыль после известных комиссий", out var pnlRefs)
                .Also(_ => _statWidgets["pnl"] = pnlRefs),
            "winrate" => CreateStatWidget(widget, "—", "По закрытым позициям", out var winRefs)
                .Also(_ => _statWidgets["winrate"] = winRefs),
            "count" => CreateStatWidget(widget, "0", "Исполнений за выбранный период", out var countRefs)
                .Also(_ => _statWidgets["count"] = countRefs),
            "sync" => CreateStatWidget(widget, "Ожидание", "Последнее обновление", out var syncRefs)
                .Also(_ => _statWidgets["sync"] = syncRefs),
            "chart" => CreateChartWidget(widget, height),
            "trades" => CreateTradesWidget(widget, height),
            _ => new TextBlock { Text = "Неизвестный виджет", Foreground = Brush(widget.Foreground) }
        };
    }

    private static FrameworkElement CreateStatWidget(
        WidgetPreference widget,
        string value,
        string caption,
        out (TextBlock Value, TextBlock Caption) refs)
    {
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var valueText = new TextBlock
        {
            Text = value,
            FontSize = 27,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(widget.Accent),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var captionText = new TextBlock
        {
            Text = caption,
            FontSize = 11,
            Foreground = Brush(widget.Foreground),
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        };
        panel.Children.Add(valueText);
        panel.Children.Add(captionText);
        refs = (valueText, captionText);
        return panel;
    }

    private FrameworkElement CreateChartWidget(WidgetPreference widget, double height)
    {
        _mainChart = new Canvas { ClipToBounds = true };
        _mainChart.SizeChanged += (_, _) => DrawChart(_mainChart, _chartPoints, widget.Accent);
        return _mainChart;
    }

    private FrameworkElement CreateTradesWidget(WidgetPreference widget, double height)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var filters = new StackPanel { Orientation = Orientation.Horizontal };
        _searchBox = new TextBox
        {
            Width = 190,
            Height = 34,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "Поиск по паре, рынку или направлению"
        };
        _searchBox.TextChanged += (_, _) => _executionView?.Refresh();
        filters.Children.Add(_searchBox);
        _connectionFilter = new ComboBox
        {
            MinWidth = 155,
            Height = 34,
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "Фильтр по аккаунту"
        };
        _connectionFilter.SelectionChanged += FilterChanged;
        filters.Children.Add(_connectionFilter);
        _periodFilter = new ComboBox { MinWidth = 120, Height = 34, ToolTip = "Период журнала" };
        _periodFilter.Items.Add(new ComboBoxItem { Content = "30 дней", Tag = "30" });
        _periodFilter.Items.Add(new ComboBoxItem { Content = "7 дней", Tag = "7" });
        _periodFilter.Items.Add(new ComboBoxItem { Content = "90 дней", Tag = "90" });
        _periodFilter.Items.Add(new ComboBoxItem { Content = "Всё время", Tag = "all" });
        _periodFilter.SelectedIndex = 0;
        _periodFilter.SelectionChanged += FilterChanged;
        filters.Children.Add(_periodFilter);
        toolbar.Children.Add(filters);
        Grid.SetRow(toolbar, 0);
        grid.Children.Add(toolbar);

        var tableHost = new Grid();
        _tradesGrid = new DataGrid
        {
            AlternationCount = 2,
            Background = Brush(widget.Background),
            Foreground = Brush(widget.Foreground),
            ColumnHeaderStyle = CreateColumnHeaderStyle(widget),
            RowBackground = Brush(widget.Background),
            AlternatingRowBackground = Brush(Blend(widget.Background, widget.Foreground, 0.035)),
            IsReadOnly = false,
            RowDetailsVisibilityMode = DataGridRowDetailsVisibilityMode.Collapsed,
            ItemsSource = _rows
        };
        foreach (var preference in _preferences.Columns.Where(item => item.Visible).OrderBy(item => item.Order))
        {
            var column = CreateColumn(preference);
            if (column is null)
            {
                continue;
            }
            _tradesGrid.Columns.Add(column);
        }
        _tradesGrid.ColumnReordered += (_, _) => PersistColumnLayout();
        _tradesGrid.PreviewMouseLeftButtonUp += TradesGridPreviewMouseLeftButtonUp;
        _tradesGrid.CellEditEnding += TradesGridCellEditEnding;
        _tradesGrid.SelectionChanged += (_, _) => UpdateRemoveButton();
        tableHost.Children.Add(_tradesGrid);

        var empty = new TextBlock
        {
            Text = "Пока нет сделок",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(widget.Foreground),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        _tradesEmptyState = empty;
        tableHost.Children.Add(empty);
        Grid.SetRow(tableHost, 1);
        grid.Children.Add(tableHost);
        _expandedTradeDetails = new TradeDetailsPanel
        {
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 8, 0, 0)
        };
        _expandedTradeDetails.CollapseRequested += CollapseTradeDetails;
        Grid.SetRow(_expandedTradeDetails, 2);
        grid.Children.Add(_expandedTradeDetails);
        ConfigureConnectionFilter();
        return grid;
    }

    private Style CreateColumnHeaderStyle(WidgetPreference widget)
    {
        var style = new Style(typeof(DataGridColumnHeader));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brush(Blend(widget.Background, widget.Foreground, 0.07))));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Brush(widget.Foreground)));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Brush(_preferences.ChartGridColor)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(9, 5, 9, 5)));
        style.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        return style;
    }

    private DataGridColumn? CreateColumn(ColumnPreference preference)
    {
        var definition = TradeColumnDefinition.All.FirstOrDefault(item => item.Id == preference.Id);
        if (definition is null)
        {
            return null;
        }
        if (preference.Id == "ticker")
        {
            var ticker = new FrameworkElementFactory(typeof(Button));
            ticker.SetBinding(ContentControl.ContentProperty, new Binding(nameof(ExecutionRow.Symbol)));
            ticker.SetValue(Control.BackgroundProperty, Brushes.Transparent);
            ticker.SetValue(Control.BorderThicknessProperty, new Thickness(0));
            ticker.SetValue(Control.PaddingProperty, new Thickness(0));
            ticker.SetValue(Control.ForegroundProperty, Brush(_preferences.AccentColor));
            ticker.SetValue(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left);
            ticker.SetValue(Control.CursorProperty, Cursors.Hand);
            ticker.AddHandler(Button.ClickEvent, new RoutedEventHandler((sender, _) =>
            {
                if (sender is Button { DataContext: ExecutionRow row })
                {
                    OpenSymbolChart(row);
                }
            }));
            var templateColumn = new DataGridTemplateColumn
            {
                Header = definition.Title,
                SortMemberPath = nameof(ExecutionRow.Symbol),
                Width = new DataGridLength(preference.Width),
                CellTemplate = new DataTemplate { VisualTree = ticker }
            };
            _columnIds[templateColumn] = preference.Id;
            return templateColumn;
        }

        var property = preference.Id switch
        {
            "category" => nameof(ExecutionRow.Category),
            "apiKey" => nameof(ExecutionRow.ConnectionName),
            "entryReason" => nameof(ExecutionRow.EntryReason),
            "exitReason" => nameof(ExecutionRow.ExitReason),
            "additionalInfo" => nameof(ExecutionRow.AdditionalInfo),
            "cryptoScreener" => nameof(ExecutionRow.CryptoScreener),
            "execution" => nameof(ExecutionRow.ExecutionLabel),
            "entry" => nameof(ExecutionRow.EntryDisplay),
            "depositPercent" => nameof(ExecutionRow.LeverageDisplay),
            "mpp" or "mpu" or "maxVolume" => nameof(ExecutionRow.ExcursionDisplay),
            "entryUsd" => nameof(ExecutionRow.EntryUsdDisplay),
            "exit" => nameof(ExecutionRow.ExitDisplay),
            "exitUsd" => nameof(ExecutionRow.ExitUsdDisplay),
            "inDeal" => nameof(ExecutionRow.InDealDisplay),
            "side" => nameof(ExecutionRow.SideLabel),
            "profitPercent" or "percent" => nameof(ExecutionRow.ProfitPercentDisplay),
            "incomeUsd" or "profitUsd" => nameof(ExecutionRow.PnlDisplay),
            "volume" or "turnover" => nameof(ExecutionRow.VolumeDisplay),
            "commission" => nameof(ExecutionRow.Commission),
            "rating" => nameof(ExecutionRow.Rating),
            "funding" => nameof(ExecutionRow.FundingDisplay),
            _ => nameof(ExecutionRow.Symbol)
        };
        var sortProperty = preference.Id switch
        {
            "entry" => nameof(ExecutionRow.EntryTimeUtc),
            "depositPercent" => nameof(ExecutionRow.ExposureMultiple),
            "inDeal" => nameof(ExecutionRow.TimeInDeal),
            _ => property
        };
        var column = new DataGridTextColumn
        {
            Header = definition.Title,
            Binding = new Binding(property)
            {
                StringFormat = preference.Id == "commission" ? "{}{0:0.####}" : null,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            },
            SortMemberPath = sortProperty,
            IsReadOnly = preference.Id is not (
                "category" or "entryReason" or "exitReason" or
                "additionalInfo" or "cryptoScreener" or "rating"),
            Width = new DataGridLength(preference.Width)
        };
        _columnIds[column] = preference.Id;
        return column;
    }

    private void TradesGridCellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.Row.Item is not ExecutionRow row)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                _database.SaveExecutionNote(row);
                StatusText.Text = $"Заметка к {row.Symbol} сохранена локально.";
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, $"Не удалось сохранить заметку к сделке: {exception.Message}",
                    "Ошибка сохранения", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }, DispatcherPriority.Background);
    }

    private void ConfigureConnectionFilter()
    {
        if (_connectionFilter is null)
        {
            return;
        }
        var selectedTag = (_connectionFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        _connectionFilter.Items.Clear();
        _connectionFilter.Items.Add(new ComboBoxItem { Content = "Все подключения", Tag = "all" });
        foreach (var connection in _connections)
        {
            _connectionFilter.Items.Add(new ComboBoxItem
            {
                Content = connection.DisplayName,
                Tag = connection.Id.ToString(CultureInfo.InvariantCulture)
            });
        }
        _connectionFilter.SelectedItem = _connectionFilter.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag?.ToString() == selectedTag)
            ?? _connectionFilter.Items[0];
    }

    private void PersistColumnLayout()
    {
        if (_tradesGrid is null || _preferences is null)
        {
            return;
        }
        var visible = _tradesGrid.Columns
            .OrderBy(item => item.DisplayIndex)
            .Where(_columnIds.ContainsKey)
            .ToList();
        for (var index = 0; index < visible.Count; index++)
        {
            var column = visible[index];
            var preference = _preferences.Columns.First(item => item.Id == _columnIds[column]);
            preference.Order = index;
            preference.Width = Math.Clamp(column.ActualWidth, 50, 600);
        }
        var nextOrder = visible.Count;
        foreach (var preference in _preferences.Columns.Where(item => !item.Visible).OrderBy(item => item.Order))
        {
            preference.Order = nextOrder++;
        }
        _database.SaveDashboardPreferences(_preferences);
    }

    private void UpdateWidget(WidgetPreference widget, Action<WidgetPreference> update)
    {
        update(widget);
        SaveAndApplyPreferences(_preferences);
    }

    private bool SaveAndApplyPreferences(DashboardPreferences preferences)
    {
        try
        {
            preferences.EnsureCatalog();
            _database.SaveDashboardPreferences(preferences);
            _preferences = preferences;
            ApplyTheme();
            BuildDashboardWidgets();
            ReloadData();
            return true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Не удалось сохранить настройки интерфейса: {exception.Message}",
                "Ошибка настроек", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void ApplyTheme()
    {
        Background = Brush(_preferences.WindowBackground);
        Foreground = Brush(_preferences.Foreground);
        HeaderBand.Visibility = _preferences.ShowHeader ? Visibility.Visible : Visibility.Collapsed;
        HeaderBand.Background = Brush(Blend(_preferences.WindowBackground, _preferences.Foreground, 0.035));
        DashboardScrollViewer.Background = Brush(_preferences.WindowBackground);
        Resources["PanelBrush"] = Brush("#" + _preferences.WindowBackground.TrimStart('#'));
        Resources["BorderBrush"] = Brush(_preferences.ChartGridColor);
        Resources["MutedBrush"] = Brush(_preferences.MutedForeground);
        Resources["AccentBrush"] = Brush(_preferences.AccentColor);
        Resources["ForegroundBrush"] = Brush(_preferences.Foreground);
    }

    private void UpdateDashboardValues()
    {
        var startUtc = DateTime.MinValue;
        var period = GetSelectedPeriod();
        if (period.HasValue)
        {
            startUtc = DateTime.UtcNow.AddDays(-period.Value);
        }
        var selectedConnectionId = _connectionFilter?.SelectedItem is ComboBoxItem selectedConnection &&
                                   int.TryParse(selectedConnection.Tag?.ToString(), CultureInfo.InvariantCulture, out var parsedId)
            ? parsedId
            : (int?)null;
        var rows = _rows.Where(item => item.Execution.ExecutedAtUtc >= startUtc)
            .Where(item => !selectedConnectionId.HasValue || item.ConnectionId == selectedConnectionId.Value)
            .ToList();
        var usdtRows = rows.Where(item => item.Execution.QuoteAsset == "USDT").ToList();
        var pnl = usdtRows.Sum(item => item.NetPnl ?? 0);

        if (_statWidgets.TryGetValue("pnl", out var pnlWidget))
        {
            pnlWidget.Value.Text = $"{pnl:N2} USDT";
            pnlWidget.Caption.Text = "Реализованный P&L после известных комиссий";
        }
        if (_statWidgets.TryGetValue("count", out var countWidget))
        {
            countWidget.Value.Text = rows.Count.ToString("N0", CultureInfo.GetCultureInfo("ru-RU"));
            countWidget.Caption.Text = period.HasValue ? $"Исполнений за {period} дней" : "Исполнений за всё время";
        }
        if (_statWidgets.TryGetValue("winrate", out var winWidget))
        {
            var names = _connections.ToDictionary(item => item.Id, item => item.DisplayName);
            var summary = PerformanceCalculator.Calculate(rows.Select(item => item.Execution), names);
            winWidget.Value.Text = summary.ClosedTrades == 0
                ? "—"
                : $"{summary.WinningTrades * 100m / summary.ClosedTrades:N1}%";
            winWidget.Caption.Text = summary.ClosedTrades == 0
                ? "Закрытых позиций пока нет"
                : $"По {summary.ClosedTrades:N0} закрытым позициям";
        }
        if (_statWidgets.TryGetValue("sync", out var syncWidget) && !_isSyncing)
        {
            var latestSync = _connections
                .Select(item => _database.GetConnection(item.Id)?.LastSyncedUtc)
                .Where(item => item.HasValue)
                .Max();
            syncWidget.Value.Text = _connections.Count == 0 ? "Не подключено" : "Подключено";
            syncWidget.Caption.Text = latestSync.HasValue
                ? $"Последнее обновление {latestSync.Value.ToLocalTime():dd.MM HH:mm}"
                : "Добавьте read-only API-ключ";
        }

        var connectionIsSelected = selectedConnectionId.HasValue;
        RemoveConnectionButton.Visibility = connectionIsSelected ? Visibility.Visible : Visibility.Collapsed;
        _chartPoints = _summary.Points
            .Where(item => item.DateUtc >= startUtc && item.NetPnl.HasValue)
            .Where(item => !selectedConnectionId.HasValue || item.ConnectionId == selectedConnectionId.Value)
            .OrderBy(item => item.DateUtc)
            .ToList();
        if (_mainChart is not null)
        {
            DrawChart(_mainChart, _chartPoints,
                _preferences.Widgets.First(item => item.Id == "chart").Accent);
        }
        if (_executionView is not null)
        {
            _executionView.Refresh();
        }
        if (_summary.HasUnconvertedFees)
        {
            StatusText.Text =
                "Часть комиссий списана в токенах, например BNB, и не пересчитана в USDT.";
        }
        if (_tradesEmptyState is not null)
        {
            _tradesEmptyState.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private int? GetSelectedPeriod()
    {
        return _periodFilter?.SelectedItem is ComboBoxItem selected &&
               int.TryParse(selected.Tag?.ToString(), CultureInfo.InvariantCulture, out var days)
            ? days
            : null;
    }

    private void DrawChart(Canvas canvas, IReadOnlyList<PerformancePoint> points, string lineColor)
    {
        if (canvas.ActualWidth < 40 || canvas.ActualHeight < 40)
        {
            return;
        }

        canvas.Children.Clear();
        var width = canvas.ActualWidth;
        var height = canvas.ActualHeight;
        const double left = 64;
        const double right = 12;
        const double top = 10;
        const double bottom = 22;
        var plotWidth = width - left - right;
        var plotHeight = height - top - bottom;
        var zero = new Line
        {
            X1 = left,
            X2 = left + plotWidth,
            Y1 = top + plotHeight / 2,
            Y2 = top + plotHeight / 2,
            Stroke = Brush(_preferences.ChartGridColor),
            StrokeThickness = 1,
            StrokeDashArray = [3, 3]
        };
        canvas.Children.Add(zero);

        if (points.Count == 0)
        {
            AddChartLabel(canvas, "После синхронизации USDT-сделок здесь появится график",
                left, top + plotHeight / 2 - 9, Math.Max(260, plotWidth - 30));
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
            canvas.Children.Add(new Line
            {
                X1 = left,
                X2 = left + plotWidth,
                Y1 = y,
                Y2 = y,
                Stroke = Brush(_preferences.ChartGridColor),
                StrokeThickness = 1
            });
            AddChartLabel(canvas, $"{value:N2}", 0, y - 9, 58);
        }

        var line = new Polyline
        {
            Stroke = Brush(lineColor),
            StrokeThickness = 2.4,
            StrokeLineJoin = PenLineJoin.Round
        };
        for (var index = 0; index < cumulative.Count; index++)
        {
            var x = left + (cumulative.Count == 1 ? plotWidth : plotWidth * index / (cumulative.Count - 1));
            var fraction = (double)((max - cumulative[index]) / range);
            line.Points.Add(new Point(x, top + fraction * plotHeight));
        }
        canvas.Children.Add(line);
    }

    private void AddChartLabel(Canvas canvas, string text, double left, double top, double width)
    {
        var label = new TextBlock
        {
            Text = text,
            Foreground = Brush(_preferences.MutedForeground),
            FontSize = 10,
            Width = width,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Canvas.SetLeft(label, left);
        Canvas.SetTop(label, top);
        canvas.Children.Add(label);
    }

    private bool IsExecutionVisible(object item)
    {
        if (item is not ExecutionRow row)
        {
            return false;
        }
        if (_connectionFilter?.SelectedItem is ComboBoxItem connectionFilter &&
            connectionFilter.Tag?.ToString() is { } connectionTag &&
            connectionTag != "all" &&
            int.TryParse(connectionTag, CultureInfo.InvariantCulture, out var connectionId) &&
            row.ConnectionId != connectionId)
        {
            return false;
        }
        if (_periodFilter?.SelectedItem is ComboBoxItem periodFilter &&
            int.TryParse(periodFilter.Tag?.ToString(), CultureInfo.InvariantCulture, out var days) &&
            row.Execution.ExecutedAtUtc < DateTime.UtcNow.AddDays(-days))
        {
            return false;
        }
        var search = _searchBox?.Text.Trim();
        return string.IsNullOrEmpty(search) ||
               row.Symbol.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               row.MarketLabel.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               row.SideLabel.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               row.ConnectionName.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void FilterChanged(object sender, SelectionChangedEventArgs e)
    {
        _executionView?.Refresh();
        UpdateDashboardValues();
    }

    private void WidgetDrop(object sender, DragEventArgs e)
    {
        if (sender is not Border { Tag: string targetId } ||
            e.Data.GetData(typeof(string)) is not string sourceId ||
            sourceId == targetId)
        {
            return;
        }
        var widgets = _preferences.Widgets.OrderBy(item => item.OrderIndex).ToList();
        var source = widgets.FirstOrDefault(item => item.Id == sourceId);
        if (source is null || source.Pinned ||
            widgets.FirstOrDefault(item => item.Id == targetId)?.Pinned == true)
        {
            return;
        }
        var sourceIndex = widgets.FindIndex(item => item.Id == sourceId);
        var targetIndex = widgets.FindIndex(item => item.Id == targetId);
        if (sourceIndex < 0 || targetIndex < 0)
        {
            return;
        }
        var moving = widgets[sourceIndex];
        widgets.RemoveAt(sourceIndex);
        widgets.Insert(targetIndex, moving);
        for (var index = 0; index < widgets.Count; index++)
        {
            widgets[index].OrderIndex = index;
        }
        _preferences.Widgets = widgets;
        SaveAndApplyPreferences(_preferences);
    }

    private void MoveWidget(WidgetPreference widget, int delta)
    {
        if (widget.Pinned)
        {
            return;
        }

        var widgets = _preferences.Widgets
            .Where(item => item.Visible)
            .OrderBy(item => item.OrderIndex)
            .ToList();
        var index = widgets.IndexOf(widget);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= widgets.Count || widgets[target].Pinned)
        {
            return;
        }

        (widgets[index].OrderIndex, widgets[target].OrderIndex) =
            (widgets[target].OrderIndex, widgets[index].OrderIndex);
        SaveAndApplyPreferences(_preferences);
    }

    private void DashboardDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(string)) is not string sourceId)
        {
            return;
        }
        var widgets = _preferences.Widgets.OrderBy(item => item.OrderIndex).ToList();
        var moving = widgets.FirstOrDefault(item => item.Id == sourceId);
        if (moving is null)
        {
            return;
        }
        widgets.Remove(moving);
        widgets.Add(moving);
        for (var index = 0; index < widgets.Count; index++)
        {
            widgets[index].OrderIndex = index;
        }
        _preferences.Widgets = widgets;
        SaveAndApplyPreferences(_preferences);
    }

    private void DashboardDragOver(object sender, DragEventArgs e) =>
        e.Effects = e.Data.GetDataPresent(typeof(string)) ? DragDropEffects.Move : DragDropEffects.None;

    private void DragHandleMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed ||
            _widgetDragStart is not Point start ||
            sender is not FrameworkElement { Tag: string id })
        {
            return;
        }

        var current = e.GetPosition(DashboardWidgets);
        if (Math.Abs(current.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _widgetDragStart = null;
        DragDrop.DoDragDrop((DependencyObject)sender, id, DragDropEffects.Move);
    }

    private void SettingsClick(object sender, RoutedEventArgs e) => OpenSettings();

    private void OpenSettings()
    {
        var window = new DashboardSettingsWindow(
            _preferences,
            SaveAndApplyPreferences,
            ExecuteSettingsAction) { Owner = this };
        _settingsWindow = window;
        try
        {
            window.ShowDialog();
        }
        finally
        {
            _settingsWindow = null;
        }
    }

    private void ExecuteSettingsAction(string action)
    {
        switch (action)
        {
            case "connect":
                AddConnectionClick(this, new RoutedEventArgs());
                break;
            case "refresh":
                RefreshClick(this, new RoutedEventArgs());
                break;
            case "export":
                ExportClick(this, new RoutedEventArgs());
                break;
            case "backup":
                BackupClick(this, new RoutedEventArgs());
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, "Неизвестное действие настроек.");
        }
    }

    private async void AddConnectionClick(object sender, RoutedEventArgs e)
    {
        var dialog = new AddConnectionWindow(_database) { Owner = this };
        dialog.ApplyTheme(_preferences);
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
            Title = "Резервная копия журнала и вложений",
            Filter = "Архив ZIP (*.zip)|*.zip",
            FileName = $"dnevnik-sdelok-backup-{DateTime.Now:yyyy-MM-dd-HHmm}.zip",
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
                "Резервная копия базы и скриншотов сохранена в ZIP. Для восстановления закройте программу и распакуйте архив в папку данных приложения. " +
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
            _connectionFilter?.SelectedItem is ComboBoxItem item && item.Tag?.ToString() != "all"
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void RemoveConnectionClick(object sender, RoutedEventArgs e)
    {
        if (_connectionFilter?.SelectedItem is not ComboBoxItem item ||
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

    private void OpenSymbolChart(ExecutionRow row)
    {
        var grid = _tradesGrid;
        if (grid is null)
        {
            StatusText.Text = "Не удалось открыть сделку: таблица ещё не готова.";
            return;
        }

        if (_expandedExecution == row)
        {
            CollapseExpandedTrade();
            return;
        }

        CollapseExpandedTrade();

        _expandedExecution = row;
        StatusText.Text = $"Открываю сделку {row.Symbol}…";
        grid.SelectedItem = row;
        grid.ScrollIntoView(row);
        if (_tradesWidgetCard is not null)
        {
            _tradesCardHeightBeforeExpansion = _tradesWidgetCard.Height;
            _tradesWidgetCard.Height = Math.Max(_tradesWidgetCard.Height + 680, 1180);
        }

        var details = _expandedTradeDetails;
        if (details is null)
        {
            StatusText.Text = $"Не удалось открыть карточку {row.Symbol}: панель сделки не создана.";
            return;
        }

        var relatedExecutions = _rows
            .Where(item => item.ConnectionId == row.ConnectionId &&
                           item.Symbol.Equals(row.Symbol, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Execution.ExecutedAtUtc)
            .ToList();
        details.Width = Math.Max(820, (_tradesWidgetCard?.ActualWidth ?? grid.ActualWidth) - 32);
        details.Initialize(row, relatedExecutions, _database, _preferences);
        details.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            details.BringIntoView();
            StatusText.Text = $"Открыта сделка {row.Symbol}. Нажмите Escape или тикер, чтобы свернуть.";
        }), DispatcherPriority.Loaded);
    }

    private void TradesGridPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        Dispatcher.BeginInvoke(PersistColumnLayout, DispatcherPriority.Background);
    }

    private void WindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _expandedExecution is not null)
        {
            CollapseExpandedTrade();
            e.Handled = true;
        }
    }

    private void CollapseExpandedTrade()
    {
        var expanded = _expandedExecution;
        if (expanded is null)
        {
            return;
        }

        if (_tradesGrid is not null)
        {
            _tradesGrid.SelectedItem = null;
        }
        _expandedExecution = null;
        if (_expandedTradeDetails is not null)
        {
            _expandedTradeDetails.Visibility = Visibility.Collapsed;
            _expandedTradeDetails.Release();
        }
        if (_tradesWidgetCard is not null && _tradesCardHeightBeforeExpansion is double originalHeight)
        {
            _tradesWidgetCard.Height = originalHeight;
        }
        _tradesCardHeightBeforeExpansion = null;
    }

    private void UpdateExpandedDetailsWidth()
    {
        if (_expandedTradeDetails is null || _tradesWidgetCard is null ||
            _tradesWidgetCard.ActualWidth <= 0)
        {
            return;
        }

        _expandedTradeDetails.Width = Math.Max(820, _tradesWidgetCard.ActualWidth - 32);
    }

    private void CollapseTradeDetails(ExecutionRow row)
    {
        if (_expandedExecution == row)
            CollapseExpandedTrade();
    }

    private static string Blend(string first, string second, double amount)
    {
        var a = (Color)ColorConverter.ConvertFromString(first);
        var b = (Color)ColorConverter.ConvertFromString(second);
        return $"#{(byte)(a.R * (1 - amount) + b.R * amount):X2}" +
               $"{(byte)(a.G * (1 - amount) + b.G * amount):X2}" +
               $"{(byte)(a.B * (1 - amount) + b.B * amount):X2}";
    }

    private static DateTime Max(DateTime first, DateTime second) =>
        first >= second ? first : second;

    private static SolidColorBrush Brush(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));
}

internal static class FrameworkElementExtensions
{
    public static FrameworkElement Also(this FrameworkElement element, Action<FrameworkElement> action)
    {
        action(element);
        return element;
    }
}
